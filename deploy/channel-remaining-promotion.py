#!/usr/bin/env python3
"""Maintain channel promotions; automatic selections are strictly below 10 ml.

Run once a minute from cron. ERP access is read-only. A durable send journal and
an exclusive lock prevent duplicate posts across retries, crashes and restarts.
Only promotion message IDs recorded by this process may be edited or deleted.
"""
import argparse
import datetime as dt
import fcntl
import hashlib
import html
from html.parser import HTMLParser
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

CHANNEL = '-1001236168661'
USERNAME = 'zibasheperfume'
BOT_ID = 8753129822
RENEW_AFTER = 36 * 3600  # Telegram normally permits deletion only for 48 hours.
STATE_DIR = Path.home() / '.local/state/zibashe-channel-promotion'
PERSIAN = str.maketrans('0123456789', '۰۱۲۳۴۵۶۷۸۹')


def remaining(row):
    return row['TotalVolume'] - row['ReservedVolume']


def eligible(row, maximum=10):
    return bool(row and not row.get('IsDeleted') and not row.get('IsInventoryOffer')
                and row.get('Status') == 1 and remaining(row) > 0
                and (maximum is None or remaining(row) < maximum)
                and str(row.get('TelegramChannelId')) == CHANNEL
                and row.get('TelegramPhotoFileId') and row.get('TelegramMessageId'))


def perfume_key(row):
    return str(row.get('PerfumeId') or hashlib.sha256(
        (row.get('DisplayBrand', '') + '|' + row['EnglishName']).casefold().encode()).hexdigest()).lower()


def source_link(row):
    return f"https://t.me/{USERNAME}/{int(row['TelegramMessageId'])}"


def short(value, limit):
    return ' '.join(str(value or '').split())[:limit].rstrip(' ،,؛;')


def caption(row, custom_text=None):
    name = html.escape(short(row.get('PersianName') or row['EnglishName'], 90))
    english = html.escape(short(row['EnglishName'], 65))
    brand = html.escape(short(row.get('DisplayBrand'), 45))
    code = str(row.get('StablePublicCode') or row['PublicCode']).translate(PERSIAN)
    volume = format(remaining(row), 'g').translate(PERSIAN)
    if custom_text is not None:
        if not isinstance(custom_text,str) or not custom_text.strip() or len(custom_text.encode('utf-16-le')) // 2 > 700:
            raise ValueError('Invalid custom channel copy')
        return (html.escape(custom_text) + f'\n\n🧴 <b>باقی‌مانده: {volume} میل</b>\n📋 کد لیست: <b>{code}</b>\n\nاز دکمهٔ زیر خریدت را ثبت کن 👇')
    header = f'✨ <b>{name}</b>\n{english}' + (f' · {brand}' if brand else '')
    details = []
    for label, field in [('🌿 حال‌وهوای رایحه', 'Accords'),
                         ('🌸 شروع رایحه', 'TopNotes'), ('🪵 رایحهٔ پایانی', 'BaseNotes')]:
        if row.get(field):
            details.append(f'{label}: {html.escape(short(row[field], 100))}')
    footer = (f'🧴 <b>فقط {volume} میل تا تکمیل لیست!</b>\n📋 کد لیست: <b>{code}</b>\n\n'
              'اگه انتخاب توئه، تا جا هست از دکمهٔ زیر ثبتش کن 👇')
    intro = 'یه فرصت کوچیک برای تجربهٔ یه رایحهٔ تازه 🤍'
    for body in [intro + '\n\n' + '\n'.join(details), intro, '']:
        result = '\n\n'.join(part for part in [header, body, footer] if part)
        if len(result.encode('utf-16-le')) // 2 <= 1024:
            return result
    raise ValueError('Perfume title exceeds Telegram caption limit')


def markup(row):
    return {'inline_keyboard': [[{'text': '🧴 دیدن لیست و ثبت درخواست', 'url': source_link(row)}]]}


class TextParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts = []

    def handle_data(self, value):
        self.parts.append(value)

    def handle_starttag(self, tag, attrs):
        if tag == 'br':
            self.parts.append('\n')


def plain(value):
    parser = TextParser()
    parser.feed(value)
    return ' '.join(''.join(parser.parts).split())


