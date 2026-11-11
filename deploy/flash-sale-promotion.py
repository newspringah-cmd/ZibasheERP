#!/usr/bin/env python3
"""Publish approved hourly offers, refresh captions, and remind the group every 15 minutes.

The API owns exact-time pricing and the permanent invoice snapshot. This worker
never changes list prices, orders, invoices, or schedules for other publishers.
"""
import argparse
import datetime as dt
from decimal import Decimal
import fcntl
import importlib.util
import json
import math
import os
import re
from pathlib import Path
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from zoneinfo import ZoneInfo

REPO = Path('/opt/zibashe/app/ZibasheERP')
STATE = Path.home() / '.local/state/zibashe-flash-sales'
GROUP = -1001283018740
CHANNEL = -1001236168661
PERSIAN = str.maketrans('0123456789', '۰۱۲۳۴۵۶۷۸۹')
TEHRAN = ZoneInfo('Asia/Tehran')
REPOST_INTERVAL = 15 * 60


def timestamp(value):
    parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    return (parsed if parsed.tzinfo else parsed.replace(tzinfo=dt.timezone.utc)).timestamp()


def caption(offer, now):
    if offer.get('caption'):
        text = offer['caption']
        if offer['isActive'] and now < timestamp(offer['endsAtUtc']):
            line = remaining_caption(offer) + '\n'
            return text.replace('⏳', line + '⏳', 1) if '⏳' in text else text + '\n' + line.rstrip()
        return text
    if not offer['isActive'] or now >= timestamp(offer['endsAtUtc']):
        reason = 'ظرفیت لیست تکمیل شد' if offer['remainingVolume'] <= 0 else 'مهلت دو ساعته به پایان رسید'
        return (f'⏰ <b>{reason}</b>\n\n'
                'پیشنهاد ۵۰٪ تخفیف می آمور XOXO پایان یافته است.\n'
                'تخفیف آیتم‌هایی که در مهلت مقرر ثبت نهایی شدند، در فاکتورشان محفوظ است 🤍\n\n'
                f'👇 <a href="{offer["listUrl"]}">مشاهدهٔ لیست ۴۵۵۹</a>')
    minutes = max(0, math.ceil((timestamp(offer['endsAtUtc']) - now) / 60))
    timer = f'{minutes // 60:02d}:{minutes % 60:02d}'.translate(PERSIAN)
    price = format(Decimal(str(offer['discountedPricePerMl'])), ',.2f').rstrip('0').rstrip('.')
    price = price.replace(',', '٬').replace('.', '٫').translate(PERSIAN)
    return ('⚡️ <b>دو ساعت فرصت خرید عطر با ۵۰٪ تخفیف!</b>\n\n'
            'زیباشی‌های عزیزم 🌸\n'
            'این بار نوبت <b>می آمور XOXO</b> شده؛ یه رایحهٔ میوه‌ای و وانیلی شیرین، '
            'با حال‌وهوای رزبری، وانیل و شکر 🍓🤍\n\n'
            '🎁 <b>۵۰٪ تخفیف روی مبلغ عطر</b>\n'
            f'💰 <b>قیمت هر میل با تخفیف: {price} تومان</b>\n\n'
            f'{remaining_caption(offer)}\n'
            f'⏳ <b>زمان باقی‌مانده: {timer}</b>\n'
            'برای استفاده از تخفیف، ثبت نهایی آیتم باید قبل از پایان مهلت انجام بشه.\n\n'
            'اگه رایحه‌های شیرین و میوه‌ای رو دوست داری، این فرصت مخصوص توئه ✨\n\n'
            f'👇 <a href="{offer["listUrl"]}">مشاهدهٔ لیست ۴۵۵۹ و ثبت خرید</a>')


def remaining_caption(offer):
    amount = format(Decimal(str(offer['remainingVolume'])), 'f')
    if '.' in amount:
        amount = amount.rstrip('0').rstrip('.')
    return '📦 <b>باقی‌مانده: ' + amount.replace('.', '٫').translate(PERSIAN) + ' میل</b>'


