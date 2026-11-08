#!/usr/bin/env python3
"""Dispatch administrator-requested ads. Existing timed publishers keep their schedules.

SQL catalogue access is read-only. Queue leases/results use the Admin-authenticated
local API; each non-idempotent Telegram action has its own durable send journal.
"""
import argparse
import datetime as dt
import fcntl
import hashlib
import html
import importlib.util
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import sqlite3
import tempfile
import time
import threading
import urllib.request
import uuid

REPO = Path('/opt/zibashe/app/ZibasheERP')
STATE = Path.home() / '.local/state/zibashe-admin-promotions'


class RetryLater(Exception):
    pass


class UnknownDelivery(Exception):
    pass


class Unavailable(Exception):
    pass


class NotPromotionManager(Exception):
    pass


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, REPO / 'deploy' / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def save(path, value):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix='.journal-', delete=False, mode='w') as stream:
        name = stream.name
        json.dump(value, stream, ensure_ascii=False)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(name, 0o600)
    os.replace(name, path)
    descriptor = os.open(path.parent, os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def stage(root, name, action):
    receipt, intent = root / (name + '-receipt.json'), root / (name + '-intent.json')
    if receipt.exists():
        return json.loads(receipt.read_text())
    if intent.exists():
        raise UnknownDelivery()
    save(intent, {'started_at': time.time()})
    try:
        value = action()
    except Exception as error:
        raise UnknownDelivery() from error
    save(receipt, value)
    return value


def validate_payload(value):
    if (value.get('Destination') not in ('group', 'channel', 'channel-delete', 'group-message', 'flash-start', 'flash-end', 'inventory-publish', 'inventory-report') or type(value.get('Code')) is not int
            or (value['Code'] <= 0 and value.get('Destination') not in ('channel-delete', 'group-message', 'flash-end')) or value.get('HoldHours') != 24
            or type(value.get('AdminChatId')) is not int or type(value.get('AdminUserId')) is not int
            or value['AdminUserId'] <= 0):
        raise Unavailable()
    try:
        uuid.UUID(value['SalesListId'])
    except (ValueError, KeyError, TypeError, AttributeError):
        raise Unavailable() from None
    if value['Destination'] in ('channel-delete', 'group-message', 'flash-end') and (value['Code'] != 0 or uuid.UUID(value['SalesListId']).int != 0):
        raise Unavailable()
    custom = value.get('CustomText')
    if custom is not None and (value.get('Destination') not in ('group','channel') or not isinstance(custom,str) or
            not custom.strip() or len(custom.encode('utf-16-le')) // 2 > (700 if value['Destination']=='channel' else 3500)):
        raise Unavailable()
    if value['Destination'] == 'group-message':
        text = value.get('MessageText')
        if not isinstance(text, str) or not text.strip() or len(text.encode('utf-16-le')) // 2 > 4096:
            raise Unavailable()
    if value.get('ChannelMode', 'replace') not in ('replace', 'append'):
        raise Unavailable()
    if value['Destination'] == 'inventory-report':
        try: uuid.UUID(value['InvoiceBatchId'])
        except (ValueError, KeyError, TypeError, AttributeError): raise Unavailable() from None
    if value['Destination'] in ('flash-start', 'flash-end'):
        settings = value.get('FlashSale')
        if not isinstance(settings, dict):
            raise Unavailable()
        try:
            if uuid.UUID(settings['CampaignId']).int == 0:
                raise ValueError()
        except (ValueError, KeyError, TypeError, AttributeError):
            raise Unavailable() from None
        if value['Destination'] == 'flash-start':
            amount, percent = settings.get('DiscountAmountPerMl'), settings.get('DiscountPercent')
            if (type(settings.get('DurationMinutes')) is not int or not 1 <= settings['DurationMinutes'] <= 10080 or
                    (amount is None and (type(percent) is not int or not 1 <= percent <= 99)) or
                    (amount is not None and (percent != 0 or type(amount) not in (int, float) or amount <= 0)) or
                    type(settings.get('OriginalPricePerMl')) not in (int, float) or settings['OriginalPricePerMl'] <= 0):
                raise Unavailable()
    return value


def row_for(marketing, payload):
    rows = marketing.query(marketing.config('deploy-sqlserver-1'), test_code=payload['Code'])
    if (len(rows) != 1 or rows[0]['Id'].lower() != payload['SalesListId'].lower()
            or not marketing.eligible(rows[0], maximum=2**31, exclusions={'codes': set(), 'list_ids': set()})):
        raise Unavailable()
    return rows[0]


def history(marketing, plan, status, message_id=None):
    with sqlite3.connect(marketing.STATE / 'history.sqlite', timeout=30) as db:
        db.execute('INSERT OR IGNORE INTO slots(slot,status,list_id,at,message_id,error,perfume_key) '
                   'VALUES(?,?,?,?,?,?,?)', (plan['slot'], status, plan['list_id'], plan['at'], message_id, None, plan['perfume_key']))
        db.execute('UPDATE slots SET status=?,message_id=? WHERE slot=?', (status, message_id, plan['slot']))
        db.commit()


def group(root, job_id, payload, marketing, cfg):
    # Accepted text is sufficient evidence after a crash, even if the list closed meanwhile.
    if (root / 'text-receipt.json').exists():
        receipt = json.loads((root / 'text-receipt.json').read_text())
        plan = json.loads((root / 'plan.json').read_text())
        history(marketing, plan, 'sent', receipt['message_id'])
        return {'State': 'sent', 'Url': 'https://t.me/c/' + marketing.CHAT[4:] + '/' + str(receipt['message_id'])}
    # An interrupted HTTP call must never be blindly repeated.
    for name in ('album', 'text'):
        if (root / (name + '-intent.json')).exists() and not (root / (name + '-receipt.json')).exists():
            raise UnknownDelivery()
    row = row_for(marketing, payload)
    if marketing.excluded(row):
        raise Unavailable()
    key = marketing.perfume_frequency_key(row)
    custom = payload.get('CustomText')
    content = None
    if custom is None:
        with sqlite3.connect(marketing.STATE / 'history.sqlite', timeout=30) as db:
            cached = db.execute('SELECT content FROM cache WHERE list_id IN (?,?) AND at>? ORDER BY at DESC LIMIT 1',
                                (row['Id'], 'perfume:' + key, time.time() - 30 * 86400)).fetchone()
        content_path = root / 'content.json'
        if content_path.exists():
            content = json.loads(content_path.read_text())
        else:
            content = json.loads(cached[0]) if cached else marketing.research(cfg, row)
            marketing.validate_content(content)
            save(content_path, content)
        marketing.validate_content(content)
    images = marketing.image_provider()
    paths, _ = images.prepare(cfg, row, marketing.STATE, marketing.post)
    if len(paths) not in (2, 3):
        raise RetryLater()
    # These are existing cache-verified exact-product images, not arbitrary brand images.
    with (marketing.STATE / 'run.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RetryLater() from None
        row = row_for(marketing, payload)
        if marketing.excluded(row):
            raise Unavailable()
        images_key = images.perfume_cache_key(row)
        checked = images.cached_images(marketing.STATE, marketing.STATE / ('perfume-images-' + images_key + '.json'), row, images_key, {8, 9})
        if not checked or len(checked[0]) not in (2, 3):
            raise RetryLater()
        if marketing.CHAT not in cfg.get('Telegram__SilentGroupChatIds', '').split(','):
            raise Unavailable()
        url = 'https://api.telegram.org/bot' + cfg['Telegram__BotToken'] + '/'
        chat = marketing.post(url + 'getChat', {'chat_id': marketing.CHAT}, timeout=20)
        if not chat.get('ok') or str(chat['result']['id']) != marketing.CHAT or chat['result']['type'] != 'supergroup':
            raise Unavailable()
        plan = {'slot': -int(uuid.UUID(job_id).hex[:15], 16) - 10_000, 'list_id': row['Id'], 'at': time.time(), 'perfume_key': key}
        if (root / 'plan.json').exists():
            plan = json.loads((root / 'plan.json').read_text())
        else:
            save(root / 'plan.json', plan)
        history(marketing, plan, 'sending')
        album = stage(root, 'album', lambda: {'ids': images.send_album(cfg, marketing.CHAT, checked[0])})
        if len(album.get('ids', [])) not in (2, 3):
            raise UnknownDelivery()
        if custom is None:
            text = marketing.caption(row, content)
            measured = text
        else:
            measured = (custom + '\n\n📋 کد لیست: ' + str(payload['Code']) +
                        '\n📦 باقی‌مانده: ' + format(row['TotalVolume']-row['ReservedVolume'], 'g') + ' میل')
            text = html.escape(measured)
        if len(measured.encode('utf-16-le')) // 2 > 4096:
            raise Unavailable()
        source = 'https://t.me/c/' + row['TelegramChannelId'][4:] + '/' + str(row['TelegramMessageId'])

        def send_text():
            result = marketing.post(url + 'sendMessage', {
                'chat_id': marketing.CHAT, 'text': text, 'parse_mode': 'HTML', 'disable_notification': True,
                'link_preview_options': {'is_disabled': True}, 'reply_parameters': {'message_id': album['ids'][0]},
                'reply_markup': {'inline_keyboard': [[{'text': '🧴 مشاهدهٔ لیست و ثبت سفارش', 'url': source}]]}}, timeout=45)
            if (not result.get('ok') or str(result['result']['chat']['id']) != marketing.CHAT
                    or type(result['result'].get('message_id')) is not int):
                raise UnknownDelivery()
            return {'message_id': result['result']['message_id']}

        receipt = stage(root, 'text', send_text)
        history(marketing, plan, 'sent', receipt['message_id'])
        return {'State': 'sent', 'Url': 'https://t.me/c/' + marketing.CHAT[4:] + '/' + str(receipt['message_id'])}


def channel(root, job_id, payload, marketing, cfg):
    publisher = module('admin_channel_promotion', 'channel-remaining-promotion.py')
    store = publisher.Store(publisher.STATE_DIR)
    with (store.directory / 'run.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RetryLater() from None
        store = publisher.Store(publisher.STATE_DIR)
        if payload['Destination'] == 'channel-delete':
            engine = publisher.Engine(store, publisher.Catalogue(), publisher.Telegram(cfg['Telegram__BotToken']))
            result = engine.pause(time.time(), job_id, requested_at=payload.get('_requested_at'))
            if result['status'] == 'pause_superseded':
                return {'State': 'failed', 'Error': 'پس از این درخواست، تبلیغ جدیدتری انتخاب شده است؛ درخواست حذف قدیمی اجرا نشد.'}
            if result['status'] not in ('paused', 'pause_already_completed'):
                raise RetryLater()
            if result['outcome'] == 'expired_message_archived':
                return {'State': 'failed', 'Error': 'تلگرام حذف پیام قدیمی را نپذیرفت؛ تبلیغ پایان داده شد و دکمهٔ خریدش برداشته شد. تبلیغ خودکار کانال متوقف است.'}
            return {'State': 'sent'}
        ad = next((value for value in publisher.active_ads(store.data) if value.get('request_id') == job_id and value.get('message_id')), None)
        if ad:
            return {'State': 'sent', 'Url': 'https://t.me/' + publisher.USERNAME + '/' + str(ad['message_id'])}
        prior = next((v for v in publisher.retired_ads(store.data) if v.get('request_id') == job_id), None)
        if prior:
            return {'State': 'sent', 'Url': 'https://t.me/' + publisher.USERNAME + '/' + str(prior['message_id'])}
        if store.data.get('paused') and payload.get('_requested_at', time.time()) <= store.data.get('paused_at', 0):
            return {'State': 'failed', 'Error': 'این درخواست قدیمی‌تر از حذف تبلیغ کانال است؛ برای شروع دوباره کد لیست را مجدداً وارد کنید.'}
        submitted = payload.get('_requested_at', time.time())
        if (submitted < store.data.get('last_replace_at', 0) or
                (payload.get('ChannelMode', 'replace') == 'replace' and submitted < store.data.get('last_manual_selection_at', 0))):
            return {'State': 'failed', 'Error': 'درخواست جدیدتری برای تبلیغ کانال ثبت شده است؛ انتخاب قبلی جایگزین آن نمی‌شود.'}
        row = row_for(marketing, payload)
        plan_path = root / 'channel-plan.json'
        if not plan_path.exists():
            save(plan_path, {'expires_at': time.time() + payload['HoldHours'] * 3600,
                             'requested_at': payload.get('_requested_at', time.time())})
        plan = json.loads(plan_path.read_text())
        if plan['expires_at'] <= time.time():
            raise Unavailable()
        store.data['selection'] = {'list_id': row['Id'], 'manual': True, 'code': payload['Code'],
                                   'requested_at': plan.get('requested_at', time.time()), 'expires_at': plan['expires_at'], 'request_id': job_id,
                                   'mode': payload.get('ChannelMode', 'replace'), 'custom_text': payload.get('CustomText')}
        store.save()
        result = publisher.Engine(store, publisher.Catalogue(), publisher.Telegram(cfg['Telegram__BotToken'])).tick(time.time())
        ad = next((value for value in publisher.active_ads(store.data) if value.get('request_id') == job_id), None)
        if result['status'] in ('sent', 'monitoring', 'updated') and ad and ad.get('request_id') == job_id:
            return {'State': 'sent', 'Url': 'https://t.me/' + publisher.USERNAME + '/' + str(ad['message_id'])}
        raise RetryLater()


def group_message(root, payload, marketing, cfg):
    if str(marketing.CHAT) != '-1001283018740':
        raise Unavailable()
    url = 'https://api.telegram.org/bot' + cfg['Telegram__BotToken'] + '/'
    destination = marketing.post(url + 'getChat', {'chat_id': marketing.CHAT})
    chat = destination.get('result', {})
    if not destination.get('ok') or chat.get('id') != -1001283018740 or chat.get('type') not in ('group', 'supergroup'):
        raise Unavailable()
    sent = stage(root, 'group-message', lambda: marketing.post(url + 'sendMessage',
        {'chat_id': marketing.CHAT, 'text': payload['MessageText']}))
    message = sent.get('result', {})
    if not sent.get('ok') or message.get('chat', {}).get('id') != -1001283018740 or type(message.get('message_id')) is not int:
        raise UnknownDelivery()
    return {'State': 'sent', 'Url': 'https://t.me/c/1283018740/' + str(message['message_id'])}


def inventory_api(cfg, route, value=None):
    req = urllib.request.Request('http://127.0.0.1:8080/api/inventory-offers/' + route,
        data=None if value is None else json.dumps(value).encode(),
        headers={'X-Api-Key': cfg['ApiKeys__Admin'], 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=25) as response:
        body = response.read()
        return json.loads(body) if body else None


def inventory_job(root, payload, marketing, cfg):
    report = payload['Destination'] == 'inventory-report'
    route = str(uuid.UUID(payload['SalesListId']))
    suffix = '?batchId=' + str(uuid.UUID(payload['InvoiceBatchId'])) if report else ''
    info = inventory_api(cfg, route + '/publication' + suffix)
    target = (cfg.get('Telegram__NewPaymentsChatId', '') if report else cfg.get('Telegram__ReadyInventoryChannelId', '@zibasheperfume_mojodi')).strip()
    if (not target or str(info.get('chatId', '')).strip() != target or info.get('code') != payload['Code'] or
            str(info.get('id', '')).lower() != route):
        raise Unavailable()
    url = 'https://api.telegram.org/bot' + cfg['Telegram__BotToken'] + '/'
    chat = marketing.post(url + 'getChat', {'chat_id': target}).get('result', {})
    if chat.get('type') not in ('group', 'supergroup', 'channel') or (
            str(chat.get('id')) != target and '@' + chat.get('username', '').lower() != target.lower()):
        raise Unavailable()
    if info.get('messageId'):
        return {'State': 'sent'}
    destination = chat['id']
    def send(name, method, data):
        result = stage(root, name, lambda: marketing.post(url + method, {'chat_id': destination, **data}))
        receipt = result.get('result', {})
        if not result.get('ok') or receipt.get('chat', {}).get('id') != destination or type(receipt.get('message_id')) is not int:
            raise UnknownDelivery()
        return receipt['message_id']
    photo_id = info.get('photoMessageId')
    if report:
        if info.get('photo') and not photo_id:
            photo_id = send('inventory-report-photo', 'sendPhoto', {'photo': info['photo'], 'caption': '💳 واریز جدید — موجودی ' + str(payload['Code'])})
        message_id = send('inventory-report-text', 'sendMessage', {'text': info['text']})
    else:
        if not info.get('photo') or not info.get('callback'):
            raise Unavailable()
        message_id = send('inventory-publication', 'sendPhoto', {'photo': info['photo'], 'caption': info['text'], 'parse_mode': 'HTML',
            'reply_markup': {'inline_keyboard': [[{'text': '🛍 درخواست خرید', 'callback_data': info['callback']}]]}})
    inventory_api(cfg, route + '/receipt', {'messageId': message_id, 'photoMessageId': photo_id, 'batchId': payload.get('InvoiceBatchId') if report else None})
    return {'State': 'sent'}


def flash_job(root, job_id, payload, cfg):
    flash = module('hourly_promotion', 'flash-sale-promotion.py')
    settings = payload['FlashSale'];campaign_id = str(uuid.UUID(settings['CampaignId']))
    flash.STATE.mkdir(mode=0o700, parents=True, exist_ok=True)
    with (flash.STATE / 'run.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RetryLater() from None
        transport = flash.Transport()
        directory = flash.STATE / campaign_id
        if payload['Destination'] == 'flash-start':
            save(directory / 'automatic-lifecycle.json', {'approved': True, 'manager_id': payload['AdminUserId'],
                'code': payload['Code'], 'request_id': job_id})
            try:
                offer = transport.api('', {'requestId': campaign_id, 'salesListId': payload['SalesListId'],
                    'durationMinutes': settings['DurationMinutes'], 'discountPercent': settings['DiscountPercent'],
                    'discountAmountPerMl': settings.get('DiscountAmountPerMl'), 'actorUserId': payload['AdminUserId'],
                    'expectedOriginalPricePerMl': settings['OriginalPricePerMl']})
            except flash.Rejected as error:
                if error.code in (400, 403, 404, 409):
                    (directory / 'automatic-lifecycle.json').unlink(missing_ok=True)
                    raise Unavailable() from None
                raise
            if offer['code'] != payload['Code'] or offer['salesListId'].lower() != payload['SalesListId'].lower():
                raise Unavailable()
            try:
                group_receipt = flash.publish(transport, directory, campaign_id)
                flash.publish(transport, directory / 'channel', campaign_id, destination=flash.CHANNEL)
            except RuntimeError:
                if any((target / 'send-intent.json').exists() and not (target / 'send-receipt.json').exists()
                       for target in (directory, directory / 'channel')):
                    raise UnknownDelivery() from None
                raise
            return {'State': 'sent', 'Url': 'https://t.me/c/1283018740/' + str(group_receipt['message_id'])}
        try:
            transport.api(campaign_id + '/end', {'actorUserId': payload['AdminUserId']})
            flash.end_thanks(transport, directory, campaign_id)
        except flash.UnknownAnnouncement:
            raise UnknownDelivery() from None
        return {'State': 'sent'}


def process(job, marketing, cfg):
    job_id = str(uuid.UUID(job['id']))
    root = STATE / job_id
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    outcome = root / 'outcome.json'
    if outcome.exists():
        return json.loads(outcome.read_text())
    raw_payload = {}
    try:
        raw_payload = json.loads(job['payload'])
        payload = validate_payload(raw_payload)
        if payload['Destination'] in ('group-message', 'flash-start', 'flash-end'):
            try:
                allowed = int(cfg.get('Telegram__OwnerUserId', '0')) > 0 and int(cfg['Telegram__OwnerUserId']) == payload['AdminUserId']
            except (ValueError, KeyError):
                allowed = False
            if not allowed:
                raise NotPromotionManager()
        if job.get('createdAt'):
            submitted = dt.datetime.fromisoformat(job['createdAt'].replace('Z', '+00:00'))
            if submitted.tzinfo is None: submitted = submitted.replace(tzinfo=dt.timezone.utc)
            payload['_requested_at'] = submitted.timestamp()
        result = (inventory_job(root, payload, marketing, cfg) if payload['Destination'] in ('inventory-publish', 'inventory-report') else
                  flash_job(root, job_id, payload, cfg) if payload['Destination'] in ('flash-start', 'flash-end') else
                  group_message(root, payload, marketing, cfg) if payload['Destination'] == 'group-message' else
                  (group if payload['Destination'] == 'group' else channel)(root, job_id, payload, marketing, cfg))
    except NotPromotionManager:
        result = {'State': 'failed', 'Error': 'این گزینه فقط برای مدیر @zahraa_frj فعال است.'}
    except UnknownDelivery:
        if (root / 'plan.json').exists():
            history(marketing, json.loads((root / 'plan.json').read_text()), 'unknown')
        result = {'State': 'unknown'}
    except Unavailable:
        error = ('متن پیام یا گروه مقصد معتبر نیست؛ متن و دسترسی ربات به گروه را بررسی کنید.'
                 if isinstance(raw_payload, dict) and raw_payload.get('Destination') == 'group-message' else
                 'پروموشن اجرا نشد؛ قیمت، موجودی یا شرایط لیست تغییر کرده است. دوباره پیش‌نمایش را بررسی و تأیید کنید.'
                 if isinstance(raw_payload, dict) and raw_payload.get('Destination') in ('flash-start','flash-end') else
                 'لیست تکمیل شده، عکس ندارد یا برای این مقصد قابل تبلیغ نیست؛ کد و وضعیت لیست را بررسی کنید.')
        result = {'State': 'failed', 'Error': error}
    except Exception:
        # No exception messages: remote URLs can contain credentials.
        return {'State': 'retry', 'Error': 'آماده‌سازی تبلیغ موقتاً انجام نشد؛ تلاش مجدد خودکار انجام می‌شود.'}
    save(outcome, result)
    return result


def api(cfg, path, value=None):
    request = urllib.request.Request('http://127.0.0.1:8080/api/marketing-promotions/' + path,
        data=None if value is None else json.dumps(value).encode(),
        headers={'X-Api-Key': cfg['ApiKeys__Admin'], 'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=25) as response:
        data = response.read()
    return json.loads(data) if data else None


def dispatch_once(marketing, cfg, logger, lane):
    """Publication and reports claim disjoint, leased rows; they cannot block each other."""
    try:
        for job in api(cfg, 'pending?lane=' + lane):
            result = process(job, marketing, cfg)
            api(cfg, str(uuid.UUID(job['id'])) + '/result', result)
            record = json.dumps({'job_id': job['id'], 'state': result['State'], 'lane': lane})
            logger.info(record)
            print(record, flush=True)
    except Exception as error:
        record = json.dumps({'state': 'worker_retry_later', 'error_type': type(error).__name__, 'lane': lane})
        logger.warning(record)
        print(record, flush=True)


def inventory_loop(marketing, cfg, logger, stop, once=False):
    logger.info('Independent inventory publication lane started')
    while not stop.is_set():
        dispatch_once(marketing, cfg, logger, 'inventory')
        if once or stop.wait(3):
            return


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-seconds', type=int, default=55)
    parser.add_argument('--once', action='store_true')
    args = parser.parse_args()
    os.umask(0o077)
    STATE.mkdir(mode=0o700, parents=True, exist_ok=True)
    logger = logging.getLogger('admin-promotions')
    logger.setLevel(logging.INFO)
    logger.addHandler(RotatingFileHandler(STATE / 'runner.log', maxBytes=2_000_000, backupCount=3))
    with (STATE / 'worker.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return 0
        marketing = module('admin_marketing_publisher', 'marketing-publisher.py')
        cfg = marketing.config('deploy-zibashe-api-1')
        logger.info('Admin advertising dispatcher started')
        deadline = time.monotonic() + max(1, min(args.run_seconds, 60))
        stop = threading.Event()
        publisher = threading.Thread(target=inventory_loop, args=(marketing, cfg, logger, stop, args.once))
        publisher.start()
        try:
            while True:
                dispatch_once(marketing, cfg, logger, 'regular')
                if args.once or time.monotonic() >= deadline:
                    break
                time.sleep(3)
        finally:
            stop.set()
            publisher.join()  # Keep the process lock until any journaled send finishes.
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
