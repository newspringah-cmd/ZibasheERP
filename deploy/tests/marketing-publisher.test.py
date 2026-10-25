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
