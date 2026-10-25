import importlib.util
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


if __name__ == '__main__': unittest.main()
