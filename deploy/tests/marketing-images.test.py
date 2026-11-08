import base64
import hashlib
import importlib.util
import json
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('images',Path(__file__).resolve().parents[1]/'marketing-images.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

def response(value):
    return {'status':'completed','output':[{'type':'message','content':[{'type':'output_text','text':json.dumps(value)}]}]}

class CheckpointTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)
        self.row={'Id':'fixture','EnglishName':'Vanilla Haze','PersianName':'وانیلا هیز','DisplayBrand':'Fugazzi','Gender':3}
        self.cfg={'AddressLabel__OpenAiApiKey':'synthetic-test-only'}
        self.page_calls=0;self.generation_calls=0;self.fail_generation=True;self.verify_products=True;self.single_product=False
    def fetch(self,url,maximum):
        if url.endswith('.html'):
            return b'<img src="https://www.fragrantica.com/a.jpg"><img src="https://www.fragrantica.com/b.jpg">',url
        return b'\xff\xd8\xff'+(b'A' if url.endswith('a.jpg') else b'B')*16000,url
    def post(self,url,payload,headers=None,**kwargs):
        if url.endswith('/images/generations'):
            self.generation_calls+=1
            if self.fail_generation:raise TimeoutError('synthetic image-generation interruption')
            return {'data':[{'b64_json':base64.b64encode(b'\x89PNG\r\n\x1a\nsynthetic-vibe').decode()}]}
        name=payload['text']['format']['name']
        if name=='fragrantica_product_page':
            self.page_calls+=1
            return response({'product_page':'https://www.fragrantica.com/perfume/Fugazzi/Vanilla-Haze.html'})
        if name=='fragrantica_photo_check':
            assert payload['text']['format']['schema']['properties']['product_ids']['items']['enum']==[0,1]
            assert payload['text']['format']['schema']['properties']['lifestyle_id']['enum']==[-1,0,1]
            return response({'selected_ids':[],'product_ids':([0] if self.single_product else [0,1]) if self.verify_products else [],'lifestyle_id':-1,'reason':'synthetic verification'})
        return response({'adult_human_main_subject':True,'adult_woman_visible':True,
            'bottle_or_brand_logo_visible':False,'overlay_text_or_watermark_visible':False})
    def prepare(self):
        with patch.object(m,'fetch',self.fetch):return m.prepare(self.cfg,self.row,self.root,self.post)
    def test_failed_wearer_step_returns_two_verified_products_and_can_resume_from_checkpoint(self):
        paths,status=self.prepare();self.assertEqual(len(paths),2)
        self.assertEqual(status,'two_verified_images_vibe_unavailable')
        key=m.perfume_cache_key(self.row)
        self.assertTrue((self.root/('verified-products-'+key+'.json')).exists())
        cache=self.root/('perfume-images-'+key+'.json')
        self.assertEqual(json.loads(cache.read_text())['version'],9)
        self.prepare();self.assertEqual(self.generation_calls,1)
        cache.unlink()  # An explicit later rebuild may restore the third image.
        self.fail_generation=False
        paths,source=self.prepare()
        self.assertEqual(len(paths),3);self.assertEqual(self.page_calls,1)
        self.assertEqual(source,'verified_from_product_checkpoint')
        self.assertIsNotNone(m.cached_images(self.root,self.root/('perfume-images-'+key+'.json'),self.row,key,{8}))
    def test_tampered_product_checkpoint_is_not_reused(self):
        self.prepare()
        key=m.perfume_cache_key(self.row);saved=json.loads((self.root/('verified-products-'+key+'.json')).read_text())
        (self.root/saved['files'][0]).write_bytes(b'altered product')
        self.fail_generation=False
        paths,_=self.prepare()
        self.assertEqual(len(paths),3);self.assertEqual(self.page_calls,2)
    def test_unverified_products_never_create_a_checkpoint(self):
        self.verify_products=False
        paths,_=self.prepare()
        self.assertEqual(paths,[]);self.assertEqual(self.generation_calls,0)
        self.assertEqual(list(self.root.glob('verified-products-*')),[])

    def test_one_bad_cached_image_keeps_two_healthy_images(self):
        self.fail_generation=False;paths,_=self.prepare()
        paths[2].write_bytes(b'corrupted image')
        available,_=self.prepare()
        self.assertEqual(available,paths[:2]);self.assertEqual(self.generation_calls,1)
        paths[0].write_bytes(b'another corrupted image')
        key=m.perfume_cache_key(self.row)
        self.assertIsNone(m.cached_images(self.root,self.root/('perfume-images-'+key+'.json'),self.row,key,{8,9}))

    def test_one_verified_product_and_verified_wearer_make_two_photos(self):
        self.single_product=True;self.fail_generation=False
        paths,_=self.prepare();self.assertEqual(len(paths),2)
        key=m.perfume_cache_key(self.row)
        self.assertEqual(len(m.cached_images(self.root,self.root/('perfume-images-'+key+'.json'),self.row,key,{8,9})[0]),2)

    def test_one_product_and_failed_wearer_never_publish_a_single_photo(self):
        self.single_product=True
        with self.assertRaises(TimeoutError):self.prepare()
        self.assertEqual(list(self.root.glob('perfume-images-*')),[])

    def test_upload_omits_one_missing_file_before_request(self):
        self.fail_generation=False;paths,_=self.prepare();paths[2].unlink()
        receipt={'ok':True,'result':[{'message_id':i,'chat':{'id':-1001}} for i in (10,11)]}
        with patch.object(m.urllib.request,'urlopen',return_value=io.BytesIO(json.dumps(receipt).encode())) as opened:
            ids=m.send_album({'Telegram__BotToken':'synthetic-only'},'-1001',paths)
        self.assertEqual(ids,[10,11]);self.assertEqual(opened.call_count,1)
        self.assertNotIn(b'attach://photo2',opened.call_args.args[0].data)

    def test_unknown_upload_is_not_retried_with_fewer_photos(self):
        self.fail_generation=False;paths,_=self.prepare()
        with patch.object(m.urllib.request,'urlopen',side_effect=TimeoutError()) as opened:
            with self.assertRaisesRegex(RuntimeError,'unknown'):m.send_album({'Telegram__BotToken':'synthetic-only'},'-1001',paths)
        self.assertEqual(opened.call_count,1)

if __name__=='__main__':unittest.main()
