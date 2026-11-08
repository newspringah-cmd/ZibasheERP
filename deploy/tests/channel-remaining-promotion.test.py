import copy
import fcntl
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

SCRIPT = Path(__file__).resolve().parents[1] / 'channel-remaining-promotion.py'
spec = importlib.util.spec_from_file_location('channel_promotion', SCRIPT)
promo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(promo)


def row(number=1, remainder=7):
    return {'Id': f'00000000-0000-0000-0000-{number:012d}', 'PerfumeId': f'perfume-{number}',
            'PublicCode': 9000 + number, 'StablePublicCode': None,
            'EnglishName': f'Perfume {number}', 'PersianName': 'عطر آزمایشی', 'DisplayBrand': 'Brand',
            'Accords': 'چوبی، گلی', 'TopNotes': 'سیب، رز', 'BaseNotes': 'سدر، مشک',
            'Status': 1, 'IsDeleted': False, 'IsInventoryOffer': False,
            'TotalVolume': 100, 'ReservedVolume': 100 - remainder,
            'TelegramChannelId': promo.CHANNEL, 'TelegramMessageId': 100 + number,
            'TelegramPhotoFileId': 'a-real-file-id'}


class Catalogue:
    def __init__(self, *rows):
        self.values = list(rows)
        self.calls = 0
        self.before_read = None

    def rows(self, active_id=None):
        self.calls += 1
        if self.before_read:
            self.before_read(self.calls)
        return copy.deepcopy(self.values)


class Telegram:
    def __init__(self):
        self.calls = []
        self.next_id = 1000
        self.fail_send = self.fail_edit = self.fail_delete = None

    def verify_destination(self):
        self.calls.append(('verify',))

    def send(self, value, caption):
        self.calls.append(('send', value['Id'], caption))
        if self.fail_send:
            raise self.fail_send
        self.next_id += 1
        return {'message_id': self.next_id, 'chat': {'id': int(promo.CHANNEL)}, 'date': 1000}

    def edit(self, message_id, caption, markup):
        self.calls.append(('edit', message_id, caption, markup))
        if self.fail_edit:
            raise self.fail_edit
        return True

    def delete(self, message_id):
        self.calls.append(('delete', message_id))
        if self.fail_delete:
            raise self.fail_delete
        return True


class PromotionTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.store = promo.Store(self.directory.name)
        self.catalogue = Catalogue(row(), row(2, 9))
        self.telegram = Telegram()
        self.engine = promo.Engine(self.store, self.catalogue, self.telegram, recover=lambda ad: None)

    def sent(self):
        self.assertEqual(self.engine.tick(1000)['status'], 'sent')
        return self.store.data['active']['message_id']

    def count(self, method):
        return sum(call[0] == method for call in self.telegram.calls)

    def test_admin_removal_deletes_only_ad_and_survives_future_ticks(self):
        message_id = self.sent()
        result = self.engine.pause(1060, 'remove-1')
        self.assertEqual(result['status'], 'paused')
        self.assertEqual(result['outcome'], 'deleted')
        self.assertTrue(self.store.data['paused'])
        self.assertIn(('delete', message_id), self.telegram.calls)
        self.assertNotIn(('delete', row()['TelegramMessageId']), self.telegram.calls)
        self.assertEqual(self.engine.tick(1120)['status'], 'paused')
        restarted = promo.Engine(promo.Store(self.directory.name), self.catalogue, self.telegram)
        self.assertEqual(restarted.tick(1180)['status'], 'paused')
        self.assertEqual(self.count('send'), 1)

    def test_empty_channel_pause_clears_pending_selection(self):
        self.select(row(3, 42))
        self.assertEqual(self.engine.pause(1000, 'remove-empty')['outcome'], 'nothing_to_delete')
        self.assertNotIn('selection', self.store.data)
        self.assertEqual(self.engine.tick(1060)['status'], 'paused')
        self.assertEqual(self.count('send'), 0)

    def test_delete_rate_limit_keeps_pause_and_retries_without_replacement(self):
        self.sent()
        self.telegram.fail_delete = promo.TelegramFailure(429, retry_after=120)
        self.assertEqual(self.engine.pause(1060, 'remove-rate')['status'], 'delete_retry')
        self.assertTrue(self.store.data['paused'])
        self.assertEqual(self.engine.tick(1120)['status'], 'retry_wait')
        self.telegram.fail_delete = None
        self.assertEqual(self.engine.tick(1180)['status'], 'paused')
        self.assertEqual(self.count('send'), 1)

    def test_explicit_selection_resumes_and_old_removal_cannot_delete_new_ad(self):
        self.sent(); self.engine.pause(1060, 'remove-old')
        self.select(row(3, 42))
        self.assertEqual(self.engine.tick(1180)['status'], 'sent')
        new_id = self.store.data['active']['message_id']
        self.assertFalse(self.store.data['paused'])
        self.assertEqual(self.engine.pause(1200, 'remove-old')['status'], 'pause_already_completed')
        self.assertEqual(self.engine.pause(1200, 'outdated-remove', requested_at=1000)['status'], 'pause_superseded')
        self.assertEqual(self.store.data['active']['message_id'], new_id)
        self.assertEqual(self.count('delete'), 1)

    def test_selection_queued_before_pause_does_not_restart_automation(self):
        self.engine.pause(1060, 'remove-before')
        self.select(row(3, 42))
        self.store.data['selection']['requested_at'] = 1000
        self.assertEqual(self.engine.tick(1120)['status'], 'selection_superseded_by_pause')
        self.assertTrue(self.store.data['paused'])
        self.assertEqual(self.count('send'), 0)

    def test_ambiguous_old_send_is_recovered_before_removal_without_new_send(self):
        self.sent(); ad = self.store.data['active']
        ad['phase'], ad['message_id'] = 'unknown', None
        self.assertEqual(self.engine.pause(1060, 'remove-unknown')['status'], 'delivery_unknown_no_duplicate_sent')
        self.assertTrue(self.store.data['paused'])
        self.engine.recover = lambda ad: {'message_id': 1001, 'date': 1000}
        self.assertEqual(self.engine.tick(1120)['status'], 'paused')
        self.assertEqual(self.count('send'), 1)
        self.assertIn(('delete', 1001), self.telegram.calls)

    def test_undeletable_aged_ad_is_archived_and_reported_truthfully(self):
        self.sent()
        self.telegram.fail_delete = promo.TelegramFailure(400, "message can't be deleted")
        result = self.engine.pause(1060, 'remove-aged')
        self.assertEqual(result['outcome'], 'expired_message_archived')
        self.assertTrue(self.store.data['paused'])
        self.assertEqual(self.count('edit'), 1)
        self.assertEqual(self.count('send'), 1)
        self.assertEqual(self.store.data['removals'][-1]['outcome'], 'expired_message_archived')

    def append(self, number=3, remainder=42):
        self.select(row(number, remainder))
        self.store.data['selection'].update(mode='append', request_id='append-'+str(number))
        return self.engine.tick(1060+number)

    def test_append_keeps_multiple_ads_and_completion_removes_only_matching_ad(self):
        first = self.sent()
        self.assertEqual(self.append()['status'], 'sent')
        second = self.store.data['active']['message_id']
        self.assertEqual(len(promo.active_ads(self.store.data)), 2)
        self.assertEqual(self.count('delete'), 0)
        self.catalogue.values[0]['ReservedVolume'] = 100
        self.assertEqual(self.engine.tick(1120)['status'], 'monitoring')
        self.assertIn(('delete', first), self.telegram.calls)
        self.assertNotIn(('delete', second), self.telegram.calls)
        self.assertEqual(len(promo.active_ads(self.store.data)), 1)
        self.assertEqual(self.count('send'), 2)

    def test_append_existing_ad_does_not_post_duplicate_or_delete_other_ads(self):
        first = self.sent(); self.append()
        self.store.data['selection'] = {'list_id':row()['Id'], 'manual':True, 'mode':'append', 'request_id':'reselect'}
        self.engine.tick(1120)
        self.assertEqual(self.count('send'), 2)
        self.assertEqual(self.count('delete'), 0)
        self.assertEqual(self.store.data['active']['message_id'], first)
        self.assertEqual(len(promo.active_ads(self.store.data)), 2)

    def test_replace_removes_all_prior_ads_and_retains_selected_existing_ad(self):
        first = self.sent(); self.append(); self.append(4)
        self.store.data['selection'] = {'list_id':row()['Id'], 'manual':True, 'mode':'replace', 'request_id':'replace'}
        self.engine.tick(1180)
        self.assertEqual(self.count('send'), 3)
        self.assertEqual(self.count('delete'), 2)
        self.assertEqual(self.store.data['active']['message_id'], first)
        self.assertEqual(len(promo.active_ads(self.store.data)), 1)

    def test_three_ads_survive_restart_and_update_their_own_remaining_volume(self):
        self.sent(); self.append(); self.append(4)
        self.catalogue.values[0]['ReservedVolume'] = 98
        restarted_store = promo.Store(self.directory.name)
        restarted = promo.Engine(restarted_store, self.catalogue, self.telegram)
        restarted.tick(1180)
        ads = promo.active_ads(restarted_store.data)
        self.assertEqual(len(ads), 3)
        self.assertEqual(promo.remaining(next(ad for ad in ads if ad['row']['Id']==row()['Id'])['row']), 2)
        self.assertEqual(self.count('send'), 3)

    def test_primary_completion_does_not_delete_retained_ad_or_duplicate_it_automatically(self):
        first = self.sent(); self.append()
        second = self.store.data['active']['message_id']
        self.catalogue.values[-1]['ReservedVolume'] = 100
        self.engine.tick(1180)
        self.assertIn(('delete', second), self.telegram.calls)
        self.assertNotIn(('delete', first), self.telegram.calls)
        ads = promo.active_ads(self.store.data)
        self.assertEqual(len({ad['row']['Id'] for ad in ads}), len(ads))

    def test_pause_removes_every_tracked_ad_and_does_not_repopulate(self):
        self.sent(); self.append(); self.append(4)
        result = self.engine.pause(1180, 'remove-all')
        self.assertEqual(result['status'], 'paused')
        self.assertEqual(self.count('delete'), 3)
        self.assertEqual(promo.active_ads(self.store.data), [])
        self.engine.tick(1240)
        self.assertEqual(self.count('send'), 3)

    def test_one_aged_retained_ad_reports_archiving_even_when_other_ads_delete(self):
        first = self.sent(); self.append()
        original = self.telegram.delete
        def delete(message_id):
            if message_id == first:
                raise promo.TelegramFailure(400, "message can't be deleted")
            return original(message_id)
        self.telegram.delete = delete
        result = self.engine.pause(1180, 'remove-aged-all')
        self.assertEqual(result['outcome'], 'expired_message_archived')
        self.assertEqual(promo.active_ads(self.store.data), [])
        self.assertEqual(self.count('send'), 2)

    def test_unknown_new_send_preserves_old_ad_and_still_cleans_its_completion(self):
        first = self.sent()
        self.telegram.fail_send = promo.TelegramFailure(unknown=True)
        self.assertEqual(self.append()['status'], 'delivery_unknown_no_duplicate_sent')
        self.catalogue.values[0]['ReservedVolume'] = 100
        self.engine.tick(1180)
        self.assertIn(('delete', first), self.telegram.calls)
        self.assertEqual(self.count('send'), 2)
        self.assertEqual(len(promo.active_ads(self.store.data)), 1)

    def test_retained_ad_deadline_deletes_only_that_ad(self):
        first = self.sent(); self.append()
        self.store.data['additional'][0]['active']['expires_at'] = 1120
        self.engine.tick(1180)
        self.assertIn(('delete', first), self.telegram.calls)
        self.assertEqual(self.count('send'), 2)
        self.assertEqual(len(promo.active_ads(self.store.data)), 1)

    def test_ambiguous_retained_ad_does_not_block_another_completed_ad(self):
        self.sent(); self.append(); self.append(4)
        old = self.store.data['additional'][0]['active']
        old.update(phase='unknown', message_id=None)
        completed = self.store.data['additional'][1]['active']['message_id']
        self.catalogue.values[2]['ReservedVolume'] = 100
        self.engine.tick(1180)
        self.assertIn(('delete', completed), self.telegram.calls)
        self.assertEqual(self.count('send'), 3)
        self.assertEqual(len(promo.active_ads(self.store.data)), 2)

    def test_delayed_replace_cannot_remove_ads_selected_by_newer_request(self):
        self.sent(); self.append()
        self.select(row(4, 42))
        self.store.data['selection'].update(mode='replace', requested_at=1000)
        self.assertEqual(self.engine.tick(1180)['status'], 'selection_superseded_by_newer_request')
        self.assertEqual(self.count('delete'), 0)
        self.assertEqual(len(promo.active_ads(self.store.data)), 2)

    def test_delayed_append_cannot_undo_a_newer_replace_choice(self):
        self.sent(); self.select(row(3, 42)); self.engine.tick(1120)
        self.select(row(4, 42))
        self.store.data['selection'].update(mode='append', requested_at=1000)
        self.assertEqual(self.engine.tick(1180)['status'], 'selection_superseded_by_newer_request')
        self.assertEqual(self.count('send'), 2)
        self.assertEqual(len(promo.active_ads(self.store.data)), 1)

    def test_strict_under_ten_boundaries(self):
        for value in [1, 2, 9, 0.5]:
            self.assertTrue(promo.eligible(row(remainder=value)))
        for value in [-1, 0, 10, 11]:
            self.assertFalse(promo.eligible(row(remainder=value)))

    def test_invalid_deleted_or_foreign_posts_never_advertised(self):
        for key, value in [('Status', 2), ('IsDeleted', True), ('IsInventoryOffer', True),
                           ('TelegramChannelId', '-100OTHER'), ('TelegramPhotoFileId', None),
                           ('TelegramMessageId', None)]:
            value_row = row(); value_row[key] = value
            self.assertFalse(promo.eligible(value_row), key)

    def test_caption_escapes_html_and_uses_actual_remaining_and_source(self):
        value = row(remainder=3); value['PersianName'] = 'عطر <خاص> & خوشبو'
        text = promo.caption(value)
        self.assertIn('۳ میل', text)
        self.assertIn('&lt;خاص&gt; &amp;', text)
        self.assertIn('۹۰۰۱', text)
        self.assertNotIn('تومان', text)
        self.assertEqual(promo.markup(value)['inline_keyboard'][0][0]['url'],
                         'https://t.me/zibasheperfume/101')

    def test_caption_stays_within_telegram_limit(self):
        value = row()
        for key in ['PersianName', 'EnglishName', 'DisplayBrand', 'Accords', 'TopNotes', 'BaseNotes']:
            value[key] = 'عطر🌸& ' * 250
        self.assertLessEqual(len(promo.caption(value).encode('utf-16-le')) // 2, 1024)

    def test_same_ad_updated_when_remaining_changes_no_repost(self):
        message_id = self.sent()
        self.catalogue.values[0]['ReservedVolume'] = 97
        self.assertEqual(self.engine.tick(1060)['status'], 'updated')
        edit = self.telegram.calls[-1]
        self.assertEqual(edit[:2], ('edit', message_id))
        self.assertIn('۳ میل', edit[2])
        self.assertEqual(self.count('send'), 1)
        self.assertEqual(self.engine.tick(1120)['status'], 'monitoring')
        self.assertEqual(self.count('edit'), 1)

    def test_completion_deletes_only_ad_then_publishes_different_perfume(self):
        message_id = self.sent()
        self.catalogue.values[0]['ReservedVolume'] = 100
        self.catalogue.values[0]['Status'] = 2
        self.assertEqual(self.engine.tick(1060)['status'], 'sent')
        self.assertEqual(self.store.data['active']['row']['Id'], row(2)['Id'])
        self.assertIn(('delete', message_id), self.telegram.calls)
        self.assertNotIn(('delete', row()['TelegramMessageId']), self.telegram.calls)
        delete_index = self.telegram.calls.index(('delete', message_id))
        self.assertLess(delete_index, max(i for i, call in enumerate(self.telegram.calls) if call[0] == 'send'))

    def test_no_candidates_waits_and_resumes_without_expiry(self):
        self.catalogue.values = []
        self.assertEqual(self.engine.tick(1000)['status'], 'waiting_for_under_10_ml')
        self.assertEqual(self.engine.tick(1000 + 900 * 86400)['status'], 'waiting_for_under_10_ml')
        self.catalogue.values = [row(remainder=2)]
        self.assertEqual(self.engine.tick(1000 + 901 * 86400)['status'], 'sent')

    def test_closed_cycle_can_clear_original_channel_ids_without_blocking_cleanup(self):
        message_id = self.sent()
        self.catalogue.values[0].update(ReservedVolume=100, Status=5,
                                         TelegramChannelId=None, TelegramMessageId=None)
        self.assertEqual(self.engine.tick(1060)['status'], 'sent')
        self.assertIn(('delete', message_id), self.telegram.calls)
        self.assertEqual(self.store.data['active']['row']['Id'], row(2)['Id'])

    def test_remaining_rises_to_ten_ad_removed_without_false_completion_claim(self):
        self.sent()
        self.catalogue.values[0]['ReservedVolume'] = 90
        self.assertEqual(self.engine.tick(1060)['status'], 'sent')
        self.assertEqual(self.store.data['retired'][-1]['reason'], 'not_eligible')

    def test_failed_edit_retries_only_presentation_not_send(self):
        self.sent()
        self.catalogue.values[0]['ReservedVolume'] = 98
        self.telegram.fail_edit = promo.TelegramFailure(unknown=True)
        self.assertEqual(self.engine.tick(1060)['status'], 'caption_retry')
        self.assertIn('۷ میل', self.store.data['active']['caption'])
        self.telegram.fail_edit = None
        self.assertEqual(self.engine.tick(1120)['status'], 'updated')
        self.assertIn('۲ میل', self.store.data['active']['caption'])
        self.assertEqual(self.count('send'), 1)

    def select(self, value):
        self.catalogue.values.append(value)
        self.store.data['selection'] = {'list_id': value['Id'], 'manual': True}
        self.store.save()

    def test_explicit_ten_ml_selection_replaces_current_ad(self):
        old_id = self.sent()
        selected = row(3, 10)
        self.select(selected)
        self.assertFalse(promo.eligible(selected))
        result = self.engine.tick(1060)
        self.assertEqual(result['status'], 'sent')
        self.assertEqual(self.store.data['active']['row']['Id'], selected['Id'])
        self.assertTrue(self.store.data['active']['manual_selection'])
        self.assertNotIn('selection', self.store.data)
        self.assertIn(('delete', old_id), self.telegram.calls)
        self.assertIn('۱۰ میل', self.store.data['active']['caption'])

    def test_timed_selection_updates_and_survives_restart_then_expires(self):
        self.select(row(3, 42))
        self.store.data['selection']['expires_at'] = 87400
        self.store.save()
        self.assertEqual(self.engine.tick(1000)['status'], 'sent')
        old_id = self.store.data['active']['message_id']
        self.assertEqual(self.store.data['active']['expires_at'], 87400)
        store = promo.Store(self.directory.name)
        engine = promo.Engine(store, self.catalogue, self.telegram)
        self.catalogue.values[-1]['ReservedVolume'] = 60
        self.assertEqual(engine.tick(87399)['status'], 'updated')
        self.assertEqual(store.data['active']['message_id'], old_id)
        self.assertEqual(engine.tick(87400)['status'], 'sent')
        self.assertNotEqual(store.data['active']['row']['Id'], row(3)['Id'])
        self.assertEqual(store.data['retired'][-1]['reason'], 'manual_expired')
        self.assertIn(('delete', old_id), self.telegram.calls)
        self.assertTrue(promo.eligible(store.data['active']['row']))

    def test_timed_selection_of_current_ad_sets_deadline_without_duplicate(self):
        self.sent()
        self.store.data['selection'] = {'list_id': row()['Id'], 'manual': True, 'expires_at': 2000}
        self.assertEqual(self.engine.tick(1060)['status'], 'monitoring')
        self.assertEqual(self.store.data['active']['expires_at'], 2000)
        self.assertEqual(self.count('send'), 1)
        self.assertEqual(self.engine.tick(2000)['status'], 'sent')
        self.assertEqual(self.count('delete'), 1)

    def test_completion_before_deadline_still_removes_ad(self):
        self.select(row(3, 42))
        self.store.data['selection']['expires_at'] = 87400
        self.assertEqual(self.engine.tick(1000)['status'], 'sent')
        self.catalogue.values[-1]['ReservedVolume'] = 100
        self.assertEqual(self.engine.tick(1060)['status'], 'sent')
        self.assertEqual(self.store.data['retired'][-1]['reason'], 'completed')
        self.assertTrue(promo.eligible(self.store.data['active']['row']))

    def test_manually_selected_ad_keeps_updating_above_threshold(self):
        self.select(row(3, 10))
        self.assertEqual(self.engine.tick(1000)['status'], 'sent')
        self.assertEqual(self.engine.tick(1060)['status'], 'monitoring')
        self.catalogue.values[-1]['ReservedVolume'] = 88
        self.assertEqual(self.engine.tick(1120)['status'], 'updated')
        self.assertIn('۱۲ میل', self.store.data['active']['caption'])
        self.catalogue.values[-1]['ReservedVolume'] = 92
        self.assertEqual(self.engine.tick(1180)['status'], 'updated')
        self.assertIn('۸ میل', self.store.data['active']['caption'])
        self.assertEqual(self.count('send'), 1)

    def test_manual_exception_ends_on_completion_next_choice_stays_under_ten(self):
        self.select(row(3, 10))
        self.engine.tick(1000)
        self.catalogue.values[-1]['ReservedVolume'] = 100
        self.catalogue.values.append(row(4, 15))
        self.assertEqual(self.engine.tick(1060)['status'], 'sent')
        self.assertFalse(self.store.data['active']['manual_selection'])
        self.assertLess(promo.remaining(self.store.data['active']['row']), 10)

    def test_manual_exception_survives_renewal_and_restart(self):
        self.select(row(3, 10))
        self.engine.tick(1000)
        self.assertEqual(self.engine.tick(1000 + promo.RENEW_AFTER)['status'], 'sent')
        store = promo.Store(self.directory.name)
        self.assertTrue(store.data['active']['manual_selection'])
        restarted = promo.Engine(store, self.catalogue, self.telegram)
        # Fake receipts use a fixed date; set the renewed receipt to its actual send time.
        store.data['active']['sent_at'] = 1000 + promo.RENEW_AFTER
        self.assertEqual(restarted.tick(1060 + promo.RENEW_AFTER)['status'], 'monitoring')
        self.assertEqual(store.data['active']['row']['Id'], row(3)['Id'])

    def test_queued_selection_survives_restart_before_replacement(self):
        self.sent()
        self.select(row(3, 10))
        store = promo.Store(self.directory.name)
        restarted = promo.Engine(store, self.catalogue, self.telegram)
        self.assertEqual(restarted.tick(1060)['status'], 'sent')
        self.assertEqual(store.data['active']['row']['Id'], row(3)['Id'])

    def test_unavailable_manual_target_preserves_current_live_ad(self):
        old_id = self.sent()
        self.select(row(3, 0))
        self.assertEqual(self.engine.tick(1060)['status'], 'selected_list_no_longer_available')
        self.assertEqual(self.store.data['active']['message_id'], old_id)
        self.assertEqual(self.count('delete'), 0)

    def test_selecting_current_list_does_not_repost(self):
        old_id = self.sent()
        self.store.data['selection'] = {'list_id': row()['Id'], 'manual': True}
        self.assertEqual(self.engine.tick(1060)['status'], 'monitoring')
        self.assertEqual(self.store.data['active']['message_id'], old_id)
        self.assertTrue(self.store.data['active']['manual_selection'])
        self.assertEqual(self.count('send'), 1)

    def test_restart_preserves_single_active_ad(self):
        self.sent()
        store = promo.Store(self.directory.name)
        engine = promo.Engine(store, self.catalogue, self.telegram)
        self.assertEqual(engine.tick(1060)['status'], 'monitoring')
        self.assertEqual(self.count('send'), 1)
        self.assertEqual(oct(store.path.stat().st_mode & 0o777), '0o600')

    def test_unknown_send_is_journaled_and_never_blindly_retried(self):
        self.telegram.fail_send = promo.TelegramFailure(unknown=True)
        self.assertEqual(self.engine.tick(1000)['status'], 'delivery_unknown_no_duplicate_sent')
        store = promo.Store(self.directory.name)
        self.assertEqual(store.data['active']['phase'], 'unknown')
        restarted = promo.Engine(store, self.catalogue, self.telegram, recover=lambda ad: None)
        self.assertEqual(restarted.tick(1060)['status'], 'delivery_unknown_no_duplicate_sent')
        self.assertEqual(self.count('send'), 1)

    def test_intent_persisted_before_network_and_recovered_after_crash(self):
        def interrupted_send(value, text):
            self.assertEqual(promo.Store(self.directory.name).data['active']['phase'], 'sending')
            raise RuntimeError('simulated process interruption')
        self.telegram.send = interrupted_send
        with self.assertRaises(RuntimeError):
            self.engine.tick(1000)
        store = promo.Store(self.directory.name)
        recovered = promo.Engine(store, self.catalogue, self.telegram,
                                 recover=lambda ad: {'message_id': 1900, 'date': 1000})
        self.assertEqual(recovered.tick(1060)['status'], 'monitoring')
        self.assertEqual(store.data['active']['message_id'], 1900)

    def test_deletion_timeout_retries_deletion_before_any_replacement(self):
        old_id = self.sent()
        self.catalogue.values[0]['ReservedVolume'] = 100
        self.telegram.fail_delete = promo.TelegramFailure(unknown=True)
        self.assertEqual(self.engine.tick(1060)['status'], 'delete_retry')
        self.assertEqual(self.count('send'), 1)
        self.telegram.fail_delete = promo.TelegramFailure(400, 'message to delete not found')
        self.assertEqual(self.engine.tick(1120)['status'], 'sent')
        self.assertEqual(self.store.data['retired'][-1]['message_id'], old_id)
        self.assertEqual(self.count('send'), 2)

    def test_telegram_rate_limit_is_respected_across_restart(self):
        self.telegram.fail_send = promo.TelegramFailure(429, retry_after=600)
        self.assertEqual(self.engine.tick(1000)['status'], 'send_rejected_retry_later')
        restarted = promo.Engine(promo.Store(self.directory.name), self.catalogue, self.telegram)
        self.assertEqual(restarted.tick(1060)['status'], 'retry_wait')
        self.assertEqual(self.count('send'), 1)
        self.telegram.fail_send = None
        self.assertEqual(restarted.tick(1600)['status'], 'sent')

    def test_renew_before_48_hours_keeps_message_deletable(self):
        self.sent()
        self.assertEqual(self.engine.tick(1000 + promo.RENEW_AFTER)['status'], 'sent')
        self.assertEqual(self.store.data['active']['row']['Id'], row()['Id'])
        self.assertEqual(self.store.data['retired'][-1]['reason'], 'renewal')

    def test_long_outage_archives_expired_caption_and_removes_buttons(self):
        self.sent()
        self.catalogue.values[0]['ReservedVolume'] = 100
        self.telegram.fail_delete = promo.TelegramFailure(400, "message can't be deleted")
        self.assertEqual(self.engine.tick(1000 + 72 * 3600)['status'], 'sent')
        edit = next(call for call in self.telegram.calls if call[0] == 'edit')
        self.assertIn('تکمیل شد', edit[2])
        self.assertEqual(edit[3], {'inline_keyboard': []})
        self.assertEqual(self.store.data['retired'][-1]['outcome'], 'expired_message_archived')

    def test_stale_selection_is_rechecked_before_send(self):
        def advance(call):
            if call == 2:
                self.catalogue.values[0]['ReservedVolume'] = 100
        self.catalogue.before_read = advance
        self.assertEqual(self.engine.tick(1000)['status'], 'candidate_changed_retry_next_minute')
        self.assertEqual(self.count('send'), 0)

    def test_corrupt_receipt_cannot_delete_original_sales_post(self):
        self.sent()
        ad = self.store.data['active']
        ad['message_id'] = ad['row']['TelegramMessageId']
        self.catalogue.values[0]['ReservedVolume'] = 100
        with self.assertRaises(ValueError):
            self.engine.tick(1060)
        self.assertEqual(self.count('delete'), 0)

    def test_no_overlap_when_cron_and_manual_run_collide(self):
        with (Path(self.directory.name) / 'run.lock').open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            result = subprocess.run([sys.executable, str(SCRIPT), '--state-dir', self.directory.name, '--preview'],
                                    capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stdout, '')

    def test_public_receipt_parser_keeps_caption_and_timestamp(self):
        parser = promo.PreviewParser()
        parser.feed('<div class="tgme_widget_message" data-post="zibasheperfume/800">'
                    '<div class="tgme_widget_message_text js-message_text"><b>عطر &amp; گل</b><br>فقط ۳ میل 🌸</div>'
                    '<a><time datetime="2026-10-28T10:00:00+00:00">10:00</time></a></div>')
        self.assertEqual(parser.messages[0]['message_id'], 800)
        self.assertEqual(parser.messages[0]['text'], 'عطر & گل\nفقط ۳ میل 🌸')
        self.assertGreater(parser.messages[0]['date'], 0)

    def test_network_errors_do_not_expose_bot_token(self):
        with patch.object(promo.urllib.request, 'urlopen', side_effect=urllib.error.URLError('SECRET_TOKEN')):
            with self.assertRaises(promo.TelegramFailure) as caught:
                promo.Telegram('SECRET_TOKEN').call('sendPhoto', {})
        self.assertNotIn('SECRET_TOKEN', str(caught.exception))
        self.assertTrue(caught.exception.unknown)


class CustomCaptionTests(unittest.TestCase):
    setUp=PromotionTests.setUp
    sent=PromotionTests.sent
    count=PromotionTests.count
    def test_custom_caption_survives_remainder_edits_and_restart(self):
        self.store.data['selection']={'list_id':row()['Id'],'manual':True,'expires_at':10000,'request_id':'own','custom_text':'متن اختصاصی <tag>\nخط دوم'}
        self.sent();self.catalogue.values[0]['ReservedVolume']=96
        restarted=promo.Engine(promo.Store(self.directory.name),self.catalogue,self.telegram,recover=lambda ad:None)
        self.assertEqual(restarted.tick(1060)['status'],'updated')
        text=restarted.store.data['active']['caption']
        self.assertIn('متن اختصاصی &lt;tag&gt;\nخط دوم',text);self.assertIn('۴ میل',text)
        self.assertEqual(self.count('send'),1)

    def test_rechoosing_auto_text_clears_previous_custom_caption(self):
        self.store.data['selection']={'list_id':row()['Id'],'manual':True,'expires_at':10000,'request_id':'own','custom_text':'CUSTOM COPY'}
        self.sent()
        self.store.data['selection']={'list_id':row()['Id'],'manual':True,'expires_at':10000,'request_id':'auto'}
        self.engine.tick(1060)
        self.assertIsNone(self.store.data['active']['custom_text']);self.assertNotIn('CUSTOM COPY',self.store.data['active']['caption'])
        self.assertEqual(self.count('send'),1)

    def test_custom_caption_still_retires_completed_list(self):
        self.store.data['selection']={'list_id':row()['Id'],'manual':True,'expires_at':10000,'request_id':'own','custom_text':'CUSTOM COPY'}
        message=self.sent();self.catalogue.values[0]['ReservedVolume']=100
        self.engine.tick(1060)
        self.assertIn(('delete',message),self.telegram.calls)


if __name__ == '__main__':
    unittest.main()