class PreviewParser(HTMLParser):
    """Read public channel receipts; never obtain updates or disturb the webhook."""
    VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'source', 'wbr'}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack, self.messages = [], []
        self.current = self.message_depth = self.text_depth = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag not in self.VOID:
            self.stack.append(tag)
        if attrs.get('data-post', '').startswith(USERNAME + '/'):
            post = attrs['data-post'].split('/')[-1]
            if post.isdigit():
                self.current = {'message_id': int(post), 'text': '', 'date': None}
                self.message_depth = len(self.stack)
        if self.current is None:
            return
        if 'tgme_widget_message_text' in attrs.get('class', '').split():
            self.text_depth = len(self.stack)
        if tag == 'br' and self.text_depth:
            self.current['text'] += '\n'
        if tag == 'time' and attrs.get('datetime'):
            try:
                self.current['date'] = dt.datetime.fromisoformat(
                    attrs['datetime'].replace('Z', '+00:00')).timestamp()
            except ValueError:
                pass

    def handle_data(self, value):
        if self.current is not None and self.text_depth:
            self.current['text'] += value

    def handle_endtag(self, tag):
        if tag not in self.stack:
            return
        depth = len(self.stack) - self.stack[::-1].index(tag)
        if self.text_depth and depth <= self.text_depth:
            self.text_depth = None
        if self.current is not None and depth <= self.message_depth:
            self.messages.append(self.current)
            self.current = self.message_depth = self.text_depth = None
        del self.stack[depth - 1:]


def recover_message(ad):
    request = urllib.request.Request('https://t.me/s/' + USERNAME,
                                     headers={'User-Agent': 'Mozilla/5.0'})
    with urllib.request.urlopen(request, timeout=20) as response:
        body = response.read(2_000_000).decode('utf-8')
    parser = PreviewParser()
    parser.feed(body)
    matches = [message for message in parser.messages
               if message['date'] is not None and message['date'] >= ad['started_at'] - 30
               and message['message_id'] != int(ad['row']['TelegramMessageId'])
               and ' '.join(message['text'].split()) == plain(ad['caption'])]
    return matches[0] if len(matches) == 1 else None


class Store:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        os.chmod(self.directory, 0o700)
        self.path = self.directory / 'state.json'
        self.data = json.loads(self.path.read_text()) if self.path.exists() else {
            'version': 1, 'channel_id': CHANNEL, 'active': None, 'last_seen': {},
            'last_perfume': None, 'blocked': {}, 'retry_at': 0, 'retired': []}
        if self.data.get('version') != 1 or self.data.get('channel_id') != CHANNEL:
            raise ValueError('Promotion state identity mismatch')

    def save(self):
        temporary = self.path.with_suffix('.tmp')
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'w') as stream:
            json.dump(self.data, stream, ensure_ascii=False, separators=(',', ':'))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, self.path)
        fd = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


class TelegramFailure(Exception):
    def __init__(self, code=None, reason='', retry_after=60, unknown=False):
        self.code, self.reason = code, reason.lower()
        self.retry_after, self.unknown = retry_after, unknown
        super().__init__('Telegram request outcome unknown' if unknown else f'Telegram rejected request ({code})')


