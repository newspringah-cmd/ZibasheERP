import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('flash', Path(__file__).resolve().parents[1] / 'flash-sale-promotion.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
OFFER = {'id': '11111111-1111-1111-1111-111111111111', 'code': 4559, 'groupChatId': m.GROUP,
         'salesListId': '46e6fd81-8140-4825-a51a-689d200aa95f', 'discountPercent': 50,
         'telegramPhotoFileId': 'fixture-photo', 'listUrl': 'https://t.me/zibasheperfume/16550',
         'startsAtUtc': '2026-10-29T20:00:00Z', 'endsAtUtc': '2026-10-29T22:00:00Z',
         'originalPricePerMl': 21384, 'discountedPricePerMl': 10692,
         'remainingVolume': 136, 'isActive': True, 'telegramMessageId': None}
START = m.timestamp(OFFER['startsAtUtc'])


class Transport:
    def __init__(self):
        self.offer = copy.deepcopy(OFFER)
        self.calls = []
        self.fail_send = self.fail_edit = self.fail_bind = None
        self.bind_calls = 0

    def api(self, route, data=None):
        if data is not None:
            self.bind_calls += 1
            if self.fail_bind:
                raise self.fail_bind
            self.offer['telegramMessageId'] = data['messageId']
            return
        return copy.deepcopy(self.offer)

    def telegram(self, method, payload):
        self.calls.append((method, copy.deepcopy(payload)))
        if method == 'getChat':
            if payload['chat_id'] == m.CHANNEL:
                return {'id': m.CHANNEL, 'type': 'channel', 'username': 'zibasheperfume'}
            return {'id': m.GROUP, 'type': 'supergroup'}
        if method == 'sendPhoto':
            if self.fail_send:
                raise self.fail_send
            return {'chat': {'id': payload['chat_id']}, 'message_id': 12345}
        if method == 'sendMessage':
            if self.fail_send:raise self.fail_send
            return {'chat':{'id':payload['chat_id']},'message_id':20000+len(self.calls)}
        if method == 'editMessageCaption' and self.fail_edit:
            raise self.fail_edit
        return True


class Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / OFFER['id']
        self.t = Transport()

    def publish(self):
        return m.publish(self.t, self.root, OFFER['id'], lambda: START)

    def test_approved_caption(self):
        text = m.caption(OFFER, START)
        self.assertIn('دو ساعت فرصت خرید عطر با ۵۰٪ تخفیف!', text)
        self.assertIn('۰۲:۰۰', text)
        self.assertIn('قیمت هر میل با تخفیف: ۱۰٬۶۹۲ تومان', text)
        self.assertNotIn('هزینهٔ شیشه', text)
        self.assertIn(OFFER['listUrl'], text)
        self.assertLessEqual(len(text.encode('utf-16-le')) // 2, 1024)

    def test_photo_once(self):
        self.publish()
        self.publish()
        self.assertEqual([name for name, _ in self.t.calls].count('sendPhoto'), 1)
        send = next(payload for name, payload in self.t.calls if name == 'sendPhoto')
        self.assertEqual(send['photo'], OFFER['telegramPhotoFileId'])
        self.assertEqual(send['chat_id'], m.GROUP)

    def test_countdown_edits_same_post(self):
        self.publish()
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 60)
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 120)
        edits = [payload for name, payload in self.t.calls if name == 'editMessageCaption']
        self.assertIn('۰۱:۵۹', edits[0]['caption'])
        self.assertIn('۰۱:۵۸', edits[1]['caption'])
        self.assertTrue(all(edit['message_id'] == 12345 for edit in edits))
        self.assertTrue(all('قیمت هر میل با تخفیف: ۱۰٬۶۹۲ تومان' in edit['caption'] for edit in edits))

    def test_expiry_final_edit(self):
        self.publish()
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 7200)
        self.assertTrue((self.root / 'finished.json').exists())
        edit = self.t.calls[-1][1]
        self.assertIn('مهلت دو ساعته به پایان رسید', edit['caption'])
        self.assertNotIn('ثبت خرید', edit['caption'])
        count = len(self.t.calls)
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 7260)
        self.assertEqual(len(self.t.calls), count)

    def test_unknown_send_not_repeated(self):
        self.t.fail_send = RuntimeError('unknown')
        with self.assertRaises(RuntimeError):
            self.publish()
        self.t.fail_send = None
        with self.assertRaisesRegex(RuntimeError, 'outcome unknown'):
            self.publish()
        self.assertEqual([name for name, _ in self.t.calls].count('sendPhoto'), 1)

    def test_failed_bind_resumes_receipt_without_duplicate(self):
        self.t.fail_bind = RuntimeError('temporary')
        with self.assertRaises(RuntimeError):
            self.publish()
        self.t.fail_bind = None
        self.publish()
        self.assertEqual([name for name, _ in self.t.calls].count('sendPhoto'), 1)
        self.assertEqual(self.t.offer['telegramMessageId'], 12345)

    def test_expiry_edit_retries_until_accepted(self):
        self.publish()
        self.t.fail_edit = m.Rejected(429)
        with self.assertRaises(m.Rejected):
            m.tick(self.t, self.root, OFFER['id'], lambda: START + 7200)
        self.assertFalse((self.root / 'finished.json').exists())
        self.t.fail_edit = None
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 7260)
        self.assertTrue((self.root / 'finished.json').exists())

    def test_crash_after_edit_unchanged_response_finishes(self):
        self.publish()
        self.t.fail_edit = m.Rejected(400, unchanged=True)
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 7200)
        self.assertTrue((self.root / 'finished.json').exists())

    def test_stock_completion_can_reopen_inside_window(self):
        self.publish()
        self.t.offer.update(remainingVolume=0, isActive=False)
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 60)
        self.assertIn('ظرفیت لیست تکمیل شد', self.t.calls[-1][1]['caption'])
        self.assertFalse((self.root / 'finished.json').exists())
        self.t.offer.update(remainingVolume=5, isActive=True)
        m.tick(self.t, self.root, OFFER['id'], lambda: START + 120)
        self.assertIn('۰۱:۵۸', self.t.calls[-1][1]['caption'])

    def test_cron_never_publishes_unreceipted_campaigns(self):
        m.tick(self.t, self.root, OFFER['id'], lambda: START)
        self.assertEqual(self.t.calls, [])

    def test_wrong_list_rejected_without_send(self):
        self.t.offer['code'] = 0
        with self.assertRaises(ValueError):
            self.publish()
        self.assertEqual(self.t.calls, [])

    def test_generic_campaign_uses_api_copy_instead_of_fixed_perfume(self):
        self.t.offer.update(code=9876,caption='Approved different perfume and discount',thankYouText='Thanks for the new offer')
        self.publish()
        sent=next(payload for method,payload in self.t.calls if method=='sendPhoto')
        self.assertIn('Approved different perfume and discount', sent['caption'])
        self.assertIn('باقی‌مانده: ۱۳۶ میل', sent['caption'])

    def test_natural_end_sends_thanks_to_group_and_channel_exactly_once(self):
        self.publish()
        m.publish(self.t,self.root/'channel',OFFER['id'],lambda:START,destination=m.CHANNEL)
        self.t.offer['thankYouText']='Thanks for your purchases'
        self.assertTrue(m.end_thanks(self.t,self.root,OFFER['id'],lambda:START+7200))
        self.assertTrue(m.end_thanks(self.t,self.root,OFFER['id'],lambda:START+7260))
        sent=[payload for method,payload in self.t.calls if method=='sendMessage']
        self.assertEqual([p['chat_id'] for p in sent],[m.GROUP,m.CHANNEL])
        self.assertTrue(all(p['text']=='Thanks for your purchases' for p in sent))

    def test_early_end_uses_shortened_deadline_and_keeps_thank_receipts(self):
        self.publish()
        self.t.offer['endsAtUtc']='2026-10-29T20:10:00Z'
        self.t.offer['isActive']=False
        self.assertFalse(m.end_thanks(self.t,self.root,OFFER['id'],lambda:START+599))
        self.assertTrue(m.end_thanks(self.t,self.root,OFFER['id'],lambda:START+600))
        self.assertEqual(len([1 for method,_ in self.t.calls if method=='sendMessage']),2)

    def test_unknown_closing_announcement_is_never_blindly_repeated(self):
        self.publish();self.t.fail_send=TimeoutError()
        with self.assertRaises(m.UnknownAnnouncement):m.end_thanks(self.t,self.root,OFFER['id'],lambda:START+7200)
        self.t.fail_send=None
        with self.assertRaises(m.UnknownAnnouncement):m.end_thanks(self.t,self.root,OFFER['id'],lambda:START+7260)
        self.assertEqual(len([1 for method,_ in self.t.calls if method=='sendMessage']),1)

    def test_deleted_original_ad_does_not_block_end_announcements(self):
        self.publish();self.t.fail_edit=m.Rejected(400,missing=True)
        self.assertTrue(m.end_thanks(self.t,self.root,OFFER['id'],lambda:START+7200))
        self.assertEqual(len([1 for method,_ in self.t.calls if method=='sendMessage']),2)

    def test_channel_copy_reuses_offer_without_changing_group_receipt_or_deadline(self):
        self.publish()
        bound = self.t.bind_calls
        original_end = self.t.offer['endsAtUtc']
        child = self.root / 'channel'
        for _ in range(2):
            m.publish(self.t, child, OFFER['id'], lambda: START + 600, destination=m.CHANNEL)
        sends = [payload for name, payload in self.t.calls if name == 'sendPhoto']
        self.assertEqual(len(sends), 2)
        self.assertEqual(sends[-1]['chat_id'], m.CHANNEL)
        self.assertIn('۰۱:۵۰', sends[-1]['caption'])
        self.assertEqual(self.t.offer['endsAtUtc'], original_end)
        self.assertEqual(self.t.bind_calls, bound)
        m.tick(self.t, child, OFFER['id'], lambda: START + 660, destination=m.CHANNEL)
        self.assertEqual(self.t.calls[-1][1]['chat_id'], m.CHANNEL)
        self.assertIn('۰۱:۴۹', self.t.calls[-1][1]['caption'])

    def test_channel_copy_ends_at_original_group_deadline(self):
        child = self.root / 'channel'
        m.publish(self.t, child, OFFER['id'], lambda: START + 600, destination=m.CHANNEL)
        m.tick(self.t, child, OFFER['id'], lambda: START + 7200, destination=m.CHANNEL)
        self.assertTrue((child / 'finished.json').exists())
        self.assertIn('مهلت دو ساعته به پایان رسید', self.t.calls[-1][1]['caption'])

    def test_receipt_cannot_be_reused_in_another_destination(self):
        self.publish()
        with self.assertRaisesRegex(ValueError, 'another destination'):
            m.publish(self.t, self.root, OFFER['id'], lambda: START, destination=m.CHANNEL)
        self.assertEqual([name for name, _ in self.t.calls].count('sendPhoto'), 1)


class RepostTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / OFFER['id']
        self.t = Transport()
        self.start = m.timestamp('2026-10-29T06:30:00Z')  # 10:00 Tehran
        self.t.offer.update(startsAtUtc='2026-10-29T06:30:00Z', endsAtUtc='2026-10-29T22:00:00Z',
                            caption='⚡️ پیشنهاد ویژه\n⏳ تایمر')
        m.publish(self.t, self.root, OFFER['id'], lambda: self.start)
        self.bound = self.t.bind_calls

    def run_at(self, seconds):
        m.reposts(self.t, self.root, OFFER['id'], lambda: self.start + seconds)

    def sends(self):
        return [p for name, p in self.t.calls if name == 'sendPhoto']

    def test_fifteen_minutes_and_restart_deduplication_group_only(self):
        self.run_at(899)
        self.assertEqual(len(self.sends()), 1)
        self.run_at(900)
        self.run_at(900)
        self.run_at(1799)
        self.assertEqual(len(self.sends()), 2)
        self.run_at(1800)
        self.assertEqual(len(self.sends()), 3)
        self.assertTrue(all(p['chat_id'] == m.GROUP for p in self.sends()))
        self.assertEqual(self.t.bind_calls, self.bound)

    def test_live_remainder_and_rollover_link_and_photo(self):
        self.t.offer.update(remainingVolume=1.5, listUrl='https://t.me/zibasheperfume/99999',
                            telegramPhotoFileId='new-cycle-photo')
        self.run_at(900)
        sent = self.sends()[-1]
        self.assertIn('باقی‌مانده: ۱٫۵ میل', sent['caption'])
        self.assertEqual(sent['photo'], 'new-cycle-photo')
        self.assertEqual(sent['reply_markup']['inline_keyboard'][0][0]['url'], self.t.offer['listUrl'])

    def test_prior_reminder_countdown_and_remainder_refresh_until_end(self):
        self.run_at(900)
        self.t.offer.update(remainingVolume=4, caption='جدید\n⏳ تایمر جدید')
        self.run_at(960)
        edits = [p for method, p in self.t.calls if method == 'editMessageCaption']
        self.assertIn('باقی‌مانده: ۴ میل', edits[-1]['caption'])
        self.t.offer.update(isActive=False, endsAtUtc='2026-10-29T06:47:00Z', caption='پایان پروموشن')
        self.run_at(1020)
        self.assertTrue(next((self.root / 'reposts').glob('*/finished.json')).exists())
        self.assertEqual(len(self.sends()), 2)

    def test_expired_or_full_or_inactive_does_not_send(self):
        for updates in [dict(remainingVolume=0), dict(isActive=False),
                        dict(endsAtUtc='2026-10-29T06:45:00Z'),
                        dict(startsAtUtc='2026-10-30T06:30:00Z')]:
            original = copy.deepcopy(self.t.offer)
            self.t.offer.update(updates)
            self.run_at(900)
            self.t.offer = original
        self.assertEqual(len(self.sends()), 1)

    def test_tehran_midnight_to_six_and_no_catchup_burst(self):
        self.t.offer['endsAtUtc'] = '2026-10-30T04:00:00Z'
        for instant in ['2026-10-29T20:30:00Z', '2026-10-30T02:29:59Z']:
            m.reposts(self.t, self.root, OFFER['id'], lambda: m.timestamp(instant))
        self.assertEqual(len(self.sends()), 1)
        wake = m.timestamp('2026-10-30T02:30:00Z')
        for _ in range(3):
            m.reposts(self.t, self.root, OFFER['id'], lambda: wake)
        self.assertEqual(len(self.sends()), 2)
        m.reposts(self.t, self.root, OFFER['id'], lambda: wake + 899)
        self.assertEqual(len(self.sends()), 2)
        m.reposts(self.t, self.root, OFFER['id'], lambda: wake + 900)
        self.assertEqual(len(self.sends()), 3)

    def test_before_midnight_allowed(self):
        self.run_at(m.timestamp('2026-10-29T20:29:59Z') - self.start)
        self.assertEqual(len(self.sends()), 2)

    def test_unknown_delivery_blocks_later_intervals(self):
        self.t.fail_send = TimeoutError()
        with self.assertRaises(TimeoutError):
            self.run_at(900)
        self.t.fail_send = None
        with self.assertRaisesRegex(RuntimeError, 'unknown'):
            self.run_at(1800)
        self.assertEqual(len(self.sends()), 2)

    def test_acknowledged_rejection_honors_retry_after(self):
        self.t.fail_send = m.Rejected(429, retry_after=1200)
        with self.assertRaises(m.Rejected):
            self.run_at(900)
        self.t.fail_send = None
        self.run_at(2099)
        self.assertEqual(len(self.sends()), 2)
        self.run_at(2100)
        self.assertEqual(len(self.sends()), 3)

    def test_unpublished_campaign_never_starts_automatically(self):
        m.reposts(self.t, self.root / 'unpublished', OFFER['id'], lambda: self.start + 900)
        self.assertEqual(len(self.sends()), 1)

    def test_receipt_survives_crash_without_double_send(self):
        self.run_at(900)
        (next((self.root / 'reposts').glob('*/last-edit.json'))).unlink()
        self.run_at(901)
        self.assertEqual(len(self.sends()), 2)

    def test_oversized_caption_rejected_before_send_intent(self):
        self.t.offer['caption'] = 'x' * 1024
        with self.assertRaisesRegex(ValueError, 'limit'):
            self.run_at(900)
        self.assertEqual(len(self.sends()), 1)
        self.assertFalse(list((self.root / 'reposts').glob('*/send-intent.json')))


if __name__ == '__main__':
    unittest.main()
