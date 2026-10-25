import importlib.util
import json
from pathlib import Path
import unittest

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

    def test_official_image_origins_and_duplicates(self):
        images = publisher.image_provider()
        self.assertTrue(images.official_page('https://us.diptyqueparis.com/products/do-son', ['diptyqueparis.com']))
        for url in ('https://diptyqueparis.com.evil.com/image', 'http://diptyqueparis.com/image', 'https://retailer.com/image'):
            self.assertFalse(images.official_page(url, ['diptyqueparis.com']))
        page = '<img src="https://cdn.example.com/a.jpg?width=500"><img src="https://cdn.example.com/a.jpg?width=900"><img src="//cdn.example.com/b.png">'
        self.assertEqual(len(images.image_urls(page, 'https://brand.com/product')), 2)
        for url in ('http://example.com/a.jpg', 'https://user:password@example.com/a.jpg', 'https://example.com:8443/a.jpg', 'https://127.0.0.1/a.jpg'):
            with self.assertRaises(ValueError): images.public_url(url)
        with self.assertRaises(ValueError): images.send_album({}, '-1001', [])

    def test_album_requires_official_lifestyle_and_orders_it_last(self):
        images = publisher.image_provider()
        self.assertEqual(images.album_selection({'selected_ids': [3, 0, 1], 'lifestyle_id': 3}, 4), [0, 1, 3])
        for verdict in ({'selected_ids': [0, 1, 2]}, {'selected_ids': [0, 1, 2], 'lifestyle_id': -1},
                        {'selected_ids': [0, 1, 1], 'lifestyle_id': 1},
                        {'selected_ids': [0, 1, 8], 'lifestyle_id': 8},
                        {'selected_ids': [0, 1, 2], 'lifestyle_id': True}):
            self.assertEqual(images.album_selection(verdict, 4), [])

    def test_ai_vibe_only_after_two_verified_product_photos(self):
        images = publisher.image_provider()
        self.assertEqual(images.product_selection({'product_ids': [1, 3]}, 4), [1, 3])
        for ids in ([], [0], [0, 0], [0, 9], [True, 2]):
            self.assertEqual(images.product_selection({'product_ids': ids}, 4), [])
        import tempfile
        import base64
        with tempfile.TemporaryDirectory() as folder:
            captured = []
            def fake_post(url, payload, headers, timeout):
                captured.append((url, payload))
                if url.endswith('/images/generations'):
                    return {'data': [{'b64_json': base64.b64encode(b'\x89PNG\r\n\x1a\n' + b'dummy').decode()}]}
                verdict = {'adult_human_main_subject': True, 'adult_woman_visible': True,
                           'bottle_or_brand_logo_visible': False, 'overlay_text_or_watermark_visible': False}
                return {'status': 'completed', 'output': [{'type': 'message', 'content': [
                    {'type': 'output_text', 'text': json.dumps(verdict)}]}]}
            name, digest, model = images.generate_vibe({'AddressLabel__OpenAiApiKey': 'test'},
                {'EnglishName': 'Test Perfume', 'TopNotes': 'rose'}, Path(folder), fake_post)
            self.assertTrue(name.startswith('ai-vibe-'))
            self.assertTrue((Path(folder)/name).is_file())
            self.assertEqual(captured[0][1]['n'], 1)
            self.assertIn('No perfume bottle', captured[0][1]['prompt'])
            self.assertEqual(len(captured), 2)
            self.assertTrue(captured[1][0].endswith('/responses'))
            self.assertIn('clearly visible fictional adult human', captured[0][1]['prompt'])


if __name__ == '__main__': unittest.main()