def valid_source(offer):
    link = offer.get('listUrl', '')
    if not offer.get('isInventoryOffer'):
        return bool(re.fullmatch(r'https://t\.me/zibasheperfume/[1-9][0-9]*', link))
    chat = str(offer.get('listChatId', ''))
    if re.fullmatch(r'-100[0-9]+', chat):
        return bool(re.fullmatch(r'https://t\.me/c/' + re.escape(chat[4:]) + r'/[1-9][0-9]*', link))
    if re.fullmatch(r'@[A-Za-z0-9_]+', chat):
        return bool(re.fullmatch(r'https://t\.me/' + re.escape(chat[1:]) + r'/[1-9][0-9]*', link))
    return False


def markup(offer, now):
    if not valid_source(offer):
        return {'inline_keyboard': []}
    active = offer['isActive'] and now < timestamp(offer['endsAtUtc'])
    return {'inline_keyboard': [[{'text': '🛍 مشاهدهٔ لیست و ثبت خرید' if active else '📋 مشاهدهٔ لیست',
                                 'url': offer['listUrl']}]]}


def save(path, value):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix='.flash-', mode='w', delete=False) as stream:
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


class Rejected(Exception):
    def __init__(self, code, unchanged=False, missing=False, retry_after=60):
        self.code = code
        self.unchanged = unchanged
        self.missing = missing
        self.retry_after = max(60, int(retry_after))
        super().__init__(f'Request rejected ({code})')


