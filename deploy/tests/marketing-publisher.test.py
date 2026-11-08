import importlib.util
import json
from pathlib import Path
import unittest
import tempfile
import contextlib
import io
import sqlite3
import time
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('publisher', Path(__file__).resolve().parents[1] / 'marketing-publisher.py')
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


class PublisherTests(unittest.TestCase):
    def row(self, remaining=5):
        return dict(IsDeleted=False, IsInventoryOffer=False, Status=1, TotalVolume=100,
                    ReservedVolume=100-remaining, TelegramPhotoFileId='photo', TelegramMessageId=123,
                    TelegramChannelId='-100123', PublicCode=42, PersianName='<عطر>', EnglishName='Test & Perfume')

    def test_exact_remainder_boundaries(self):
        for remaining in (1, 5, 9):
            self.assertTrue(publisher.eligible(self.row(remaining)))
        for remaining in (0, 10, 11, -1):
            self.assertFalse(publisher.eligible(self.row(remaining)))

    def test_closed_deleted_and_invalid_posts(self):
        for key, value in [('Status', 2), ('IsDeleted', True), ('IsInventoryOffer', True),
                           ('TelegramPhotoFileId', None), ('TelegramMessageId', None), ('TelegramChannelId', 'invalid')]:
            row = self.row(); row[key] = value
            self.assertFalse(publisher.eligible(row))

    def test_group_exclusion_rejects_code_and_rolled_cycle(self):
        policy = {'codes': {7888}, 'list_ids': set()}
        for codes in [(7888, None), (90001, 7888), (7888, 90002)]:
            row = self.row(); row.update(PublicCode=codes[0], StablePublicCode=codes[1])
            for maximum in (10, 20, 2**31):
                self.assertFalse(publisher.eligible(row, maximum, policy))

    def test_group_exclusion_matches_id_when_code_changes(self):
        identity = '993D4AAB-9165-4CB8-BDF5-4A9A6D86071E'
        row = self.row(); row['Id'] = identity
        self.assertFalse(publisher.eligible(row, exclusions={'codes': set(), 'list_ids': {identity.lower()}}))
        self.assertTrue(publisher.eligible(row, exclusions={'codes': {7888}, 'list_ids': set()}))

    def test_persistent_exclusion_is_loaded_by_default_without_affecting_other_lists(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(publisher, 'STATE', Path(directory)):
            path = Path(directory) / 'excluded-lists.json'
            path.write_text(json.dumps({'version': 1, 'chat_id': publisher.CHAT,
                                        'codes': [7888], 'list_ids': []}))
            blocked = self.row(); blocked['PublicCode'] = 7888
            self.assertFalse(publisher.eligible(blocked))
            self.assertTrue(publisher.eligible(self.row()))
            self.assertEqual(publisher.load_exclusions()['codes'], {7888})

    def test_malformed_or_foreign_group_policy_stops_advertising(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(publisher, 'STATE', Path(directory)):
            path = Path(directory) / 'excluded-lists.json'
            for data in [{'version': 1, 'chat_id': '-100OTHER', 'codes': [7888]},
                         {'version': 1, 'chat_id': publisher.CHAT, 'codes': ['7888']},
                         {'version': 1, 'chat_id': publisher.CHAT, 'codes': [7888], 'list_ids': ['bad-id']}]:
                path.write_text(json.dumps(data))
                with self.assertRaises(ValueError):
                    publisher.eligible(self.row())

    def test_missing_policy_keeps_existing_selection_rules(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(publisher, 'STATE', Path(directory)):
            self.assertTrue(publisher.eligible(self.row(5)))
            self.assertFalse(publisher.eligible(self.row(10)))

    def run_selection(self, history, code=42, publish=False, photo_count=0):
        with tempfile.TemporaryDirectory() as directory, patch.object(publisher, 'STATE', Path(directory)):
            row=self.row();row.update(Id='00000000-0000-0000-0000-000000000042',DisplayBrand='Test',PublicCode=code)
            key=publisher.perfume_frequency_key(row);now=time.time()
            with sqlite3.connect(Path(directory)/'history.sqlite') as db:
                db.execute('CREATE TABLE slots(slot INTEGER PRIMARY KEY,status TEXT,list_id TEXT,at REAL,message_id INTEGER,error TEXT,perfume_key TEXT)')
                for i,(status,age) in enumerate(history):
                    db.execute('INSERT INTO slots VALUES(?,?,?,?,?,?,?)',(-100-i,status,row['Id'],now-age,1,None,key))
            (Path(directory)/'excluded-lists.json').write_text(json.dumps({'version':1,'chat_id':publisher.CHAT,'codes':[7888],'list_ids':[]}))
            content={'reviews_found':True,'analysis':'یه رایحه گلی و چوبی با حال‌وهوای گرم و دلنشین',
                     'positive_reviews':'بعضی‌ها از ترکیب گل و چوب و حس نرمش خوششون اومده',
                     'sources':['https://www.fragrantica.com/perfume/Test/Exact-42.html']}
            out=io.StringIO()
            images=SimpleNamespace(prepare=lambda *args:([Path(directory)/str(i) for i in range(photo_count)],'synthetic'),
                send_album=lambda *args:list(range(700,700+photo_count)))
            with patch('sys.argv',['publisher']+([] if publish else ['--dry-run'])), patch.object(publisher,'config',return_value={'Telegram__SilentGroupChatIds':publisher.CHAT,'Telegram__BotToken':'synthetic-only'}), \
                 patch.object(publisher,'query',return_value=[row]), patch.object(publisher,'research',return_value=content), \
                 patch.object(publisher,'image_provider',return_value=images), \
                 patch.object(publisher,'post',return_value={'ok':True,'result':{'message_id':600}}), \
                 contextlib.redirect_stdout(out):
                publisher.main()
            return [json.loads(line) for line in out.getvalue().splitlines()] if publish else json.loads(out.getvalue().strip())

    def test_automatic_advertisement_includes_two_photo_fallback_album(self):
        output=self.run_selection([],publish=True,photo_count=2)
        self.assertTrue(any(line.get('official_album')=='sent' and line.get('message_ids')==[700,701] for line in output))

    def test_previous_ad_three_days_ago_no_longer_blocks_direct_selection(self):
        self.assertEqual(self.run_selection([('sent',3*86400)])['status'],'validated_without_posting')

    def test_two_ads_in_24h_still_block_direct_selection(self):
        result=self.run_selection([('sent',3600),('sent',7200)])
        self.assertEqual(result['reason'],'daily_perfume_limit')

    def test_recent_failed_attempt_backoff_remains(self):
        self.assertEqual(self.run_selection([('failed',3600)])['status'],'no_new_eligible_list')

    def test_explicit_group_exclusion_remains_in_main_selection(self):
        self.assertEqual(self.run_selection([],7888)['status'],'no_new_eligible_list')

    def test_content_needs_actual_reviews(self):
        content = dict(reviews_found=True, analysis='یه رایحه گلی و چوبی با حال‌وهوای گرم و دلنشین',
                       positive_reviews='بعضی‌ها از ترکیب رز و چوب و حس نرمش خوششون اومده',
                       sources=['https://www.fragrantica.com/perfume/Test/Exact-42.html'])
        publisher.validate_content(content)
        publisher.validate_content({**content, 'analysis': 'رز و گل صدتومانی در کنار مشک، یه حس گلی و نرم بهت می‌دن'})
        for key, value in [('reviews_found', False), ('sources', ['https://example.org']),
                           ('analysis', 'قیمت این عطر هزار تومان است'), ('analysis', 'Try https://example.org for more details'),
                           ('positive_reviews', 'سه نمونه با مقدار ۵ میل در نظر بگیر')]:
            bad = {**content, key: value}
            with self.assertRaises(ValueError): publisher.validate_content(bad)

    def test_format_has_live_remainder_but_no_price(self):
        text = publisher.caption(self.row(4), dict(analysis='رایحه دلنشین', positive_reviews='حس نرم و دوست‌داشتنی'))
        self.assertIn('4 میل', text)
        self.assertIn('&lt;عطر&gt;', text)
        self.assertNotIn('تومان', text)
        self.assertNotIn('قیمت', text)

    def test_fragrantica_image_origins_and_duplicates(self):
        images = publisher.image_provider()
        self.assertTrue(images.source_page('https://www.fragrantica.com/perfume/Nasomatto/Nudiflorum-49223.html', ['fragrantica.com']))
        for url in ('https://fragrantica.com.evil.com/image', 'http://fragrantica.com/image', 'https://retailer.com/image'):
            self.assertFalse(images.source_page(url, ['fragrantica.com']))
        page = '<img src="https://cdn.example.com/a.jpg?width=500"><img src="https://cdn.example.com/a.jpg?width=900"><img src="//cdn.example.com/b.png">'
        self.assertEqual(len(images.image_urls(page, 'https://brand.com/product')), 2)
        for url in ('http://example.com/a.jpg', 'https://user:password@example.com/a.jpg', 'https://example.com:8443/a.jpg', 'https://127.0.0.1/a.jpg'):
            with self.assertRaises(ValueError): images.public_url(url)
        with self.assertRaises(ValueError): images.send_album({}, '-1001', [])

    def test_album_requires_fragram_wearer_and_orders_it_last(self):
        images = publisher.image_provider()
        self.assertEqual(images.album_selection({'selected_ids': [3, 0, 1], 'product_ids': [0, 1], 'lifestyle_id': 3}, 4), [0, 1, 3])
        for verdict in ({'selected_ids': [3, 0, 1], 'lifestyle_id': 3},
                        {'selected_ids': [0, 1, 2], 'product_ids': [0, 1], 'lifestyle_id': -1},
                        {'selected_ids': [0, 1, 1], 'lifestyle_id': 1},
                        {'selected_ids': [0, 1, 8], 'product_ids': [0, 1], 'lifestyle_id': 8},
                        {'selected_ids': [0, 1, 2], 'product_ids': [0, 1], 'lifestyle_id': True},
                        {'selected_ids': [0, 1, 2], 'product_ids': [0, 2], 'lifestyle_id': 2}):
            self.assertEqual(images.album_selection(verdict, 4), [])

    def test_product_selection_requires_two_distinct_fragrantica_photos(self):
        images = publisher.image_provider()
        self.assertEqual(images.product_selection({'product_ids': [1, 3]}, 4), [1, 3])
        for ids in ([], [0], [0, 0], [0, 9], [True, 2]):
            self.assertEqual(images.product_selection({'product_ids': ids}, 4), [])

if __name__ == '__main__': unittest.main()