class Telegram:
    def __init__(self, token):
        self.token = token

    def call(self, method, payload):
        request = urllib.request.Request('https://api.telegram.org/bot' + self.token + '/' + method,
                                        data=json.dumps(payload).encode(),
                                        headers={'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            try:
                result = json.load(error)
            except Exception:
                raise TelegramFailure(error.code, unknown=error.code >= 500) from None
        except Exception:
            raise TelegramFailure(unknown=True) from None
        if not result.get('ok'):
            code = result.get('error_code', 500)
            raise TelegramFailure(code, str(result.get('description', '')),
                                  int(result.get('parameters', {}).get('retry_after', 60)), code >= 500)
        return result.get('result')

    def verify_destination(self):
        me = self.call('getMe', {})
        chat = self.call('getChat', {'chat_id': CHANNEL})
        rights = self.call('getChatMember', {'chat_id': CHANNEL, 'user_id': BOT_ID})
        if (me['id'] != BOT_ID or str(chat['id']) != CHANNEL or chat['type'] != 'channel'
                or chat.get('username', '').lower() != USERNAME
                or not all(rights.get(key) for key in ('can_post_messages', 'can_edit_messages', 'can_delete_messages'))):
            raise ValueError('Channel identity or bot privileges changed')

    def send(self, row, text):
        return self.call('sendPhoto', {'chat_id': CHANNEL, 'photo': row['TelegramPhotoFileId'],
                                      'caption': text, 'parse_mode': 'HTML',
                                      'reply_markup': markup(row), 'disable_notification': True})

    def edit(self, message_id, text, buttons):
        try:
            return self.call('editMessageCaption', {'chat_id': CHANNEL, 'message_id': message_id,
                                                    'caption': text, 'parse_mode': 'HTML',
                                                    'reply_markup': buttons})
        except TelegramFailure as error:
            if error.code == 400 and 'message is not modified' in error.reason:
                return True
            raise

    def delete(self, message_id):
        return self.call('deleteMessage', {'chat_id': CHANNEL, 'message_id': message_id})


class Catalogue:
    def rows(self, active_id=None):
        active = " OR Id='" + str(uuid.UUID(active_id)) + "'" if active_id else ''
        return self.read("(IsDeleted=0 AND IsInventoryOffer=0 AND Status=1 "
                         "AND TotalVolume-ReservedVolume>0 AND TotalVolume-ReservedVolume<10 "
                         "AND TelegramChannelId='" + CHANNEL + "')" + active)

    def by_code(self, code):
        code = int(code)
        return self.read(f'(PublicCode={code} OR StablePublicCode={code}) AND IsDeleted=0 AND Status=1')

    def read(self, condition):
        query = """SET NOCOUNT ON; SELECT Id,PerfumeId,PublicCode,StablePublicCode,EnglishName,
        PersianName,DisplayBrand,Accords,TopNotes,BaseNotes,Status,IsDeleted,IsInventoryOffer,
        TotalVolume,ReservedVolume,TelegramPhotoFileId,TelegramChannelId,TelegramMessageId
        FROM SalesLists WHERE """ + condition + ' FOR JSON PATH;'
        result = subprocess.run(['docker', 'exec', '-i', 'deploy-sqlserver-1', 'sh', '-c',
                                 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd '
                                 '-S localhost -U sa -C -d ZibasheERPDb -b -y 0 -w 65535 -t 15'],
                                input=query, capture_output=True, text=True, timeout=25)
        if result.returncode:
            raise RuntimeError('Read-only catalogue query failed')
        return json.loads(''.join(result.stdout.splitlines()) or '[]')


def active_ads(state):
    result = [state['active']] if state.get('active') else []
    result.extend(slot['active'] for slot in state.get('additional', []) if slot.get('active'))
    return result


def retired_ads(state):
    result = list(state.get('retired', []))
    for slot in state.get('additional', []):
        result.extend(slot.get('retired', []))
    return result


class AdditionalStore:
    def __init__(self, parent, data):
        self.parent, self.data = parent, data

    def save(self):
        self.parent.save()


class Engine:
    def __init__(self, store, catalogue, telegram, recover=recover_message):
        self.store, self.catalogue, self.telegram, self.recover = store, catalogue, telegram, recover
        self.state = store.data
        self.excluded_ids = set()

    def result(self, status, now, **extra):
        result = {'status': status, 'at': now, **extra}
        self.state['last_run'] = result
        self.store.save()
        return result

    def finish(self, now, outcome):
        ad = self.state['active']
        self.state['retired'] = (self.state['retired'] + [{
            'message_id': ad['message_id'], 'list_id': ad['row']['Id'],
            'reason': ad.get('retire_reason', 'externally_removed'), 'outcome': outcome, 'at': now,
            'request_id': ad.get('request_id'),
            'removal_request_id': self.state.get('pause_request_id') if ad.get('retire_reason') == 'admin_removed' else None}])[-100:]
        self.state['active'] = None
        self.store.save()

    def owned_id(self, ad):
        message_id = ad.get('message_id')
        if (ad.get('channel_id') != CHANNEL or not isinstance(message_id, int) or message_id <= 0
                or message_id == int(ad['row']['TelegramMessageId'])):
            raise ValueError('Refusing to mutate an unverified promotion message')
        return message_id

    def retire(self, ad, now):
        message_id = self.owned_id(ad)
        try:
            self.telegram.delete(message_id)
        except TelegramFailure as error:
            if error.code == 400 and 'message to delete not found' in error.reason:
                pass
            elif error.code == 400 and ("can't be deleted" in error.reason or 'cannot be deleted' in error.reason):
                # An outage spanning the deletion window must not leave a false stock claim.
                text = ('✅ <b>این لیست تکمیل شد.</b>' if ad['retire_reason'] == 'completed'
                        else '🌷 <b>این تبلیغ پایان یافته است.</b>')
                try:
                    self.telegram.edit(message_id, text, {'inline_keyboard': []})
                except TelegramFailure as edit_error:
                    if not (edit_error.code == 400 and 'message to edit not found' in edit_error.reason):
                        raise
                self.finish(now, 'expired_message_archived')
                return True
            else:
                self.state['retry_at'] = now + max(60, error.retry_after)
                self.store.save()
                return False
        self.finish(now, 'deleted')
        return True

    def pause(self, now, request_id, requested_at=None):
        prior = next((value for value in self.state.get('removals', []) if value['request_id'] == request_id), None)
        if prior:
            return {'status': 'pause_already_completed', 'outcome': prior['outcome'], 'at': now}
        requested_at = now if requested_at is None else requested_at
        if requested_at < self.state.get('last_manual_selection_at', 0):
            return {'status': 'pause_superseded', 'at': now}
        self.state.update(paused=True, pause_request_id=request_id, paused_at=requested_at)
        self.state.pop('selection', None)
        self.store.save()  # Persist the pause before any Telegram request.
        return self.tick(now)

    def tick(self, now):
        selection = self.state.get('selection')
        ad = self.state.get('active')
        slots = self.state.setdefault('additional', [])
        valid_selection = False
        if selection and (selection.get('expires_at') or float('inf')) > now:
            selected = next((value for value in self.catalogue.rows(selection['list_id'])
                             if value['Id'].lower() == selection['list_id'].lower()), None)
            valid_selection = eligible(selected, maximum=None)
            if self.state.get('paused') and selection.get('requested_at', now) <= self.state.get('paused_at', 0):
                valid_selection = False
        if valid_selection:
            requested_at = selection.get('requested_at', now)
            if (requested_at < self.state.get('last_replace_at', 0) or
                    (selection.get('mode', 'replace') == 'replace' and requested_at < self.state.get('last_manual_selection_at', 0))):
                self.state.pop('selection', None)
                return self.result('selection_superseded_by_newer_request', now)
            if selection.get('mode', 'replace') == 'replace':
                self.state['last_replace_at'] = requested_at
            matched = next((slot for slot in slots if slot.get('active') and
                            slot['active']['row']['Id'].lower() == selection['list_id'].lower()
                            and slot['active']['phase'] == 'active'), None)
            if matched:
                slots.remove(matched)
                self.state['retired'] = (self.state['retired'] + matched.get('retired', []))[-100:]
                if ad:
                    slots.append({'version': 1, 'channel_id': CHANNEL, 'active': ad, 'last_seen': {},
                                  'last_perfume': None, 'blocked': {}, 'retry_at': 0, 'retired': []})
                self.state['active'] = ad = matched['active']
                self.store.save()
            if selection.get('mode', 'replace') == 'append':
                if ad and ad['phase'] in ('sending', 'unknown'):
                    receipt = self.recover(ad)
                    if receipt:
                        ad.update(phase='active', message_id=receipt['message_id'], sent_at=receipt['date'])
                        self.store.save()
                if ad and ad['phase'] == 'active' and ad['row']['Id'].lower() != selection['list_id'].lower():
                    ad['manual_selection'] = True
                    slots.append({'version': 1, 'channel_id': CHANNEL, 'active': ad, 'last_seen': {},
                                  'last_perfume': None, 'blocked': {}, 'retry_at': 0, 'retired': []})
                    self.state['active'] = ad = None
                    self.store.save()  # Keep the previous ad durable before starting a new send.
            else:
                for slot in slots:
                    slot.pop('selection', None)
                    if slot.get('active'):
                        slot['active'].update(phase='retiring', retire_reason='manual_replacement')
                self.store.save()
        waiting = None
        for slot in list(slots):
            if self.state.get('paused') and not valid_selection:
                slot.update(paused=True, pause_request_id=self.state.get('pause_request_id'),
                            paused_at=self.state.get('paused_at', now))
                slot.pop('selection', None)
            child = Engine(AdditionalStore(self.store, slot), self.catalogue, self.telegram, self.recover)
            try:
                result = child._tick_single(now, allow_new=False)
            except TelegramFailure:
                waiting = 'caption_retry'
                continue
            if slot.get('active') is None and not slot.get('selection'):
                self.state['retired'] = (self.state['retired'] + slot.get('retired', []))[-100:]
                slots.remove(slot)
                self.store.save()
            elif result['status'] in ('delete_retry', 'retry_wait', 'delivery_unknown_no_duplicate_sent'):
                waiting = result['status']
        # Removal/replacement must finish for every tracked ad before it can be acknowledged.
        if waiting and (self.state.get('paused') or (valid_selection and selection.get('mode', 'replace') == 'replace')):
            return self.result(waiting, now)
        self.excluded_ids = {slot['active']['row']['Id'].lower() for slot in slots if slot.get('active')}
        return self._tick_single(now)

    def _tick_single(self, now, allow_new=True):
        if now < self.state.get('retry_at', 0):
            return self.result('retry_wait', now)
        ad = self.state['active']
        selection = self.state.get('selection')
        if selection and (selection.get('expires_at') or float('inf')) <= now:
            self.state.pop('selection', None)
            selection = None
        if selection and self.state.get('paused'):
            if selection.get('requested_at', now) <= self.state.get('paused_at', 0):
                self.state.pop('selection', None)
                return self.result('selection_superseded_by_pause', now)
            selected = next((value for value in self.catalogue.rows(selection['list_id'])
                             if value['Id'].lower() == selection['list_id'].lower()), None)
            if not eligible(selected, maximum=None):
                self.state.pop('selection', None)
                return self.result('selected_list_no_longer_available', now)
            self.state['paused'] = False
            self.store.save()
        if selection:
            self.state['last_manual_selection_at'] = max(self.state.get('last_manual_selection_at', 0), selection.get('requested_at', now))
        preferred = selection['list_id'] if selection else None
        if ad and ad['phase'] in ('sending', 'unknown'):
            receipt = self.recover(ad)
            if not receipt:
                return self.result('delivery_unknown_no_duplicate_sent', now)
            ad.update(phase='active', message_id=receipt['message_id'], sent_at=receipt['date'])
            self.state['last_seen'][perfume_key(ad['row'])] = now
            self.state['last_perfume'] = perfume_key(ad['row'])
            self.store.save()
        if self.state.get('paused'):
            if ad:
                ad.update(phase='retiring', retire_reason='admin_removed')
                self.store.save()
                if not self.retire(ad, now):
                    return self.result('delete_retry', now)
            request_id = self.state.get('pause_request_id')
            previous = next((value for value in reversed(self.state['retired'])
                             if value.get('removal_request_id') == request_id), None)
            archived = any(value.get('removal_request_id') == request_id and value['outcome'] == 'expired_message_archived'
                           for value in self.state['retired'])
            outcome = 'expired_message_archived' if archived else previous['outcome'] if previous else 'nothing_to_delete'
            if request_id and not any(value['request_id'] == request_id for value in self.state.get('removals', [])):
                self.state['removals'] = (self.state.get('removals', []) +
                    [{'request_id': request_id, 'at': now, 'outcome': outcome}])[-100:]
            return self.result('paused', now, outcome=outcome)
        if selection and ad and ad['phase'] == 'active':
            if ad['row']['Id'].lower() == selection['list_id'].lower():
                ad['manual_selection'] = True
                ad['expires_at'] = selection.get('expires_at')
                ad['request_id'] = selection.get('request_id')
                ad['custom_text'] = selection.get('custom_text')
                self.state.pop('selection', None)
                selection, preferred = None, None
            else:
                selected = next((value for value in self.catalogue.rows(selection['list_id'])
                                 if value['Id'].lower() == selection['list_id'].lower()), None)
                if not eligible(selected, maximum=None):
                    self.state.pop('selection', None)
                    return self.result('selected_list_no_longer_available', now)
                ad.update(phase='retiring', retire_reason='manual_replacement')
            self.store.save()
        if ad and ad['phase'] == 'retiring':
            if ad.get('retire_reason') == 'renewal':
                preferred = ad['row']['Id']
                if ad.get('manual_selection') or not allow_new:
                    selection = {'list_id': preferred, 'manual': True, 'expires_at': ad.get('expires_at'), 'request_id': ad.get('request_id'), 'custom_text': ad.get('custom_text')}
                    self.state['selection'] = selection
                    self.store.save()
            if not self.retire(ad, now):
                return self.result('delete_retry', now)
            ad = None
        rows = self.catalogue.rows(ad['row']['Id'] if ad else preferred)
        if ad:
            self.owned_id(ad)
            fresh = next((row for row in rows if row['Id'].lower() == ad['row']['Id'].lower()), None)
            allowed = eligible(fresh, maximum=None if ad.get('manual_selection') else 10)
            expired = ad.get('expires_at') is not None and now >= ad['expires_at']
            if not allowed or expired or now - ad['sent_at'] >= RENEW_AFTER:
                reason = ('completed' if fresh and remaining(fresh) <= 0 else 'not_eligible')
                if expired:
                    reason, preferred = 'manual_expired', None
                elif allowed:
                    reason, preferred = 'renewal', fresh['Id']
                    if ad.get('manual_selection') or not allow_new:
                        selection = {'list_id': preferred, 'manual': True, 'expires_at': ad.get('expires_at'), 'request_id': ad.get('request_id'), 'custom_text': ad.get('custom_text')}
                        self.state['selection'] = selection
                ad.update(phase='retiring', retire_reason=reason)
                self.store.save()
                if not self.retire(ad, now):
                    return self.result('delete_retry', now)
                ad = None
            else:
                text = caption(fresh, ad.get('custom_text'))
                if text != ad['caption'] or markup(fresh) != markup(ad['row']):
                    try:
                        self.telegram.edit(ad['message_id'], text, markup(fresh))
                    except TelegramFailure as error:
                        if error.code == 400 and 'message to edit not found' in error.reason:
                            self.finish(now, 'externally_removed')
                            preferred, ad = fresh['Id'], None
                        else:
                            self.state['retry_at'] = now + max(60, error.retry_after)
                            return self.result('caption_retry', now)
                    else:
                        ad.update(row=fresh, caption=text)
                        return self.result('updated', now, message_id=ad['message_id'], remaining_ml=remaining(fresh))
                else:
                    return self.result('monitoring', now, message_id=ad['message_id'], remaining_ml=remaining(fresh))
        if not allow_new and not selection and ad is None:
            return self.result('additional_finished', now)
        if selection:
            candidates = [row for row in rows if row['Id'].lower() == selection['list_id'].lower()
                          and eligible(row, maximum=None)]
            if not candidates:
                self.state.pop('selection', None)
                return self.result('selected_list_no_longer_available', now)
        else:
            candidates = [row for row in rows if eligible(row) and row['Id'].lower() not in self.excluded_ids
                          and self.state.get('blocked', {}).get(row['Id'].lower(), 0) <= now]
        candidates.sort(key=lambda row: (row['Id'] != preferred,
                         perfume_key(row) == self.state.get('last_perfume'),
                         self.state['last_seen'].get(perfume_key(row), 0), remaining(row), row['Id']))
        if not candidates:
            return self.result('waiting_for_under_10_ml', now)
        row = candidates[0]
        manual = bool(selection)
        self.telegram.verify_destination()
        # Re-read after selection; never publish a catalogue snapshot that completed meanwhile.
        fresh = next((value for value in self.catalogue.rows(row['Id'])
                      if value['Id'].lower() == row['Id'].lower()), None)
        if not eligible(fresh, maximum=None if manual else 10):
            return self.result('candidate_changed_retry_next_minute', now)
        ad = {'phase': 'sending', 'channel_id': CHANNEL, 'row': fresh,
              'caption': caption(fresh, selection.get('custom_text') if selection else None), 'custom_text': selection.get('custom_text') if selection else None, 'started_at': now, 'message_id': None,
              'manual_selection': manual, 'expires_at': selection.get('expires_at') if selection else None,
              'request_id': selection.get('request_id') if selection else None}
        self.state['active'] = ad
        self.store.save()  # Persist intent before the non-idempotent send.
        try:
            receipt = self.telegram.send(fresh, ad['caption'])
        except TelegramFailure as error:
            if error.unknown:
                ad['phase'] = 'unknown'
                return self.result('delivery_unknown_no_duplicate_sent', now)
            self.state['active'] = None
            self.state['retry_at'] = now + max(60, error.retry_after)
            if error.code != 429:
                self.state['blocked'][fresh['Id'].lower()] = now + 900
            return self.result('send_rejected_retry_later', now, error_code=error.code)
        if (str(receipt.get('chat', {}).get('id')) != CHANNEL
                or not isinstance(receipt.get('message_id'), int)
                or receipt['message_id'] == int(fresh['TelegramMessageId'])):
            ad['phase'] = 'unknown'
            return self.result('delivery_receipt_mismatch', now)
        ad.update(phase='active', message_id=receipt['message_id'], sent_at=receipt.get('date', now))
        self.state['last_seen'][perfume_key(fresh)] = now
        self.state['last_perfume'] = perfume_key(fresh)
        if manual:
            self.state.pop('selection', None)
        return self.result('sent', now, message_id=ad['message_id'],
                           perfume=fresh['PersianName'] or fresh['EnglishName'],
                           code=fresh.get('StablePublicCode') or fresh['PublicCode'],
                           remaining_ml=remaining(fresh))


def bot_token():
    result = subprocess.run(['docker', 'inspect', 'deploy-zibashe-api-1'],
                            capture_output=True, text=True, timeout=15)
    if result.returncode:
        raise RuntimeError('Bot runtime configuration unavailable')
    config = dict(item.split('=', 1) for item in json.loads(result.stdout)[0]['Config']['Env'] if '=' in item)
    return config['Telegram__BotToken']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--state-dir', type=Path, default=STATE_DIR)
    parser.add_argument('--preview', action='store_true', help='Read catalogue and show selection without sending')
    parser.add_argument('--status', action='store_true', help='Show durable promotion state without network calls')
    parser.add_argument('--select-code', type=int,
                        help='Replace this promotion with an explicitly chosen open list; later automatic choices remain under 10 ml')
    parser.add_argument('--hold-hours', type=float, help='End the selected promotion after this many hours')
    args = parser.parse_args()
    if args.hold_hours is not None and (args.select_code is None or not 0 < args.hold_hours <= 24 * 365):
        parser.error('--hold-hours requires --select-code and a positive finite duration up to one year')
    os.umask(0o077)
    store = Store(args.state_dir)
    handler = RotatingFileHandler(store.directory / 'runner.log', maxBytes=2_000_000, backupCount=3)
    logger = logging.getLogger('channel-promotion')
    logger.setLevel(logging.INFO)
    logger.addHandler(handler)
    with (store.directory / 'run.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return
        # Reload only after obtaining the lock: a previous process may have just committed.
        store = Store(args.state_dir)
        if args.status:
            ad = store.data['active']
            print(json.dumps({'last_run': store.data.get('last_run'), 'active': None if not ad else {
                'phase': ad['phase'], 'message_id': ad.get('message_id'), 'caption': ad['caption'],
                'code': ad['row'].get('StablePublicCode') or ad['row']['PublicCode'],
                'remaining_ml': remaining(ad['row']), 'sent_at': ad.get('sent_at'),
                'expires_at': ad.get('expires_at'), 'request_id': ad.get('request_id'), 'custom_text': ad.get('custom_text')},
                'retired': store.data['retired'][-3:], 'active_count': len(active_ads(store.data)),
                'additional_codes': [value['row'].get('StablePublicCode') or value['row']['PublicCode']
                                     for slot in store.data.get('additional', []) if (value := slot.get('active'))]}, ensure_ascii=False))
            return
        if args.preview:
            values = Catalogue().by_code(args.select_code) if args.select_code else Catalogue().rows()
            rows = sorted((row for row in values if eligible(row, maximum=None if args.select_code else 10)), key=remaining)
            print(json.dumps({'status': 'preview_no_messages_sent', 'eligible_count': len(rows),
                              'caption': caption(rows[0]) if rows else None,
                              'source': source_link(rows[0]) if rows else None}, ensure_ascii=False))
            return
        try:
            if args.select_code is not None:
                matches = [row for row in Catalogue().by_code(args.select_code) if eligible(row, maximum=None)]
                if len(matches) != 1:
                    raise ValueError('Selected code must identify one open channel sales list')
                store.data['selection'] = {'list_id': matches[0]['Id'], 'manual': True,
                                           'code': args.select_code, 'requested_at': time.time(),
                                           'expires_at': time.time() + args.hold_hours * 3600 if args.hold_hours else None}
                store.save()
            result = Engine(store, Catalogue(), Telegram(bot_token())).tick(time.time())
        except Exception as error:
            # Exception strings may contain credential URLs; log only the type.
            result = {'status': 'failed_retry_next_minute', 'error_type': type(error).__name__, 'at': time.time()}
            logger.error(json.dumps(result))
            print(json.dumps(result), file=sys.stderr)
            return 1
        logger.info(json.dumps(result, ensure_ascii=False))
        print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    sys.exit(main())