class Transport:
    def __init__(self):
        spec = importlib.util.spec_from_file_location('marketing', REPO / 'deploy/marketing-publisher.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.cfg = module.config('deploy-zibashe-api-1')

    def request(self, url, data=None, headers=None):
        request = urllib.request.Request(url, data=json.dumps(data).encode() if data is not None else None,
                                         headers={'Content-Type': 'application/json', **(headers or {})})
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                body = response.read()
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            # HTTP rejection is an acknowledged failure, unlike an unknown send timeout.
            unchanged = missing = False
            retry_after = 60
            try:
                body = json.loads(error.read(4096))
                detail = body.get('description', '')
                retry_after = body.get('parameters', {}).get('retry_after', 60)
                unchanged = error.code == 400 and 'message is not modified' in detail.lower()
                missing = error.code == 400 and any(value in detail.lower() for value in ('message_id_invalid', 'message to edit not found'))
            except Exception:
                pass
            raise Rejected(error.code, unchanged, missing, retry_after) from None
        except Exception:
            raise RuntimeError('Remote response unknown') from None

    def api(self, route, data=None):
        return self.request('http://127.0.0.1:8080/api/flash-sales/' + route, data,
                            {'X-Api-Key': self.cfg['ApiKeys__Admin']})

    def telegram(self, method, payload):
        result = self.request('https://api.telegram.org/bot' + self.cfg['Telegram__BotToken'] + '/' + method, payload)
        if not result.get('ok'):
            raise Rejected(result.get('error_code', 0), retry_after=result.get('parameters', {}).get('retry_after', 60))
        return result['result']


def validate(offer, campaign_id=None, require_media=True):
    if (type(offer['code']) is not int or offer['code'] <= 0 or offer['groupChatId'] != GROUP or
            (require_media and (not valid_source(offer) or
            not offer['telegramPhotoFileId'])) or not 0 < offer['discountedPricePerMl'] < offer['originalPricePerMl']):
        raise ValueError('Approved offer identity mismatch')
    uuid.UUID(offer['salesListId'])
    if campaign_id is not None and str(uuid.UUID(offer['id'])) != str(uuid.UUID(campaign_id)):
        raise ValueError('Campaign identity mismatch')


def publish(transport, root, campaign_id, now=time.time, destination=GROUP):
    if destination not in (GROUP, CHANNEL):
        raise ValueError('Unapproved destination')
    offer = transport.api(campaign_id)
    validate(offer, campaign_id)
    receipt, intent = root / 'send-receipt.json', root / 'send-intent.json'
    if receipt.exists():
        sent = json.loads(receipt.read_text())
    elif destination == GROUP and offer.get('telegramMessageId'):
        sent = {'message_id': offer['telegramMessageId'], 'chat': {'id': destination}}
        save(receipt, sent)
    else:
        if intent.exists():
            raise RuntimeError('Send outcome unknown; inspect the group instead of posting a duplicate')
        if not offer['isActive'] or now() >= timestamp(offer['endsAtUtc']):
            raise ValueError('Offer is no longer active')
        chat = transport.telegram('getChat', {'chat_id': destination})
        valid_type = chat.get('type') in ('supergroup', 'group') if destination == GROUP else (
            chat.get('type') == 'channel' and chat.get('username', '').lower() == 'zibasheperfume')
        if chat.get('id') != destination or not valid_type:
            raise ValueError('Destination identity mismatch')
        text = caption(offer, now())
        if len(text.encode('utf-16-le')) // 2 > 1024:
            raise ValueError('Approved caption exceeds Telegram limit')
        save(intent, {'campaign_id': campaign_id, 'at': now()})
        try:
            sent = transport.telegram('sendPhoto', {'chat_id': destination, 'photo': offer['telegramPhotoFileId'],
                'caption': text, 'parse_mode': 'HTML', 'reply_markup': markup(offer, now())})
        except Rejected as error:
            save(root / 'send-rejected.json', {'http_status': error.code, 'at': now()})
            intent.unlink()
            raise
        if sent.get('chat', {}).get('id') != destination or type(sent.get('message_id')) is not int:
            raise RuntimeError('Unexpected publication receipt; manual review required')
        sent = {'message_id': sent['message_id'], 'chat': {'id': destination}}
        save(receipt, sent)
    if sent.get('chat', {}).get('id') != destination or type(sent.get('message_id')) is not int:
        raise ValueError('Saved publication receipt belongs to another destination')
    if destination == GROUP:
        transport.api(campaign_id + '/message', {'messageId': sent['message_id']})
    return sent


def tick(transport, root, campaign_id, now=time.time, destination=GROUP, bind_message=True):
    if destination not in (GROUP, CHANNEL):
        raise ValueError('Unapproved destination')
    if (root / 'finished.json').exists():
        return
    receipt = root / 'send-receipt.json'
    if not receipt.exists():
        return  # The recurring worker never starts or republishes a campaign.
    sent = json.loads(receipt.read_text())
    if sent.get('chat', {}).get('id') != destination:
        raise ValueError('Saved publication receipt belongs to another destination')
    offer = transport.api(campaign_id)
    validate(offer, campaign_id, require_media=now() < timestamp(offer['endsAtUtc']))
    if destination == GROUP and bind_message:
        transport.api(campaign_id + '/message', {'messageId': sent['message_id']})
    text = caption(offer, now())
    updated = root / 'last-edit.json'
    previous = json.loads(updated.read_text()) if updated.exists() else {}
    if previous.get('caption') != text:
        try:
            transport.telegram('editMessageCaption', {'chat_id': destination, 'message_id': sent['message_id'],
                'caption': text, 'parse_mode': 'HTML', 'reply_markup': markup(offer, now())})
        except Rejected as error:
            # Editing is idempotent. A failed edit is retried on the next minute;
            # it cannot extend the server-side pricing deadline.
            if error.missing:
                save(root / 'finished.json', {'at': now(), 'message_id': sent['message_id'], 'old_message_unavailable': True})
                return
            if not error.unchanged:
                save(root / 'edit-error.json', {'http_status': error.code, 'at': now()})
                raise
        save(updated, {'caption': text, 'at': now()})
    if now() >= timestamp(offer['endsAtUtc']):
        save(root / 'finished.json', {'at': now(), 'message_id': sent['message_id']})


def reposts(transport, root, campaign_id, now=time.time):
    """Called under run.lock. Refresh prior reminders, then send at most one due photo.

    An unknown send outcome blocks later sends for this campaign pending review;
    durable receipts survive crashes without rebinding the original campaign post.
    Missed intervals (including quiet hours) are never replayed in a burst.
    """
    if not (root / 'send-receipt.json').exists():
        return
    current = now()
    directories = sorted((root / 'reposts').glob('*'))
    if (root / 'finished.json').exists() and all((p / 'finished.json').exists() for p in directories):
        return
    quiet = dt.datetime.fromtimestamp(current, TEHRAN).hour < 6
    if quiet and not directories:
        return
    pending = False
    last_sent = timestamp(transport.api(campaign_id)['startsAtUtc'])
    original_intent = root / 'send-intent.json'
    if original_intent.exists():
        last_sent = max(last_sent, json.loads(original_intent.read_text())['at'])
    for directory in directories:
        receipt = directory / 'send-receipt.json'
        if receipt.exists():
            sent = json.loads(receipt.read_text())
            last_sent = max(last_sent, sent['at'])
            try:
                tick(transport, directory, campaign_id, now=now, bind_message=False)
            except Exception as error:
                save(directory / 'last-error.json', {'type': type(error).__name__, 'at': current})
        elif (directory / 'send-intent.json').exists():
            pending = True
    if pending:
        raise RuntimeError('Reminder outcome unknown; manual review required')
    if quiet or current - last_sent < REPOST_INTERVAL:
        return
    cooldown = root / 'repost-cooldown.json'
    if cooldown.exists() and json.loads(cooldown.read_text())['until'] > current:
        return
    offer = transport.api(campaign_id)  # Re-read remaining volume and the rollover source immediately before sending.
    validate(offer, campaign_id, require_media=offer['isActive'])
    if not offer['isActive'] or offer['remainingVolume'] <= 0 or not timestamp(offer['startsAtUtc']) <= current < timestamp(offer['endsAtUtc']):
        return
    text = caption(offer, current)
    if len(text.encode('utf-16-le')) // 2 > 1024:
        raise ValueError('Reminder caption exceeds Telegram limit')
    directory = root / 'reposts' / str(int(current // REPOST_INTERVAL))
    intent = directory / 'send-intent.json'
    save(intent, {'at': current, 'campaign_id': campaign_id, 'destination': GROUP})
    try:
        sent = transport.telegram('sendPhoto', {'chat_id': GROUP, 'photo': offer['telegramPhotoFileId'],
            'caption': text, 'parse_mode': 'HTML', 'reply_markup': markup(offer, current)})
    except Rejected as error:
        save(directory / 'send-rejected.json', {'http_status': error.code, 'at': current})
        save(cooldown, {'until': current + error.retry_after})
        intent.unlink()
        raise
    if sent.get('chat', {}).get('id') != GROUP or type(sent.get('message_id')) is not int:
        raise RuntimeError('Reminder receipt unavailable; manual review required')
    save(directory / 'send-receipt.json', {'message_id': sent['message_id'], 'chat': {'id': GROUP}, 'at': now()})
    save(directory / 'last-edit.json', {'caption': text, 'at': current})


class UnknownAnnouncement(RuntimeError):
    pass


def end_thanks(transport, root, campaign_id, now=time.time):
    offer = transport.api(campaign_id)
    validate(offer, campaign_id, require_media=False)
    if now() < timestamp(offer['endsAtUtc']):
        return False
    text = offer.get('thankYouText') or (
        '🎉 پروموشن ساعتی به پایان رسید\n\nزیباشی‌های عزیزم 🌸\n'
        'از همراهی، اعتماد و خریدهای قشنگتون خیلی ممنونیم 🤍🌷\n'
        'تخفیف آیتم‌های ثبت‌شده در مهلت، در فاکتورشان محفوظ است.\n'
        'ان‌شاءالله در آینده هم از این تخفیف‌های خوش‌عطر خواهیم داشت ✨')
    for target, destination in [(root, GROUP), (root / 'channel', CHANNEL)]:
        if (target / 'send-receipt.json').exists() and not (target / 'finished.json').exists():
            try:
                tick(transport, target, campaign_id, now=now, destination=destination)
            except Exception as error:
                save(target / 'closing-edit-error.json', {'type': type(error).__name__, 'at': now()})
        directory = target / 'closing-announcement'
        receipt, intent = directory / 'send-receipt.json', directory / 'send-intent.json'
        if receipt.exists():
            sent = json.loads(receipt.read_text())
            if sent.get('chat', {}).get('id') != destination:
                raise ValueError('Closing receipt belongs to another destination')
            continue
        if intent.exists():
            raise UnknownAnnouncement('Closing announcement outcome unknown; manual review required')
        chat = transport.telegram('getChat', {'chat_id': destination})
        if chat.get('id') != destination:
            raise ValueError('Closing destination mismatch')
        save(intent, {'at': now(), 'destination': destination})
        try:
            sent = transport.telegram('sendMessage', {'chat_id': destination, 'text': text, 'parse_mode': 'HTML',
                'link_preview_options': {'is_disabled': True}})
        except Rejected:
            intent.unlink()
            raise
        except Exception as error:
            raise UnknownAnnouncement('Closing announcement outcome unknown; manual review required') from None
        if sent.get('chat', {}).get('id') != destination or type(sent.get('message_id')) is not int:
            raise UnknownAnnouncement('Closing receipt unavailable; manual review required')
        save(receipt, {'message_id': sent['message_id'], 'chat': {'id': destination}})
    save(root / 'end-announcements-complete.json', {'at': now()})
    return True


def main():
    parser = argparse.ArgumentParser()
    publication = parser.add_mutually_exclusive_group()
    publication.add_argument('--publish', metavar='CAMPAIGN_ID')
    publication.add_argument('--publish-channel', metavar='CAMPAIGN_ID')
    args = parser.parse_args()
    STATE.mkdir(mode=0o700, parents=True, exist_ok=True)
    with (STATE / 'run.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return
        transport = None
        if args.publish or args.publish_channel:
            campaign_id = str(uuid.UUID(args.publish or args.publish_channel))
            transport = Transport()
            destination = CHANNEL if args.publish_channel else GROUP
            root = STATE / campaign_id / 'channel' if args.publish_channel else STATE / campaign_id
            sent = publish(transport, root, campaign_id, destination=destination)
            prefix = 'https://t.me/zibasheperfume/' if args.publish_channel else 'https://t.me/c/1283018740/'
            print(json.dumps({'published': True, 'message_id': sent['message_id'],
                'url': prefix + str(sent['message_id'])}))
        for root in STATE.iterdir():
            if not root.is_dir():
                continue
            try:
                campaign_id = str(uuid.UUID(root.name))
            except ValueError:
                continue
            for target, destination in [(root, GROUP), (root / 'channel', CHANNEL)]:
                if not (target / 'send-receipt.json').exists() or (target / 'finished.json').exists():
                    continue
                try:
                    transport = transport or Transport()
                    tick(transport, target, campaign_id, destination=destination)
                except Exception as error:
                    # One destination's failure cannot stall the other countdown.
                    save(target / 'last-error.json', {'type': type(error).__name__, 'at': time.time()})
            lifecycle = root / 'automatic-lifecycle.json'
            if (root / 'send-receipt.json').exists():
                try:
                    transport = transport or Transport()
                    reposts(transport, root, campaign_id)
                except Exception as error:
                    save(root / 'repost-error.json', {'type': type(error).__name__, 'at': time.time()})
            if lifecycle.exists() and not (root / 'end-announcements-complete.json').exists():
                try:
                    transport = transport or Transport()
                    end_thanks(transport, root, campaign_id)
                except Exception as error:
                    save(root / 'end-announcement-error.json', {'type': type(error).__name__, 'at': time.time()})


if __name__ == '__main__':
    main()
