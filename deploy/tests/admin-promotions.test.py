"""Synthetic end-to-end publisher tests: fake Telegram, fake catalogue, local journals."""
import copy
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import time
import types
import unittest
from unittest.mock import patch
import uuid

DEPLOY = Path(__file__).resolve().parents[1]

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, DEPLOY / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result

worker = load('admin_promotions', 'admin-promotions.py')
publisher = load('admin_channel_promotion_test', 'channel-remaining-promotion.py')

def fragrance(number=1, remaining=14):
    return {'Id': str(uuid.UUID(int=number)), 'PerfumeId': 'perfume-' + str(number), 'PublicCode': 9000 + number,
        'StablePublicCode': None, 'EnglishName': 'Fixture', 'PersianName': 'عطر آزمایشی', 'DisplayBrand': 'Fixture',
        'TotalVolume': 100, 'ReservedVolume': 100-remaining, 'Status': 1, 'IsDeleted': False, 'IsInventoryOffer': False,
        'TelegramChannelId': publisher.CHANNEL, 'TelegramMessageId': 7 + number, 'TelegramPhotoFileId': 'fixture-photo'}

class ChannelTelegram:
    def __init__(self):
        self.sent = []; self.deleted = []
    def verify_destination(self): pass
    def send(self, row, caption):
        self.sent.append(row['Id'])
        return {'message_id': 1000+len(self.sent), 'chat': {'id': int(publisher.CHANNEL)}, 'date': time.time()}
    def edit(self, *args): pass
    def delete(self, message_id): self.deleted.append(message_id)

class Marketing:
    CHAT = '-1001283018740'
    def __init__(self, root):
        self.STATE = root
        self.rows = [fragrance()]
        self.albums = []; self.messages = []; self.text_failure = False; self.album_failure = False
        self.prepare_failure = False; self.excluded_list = False
        self.photos = [root / (str(i) + '.jpg') for i in range(3)]
        for photo in self.photos: photo.write_bytes(b'synthetic-photo')
        self.images = types.SimpleNamespace(prepare=self.prepare, perfume_cache_key=lambda row:'fixture',
            cached_images=lambda *args:(self.photos, {}), send_album=self.send_album)
        with sqlite3.connect(root / 'history.sqlite') as db:
            db.executescript('CREATE TABLE slots(slot INTEGER PRIMARY KEY,status TEXT,list_id TEXT,at REAL,message_id INTEGER,error TEXT,perfume_key TEXT); CREATE TABLE cache(list_id TEXT PRIMARY KEY,at REAL,content TEXT);')
            db.execute('INSERT INTO cache VALUES(?,?,?)', (self.rows[0]['Id'], time.time(), json.dumps({'analysis':'synthetic', 'positive_reviews':'synthetic', 'reviews_found':True, 'sources':['https://example.invalid']})))
    def config(self, container): return {}
    def query(self, env, test_code): return copy.deepcopy([r for r in self.rows if r['PublicCode']==test_code and r['Status']==1 and r['TotalVolume']>r['ReservedVolume']])
    def eligible(self, row, **kwargs): return publisher.eligible(row, maximum=None)
    def excluded(self, row): return self.excluded_list
    def perfume_frequency_key(self, row): return 'fixture'
    def validate_content(self, value): pass
    def image_provider(self): return self.images
    def prepare(self, *args):
        if self.prepare_failure: raise TimeoutError()
        return self.photos, 'cached_verified'
    def caption(self, row, content): return 'A synthetic readable promotion'
    def send_album(self, cfg, chat, paths):
        self.albums.append(list(paths))
        if self.album_failure: raise TimeoutError()
        return [101+i for i in range(len(paths))]
    def post(self, url, payload, **kwargs):
        if url.endswith('getChat'): return {'ok':True,'result':{'id':int(self.CHAT),'type':'supergroup'}}
        self.messages.append(payload)
        if self.text_failure: raise TimeoutError()
        return {'ok':True,'result':{'chat':{'id':int(self.CHAT)},'message_id':104}}

class PromotionTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(); self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.state = self.root/'worker'; self.state.mkdir()
        marketing_dir = self.root/'marketing'; marketing_dir.mkdir()
        self.marketing = Marketing(marketing_dir)
        self.cfg = {'Telegram__BotToken':'synthetic-test-only', 'Telegram__SilentGroupChatIds':self.marketing.CHAT,'Telegram__OwnerUserId':'1'}
        self.payload = {'SalesListId':self.marketing.rows[0]['Id'],'Code':9001,'Destination':'group','AdminChatId':1,'AdminUserId':1,'HoldHours':24}
        self.job = {'id':str(uuid.uuid4()),'payload':json.dumps(self.payload),'attempts':0}
        self.addCleanup(patch.stopall)
        patch.object(worker,'STATE',self.state).start()
        patch.object(publisher,'STATE_DIR',self.root/'channel').start()
        self.channel_telegram = ChannelTelegram()
        self.channel_rows = [fragrance(1), fragrance(2,7)]
        catalogue = types.SimpleNamespace(rows=lambda active_id=None:copy.deepcopy(self.channel_rows))
        patch.object(publisher,'Catalogue',lambda:catalogue).start()
        patch.object(publisher,'Telegram',lambda token:self.channel_telegram).start()
        patch.object(worker,'module',lambda *args:publisher).start()
    def run_job(self): return worker.process(self.job,self.marketing,self.cfg)
    def job_root(self): return self.state/self.job['id']
    def choose_channel(self):
        self.payload['Destination']='channel';self.job['payload']=json.dumps(self.payload)

    def choose_removal(self):
        self.payload.update(Destination='channel-delete', Code=0, SalesListId=str(uuid.UUID(int=0)))
        self.job['payload'] = json.dumps(self.payload)

    def test_removal_needs_no_stock_lookup_and_does_not_publish_anything(self):
        self.choose_removal(); self.marketing.rows = []
        self.assertEqual(self.run_job(), {'State':'sent'})
        self.assertTrue(publisher.Store(publisher.STATE_DIR).data['paused'])
        self.assertEqual(self.run_job(), {'State':'sent'})
        self.assertEqual(self.channel_telegram.sent, [])
        self.assertEqual(self.marketing.messages, [])

    def test_removal_is_idempotent_after_crash_before_worker_receipt(self):
        self.choose_channel(); self.assertEqual(self.run_job()['State'], 'sent')
        self.job = {'id':str(uuid.uuid4()),'payload':'','attempts':0}; self.choose_removal()
        self.assertEqual(self.run_job(), {'State':'sent'})
        (self.job_root()/'outcome.json').unlink()
        self.assertEqual(self.run_job(), {'State':'sent'})
        self.assertEqual(len(self.channel_telegram.deleted), 1)
        self.assertEqual(len(self.channel_telegram.sent), 1)

    def test_older_queued_publish_cannot_undo_removal_but_new_request_resumes(self):
        import datetime as dt
        def at(seconds): return dt.datetime.fromtimestamp(time.time()+seconds, dt.timezone.utc).isoformat()
        original = copy.deepcopy(self.payload)
        self.choose_removal(); self.job['createdAt'] = at(-10)
        self.assertEqual(self.run_job(), {'State':'sent'})
        self.payload = original; self.payload['Destination'] = 'channel'
        self.job = {'id':str(uuid.uuid4()), 'payload':json.dumps(self.payload), 'createdAt':at(-100)}
        self.assertEqual(self.run_job()['State'], 'failed')
        self.assertEqual(self.channel_telegram.sent, [])
        self.job = {'id':str(uuid.uuid4()), 'payload':json.dumps(self.payload), 'createdAt':at(0)}
        self.assertEqual(self.run_job()['State'], 'sent')
        self.assertFalse(publisher.Store(publisher.STATE_DIR).data['paused'])
        self.assertEqual(len(self.channel_telegram.sent), 1)

    def test_removal_of_undeletable_message_reports_archiving_not_deletion(self):
        self.choose_channel(); self.assertEqual(self.run_job()['State'], 'sent')
        def cannot_delete(message_id):
            raise publisher.TelegramFailure(400, "message can't be deleted")
        self.channel_telegram.delete = cannot_delete
        self.job = {'id':str(uuid.uuid4()),'payload':'','attempts':0};self.choose_removal()
        result = self.run_job()
        self.assertEqual(result['State'], 'failed')
        self.assertIn('تلگرام حذف پیام قدیمی را نپذیرفت', result['Error'])
        self.assertTrue(publisher.Store(publisher.STATE_DIR).data['paused'])
        self.assertEqual(len(self.channel_telegram.sent), 1)

    def test_append_job_keeps_prior_ad_and_recovers_its_receipt_from_retained_slot(self):
        self.choose_channel(); first_job = copy.deepcopy(self.job)
        first_result = self.run_job()
        self.marketing.rows.append(fragrance(2, 7))
        self.payload.update(SalesListId=fragrance(2)['Id'], Code=9002, ChannelMode='append')
        self.job = {'id':str(uuid.uuid4()),'payload':json.dumps(self.payload)}
        self.assertEqual(self.run_job()['State'], 'sent')
        self.assertEqual(len(publisher.active_ads(publisher.Store(publisher.STATE_DIR).data)), 2)
        self.assertEqual(self.channel_telegram.deleted, [])
        self.job = first_job
        (self.job_root()/'outcome.json').unlink()
        self.assertEqual(self.run_job(), first_result)
        self.assertEqual(len(self.channel_telegram.sent), 2)
        self.channel_rows[0].update(ReservedVolume=100, Status=2)
        store = publisher.Store(publisher.STATE_DIR)
        publisher.Engine(store, publisher.Catalogue(), self.channel_telegram).tick(time.time()+60)
        (self.job_root()/'outcome.json').unlink()
        self.assertEqual(self.run_job(), first_result)
        self.assertEqual(len(self.channel_telegram.sent), 2)
        self.assertEqual(len(publisher.active_ads(publisher.Store(publisher.STATE_DIR).data)), 1)

    def test_invalid_channel_mode_cannot_publish(self):
        self.choose_channel(); self.payload['ChannelMode'] = 'unrecognized'
        self.job['payload'] = json.dumps(self.payload)
        self.assertEqual(self.run_job()['State'], 'failed')
        self.assertEqual(self.channel_telegram.sent, [])

    def test_group_has_three_photos_and_readable_text_with_button(self):
        result=self.run_job()
        self.assertEqual(result,{'State':'sent','Url':'https://t.me/c/1283018740/104'})
        self.assertEqual(len(self.marketing.albums[0]),3)
        text=self.marketing.messages[0]
        self.assertEqual(text['reply_parameters']['message_id'],101)
        self.assertIn('reply_markup',text)
        self.marketing.rows=[]
        self.assertEqual(self.run_job(),result)
        self.assertEqual(len(self.marketing.albums),1);self.assertEqual(len(self.marketing.messages),1)

    def test_group_sends_two_verified_photos_with_normal_text(self):
        self.marketing.photos=self.marketing.photos[:2]
        self.assertEqual(self.run_job()['State'],'sent')
        self.assertEqual(len(self.marketing.albums[0]),2)
        self.assertEqual(len(self.marketing.messages),1)
        self.assertIn('reply_markup',self.marketing.messages[0])
        self.assertEqual(self.run_job()['State'],'sent');self.assertEqual(len(self.marketing.albums),1)

    def test_group_never_publishes_an_unverified_single_photo_album(self):
        self.marketing.photos=self.marketing.photos[:1]
        self.assertEqual(self.run_job()['State'],'retry')
        self.assertEqual(self.marketing.albums,[]);self.assertEqual(self.marketing.messages,[])

    def test_crash_after_album_receipt_resumes_only_text(self):
        original=worker.stage
        def crash(root,name,action):
            if name=='text': raise SystemExit('synthetic crash')
            return original(root,name,action)
        with patch.object(worker,'stage',crash):
            with self.assertRaises(SystemExit): self.run_job()
        self.assertEqual(len(self.marketing.albums),1)
        self.assertEqual(self.run_job()['State'],'sent')
        self.assertEqual(len(self.marketing.albums),1);self.assertEqual(len(self.marketing.messages),1)

    def test_crash_after_text_receipt_recovers_without_resending(self):
        original=worker.history
        def crash(marketing,plan,status,message_id=None):
            if status=='sent': raise SystemExit('synthetic crash')
            return original(marketing,plan,status,message_id)
        with patch.object(worker,'history',crash):
            with self.assertRaises(SystemExit): self.run_job()
        self.marketing.rows=[]
        self.assertEqual(self.run_job()['State'],'sent')
        self.assertEqual(len(self.marketing.albums),1);self.assertEqual(len(self.marketing.messages),1)

    def test_unknown_album_delivery_never_blindly_retries(self):
        self.marketing.album_failure=True
        self.assertEqual(self.run_job()['State'],'unknown')
        self.marketing.album_failure=False
        self.marketing.photos=self.marketing.photos[:2]
        self.assertEqual(self.run_job()['State'],'unknown')
        self.assertEqual(len(self.marketing.albums),1);self.assertEqual(len(self.marketing.messages),0)

    def test_unknown_text_delivery_does_not_repeat_album_or_text(self):
        self.marketing.text_failure=True
        self.assertEqual(self.run_job()['State'],'unknown')
        self.marketing.text_failure=False
        self.assertEqual(self.run_job()['State'],'unknown')
        self.assertEqual(len(self.marketing.albums),1);self.assertEqual(len(self.marketing.messages),1)

    def test_preparation_failure_is_safe_to_retry(self):
        self.marketing.prepare_failure=True
        self.assertEqual(self.run_job()['State'],'retry')
        self.assertFalse((self.job_root()/'outcome.json').exists())
        self.marketing.prepare_failure=False
        self.assertEqual(self.run_job()['State'],'sent')
        self.assertEqual(len(self.marketing.albums),1)

    def test_closed_or_excluded_list_never_posts(self):
        self.marketing.rows[0]['Status']=2
        self.assertEqual(self.run_job()['State'],'failed')
        self.assertEqual(self.marketing.albums,[])

    def test_group_exclusions_remain_enforced(self):
        self.marketing.excluded_list=True
        self.assertEqual(self.run_job()['State'],'failed')
        self.assertEqual(self.marketing.messages,[])

    def test_active_automatic_publisher_defers_manual_job(self):
        import fcntl
        with (self.marketing.STATE/'run.lock').open('a') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX)
            self.assertEqual(self.run_job()['State'],'retry')
        self.assertEqual(self.run_job()['State'],'sent')
        self.assertEqual(len(self.marketing.albums),1)

    def test_invalid_destination_cannot_publish(self):
        self.payload['Destination']='other-group';self.job['payload']=json.dumps(self.payload)
        self.assertEqual(self.run_job()['State'],'failed')
        self.assertEqual(self.marketing.albums,[])

    def test_channel_request_keeps_24_hour_deadline_and_durable_request_identity(self):
        self.choose_channel()
        result=self.run_job();self.assertEqual(result['State'],'sent')
        store=publisher.Store(publisher.STATE_DIR);ad=store.data['active']
        self.assertEqual(ad['request_id'],self.job['id'])
        self.assertTrue(86390<ad['expires_at']-time.time()<=86400)
        deadline=ad['expires_at'];(self.job_root()/'outcome.json').unlink()
        self.assertEqual(self.run_job(),result)
        self.assertEqual(publisher.Store(publisher.STATE_DIR).data['active']['expires_at'],deadline)
        self.assertEqual(len(self.channel_telegram.sent),1)

    def test_completed_channel_request_recovery_does_not_replace_new_automatic_ad(self):
        self.choose_channel();result=self.run_job()
        store=publisher.Store(publisher.STATE_DIR)
        self.channel_rows[0]['ReservedVolume']=100;self.channel_rows[0]['Status']=2
        engine=publisher.Engine(store,publisher.Catalogue(),self.channel_telegram)
        engine.tick(time.time()+60)
        self.assertEqual(store.data['retired'][-1]['request_id'],self.job['id'])
        (self.job_root()/'outcome.json').unlink();self.marketing.rows=[]
        self.assertEqual(self.run_job(),result)
        self.assertEqual(len(self.channel_telegram.sent),2)
        self.assertEqual(publisher.Store(publisher.STATE_DIR).data['active']['row']['Id'],fragrance(2)['Id'])

    def choose_message(self, text='سلام <b>دوستان</b>\nپیام مدیر 🌸'):
        self.payload.update(Destination='group-message',Code=0,SalesListId=str(uuid.UUID(int=0)),MessageText=text)
        self.job['payload']=json.dumps(self.payload)

    def test_group_message_preserves_plain_text_and_sends_once(self):
        self.choose_message()
        first=self.run_job();second=self.run_job()
        self.assertEqual(first,second);self.assertEqual(first['State'],'sent')
        self.assertEqual(len(self.marketing.messages),1)
        self.assertEqual(self.marketing.messages[0]['text'],self.payload['MessageText'])
        self.assertNotIn('parse_mode',self.marketing.messages[0])
        self.assertEqual(self.marketing.messages[0]['chat_id'],'-1001283018740')
        self.assertEqual(self.marketing.albums,[])

    def test_group_message_unknown_send_is_not_retried(self):
        self.choose_message();self.marketing.text_failure=True
        self.assertEqual(self.run_job()['State'],'unknown')
        self.marketing.text_failure=False
        self.assertEqual(self.run_job()['State'],'unknown')
        self.assertEqual(len(self.marketing.messages),1)

    def test_group_message_receipt_recovers_after_result_interruption(self):
        self.choose_message();first=self.run_job()
        (self.job_root()/'outcome.json').unlink()
        self.assertEqual(self.run_job(),first)
        self.assertEqual(len(self.marketing.messages),1)

    def test_group_message_rejects_long_utf16_and_blank_text(self):
        for text in ['🌸'*2049,'   ']:
            self.choose_message(text)
            self.assertEqual(self.run_job()['State'],'failed')
            (self.job_root()/'outcome.json').unlink()
        self.assertEqual(self.marketing.messages,[])

    def test_group_message_rejects_other_destinations(self):
        self.choose_message();self.marketing.CHAT='-1009999999999'
        self.assertEqual(self.run_job()['State'],'failed')
        self.assertEqual(self.marketing.messages,[])

    def test_accounting_administrator_cannot_send_group_messages(self):
        self.choose_message();self.payload['AdminUserId']=2;self.job['payload']=json.dumps(self.payload)
        result=self.run_job()
        self.assertEqual(result['State'],'failed');self.assertIn('zahraa_frj',result['Error'])
        self.assertEqual(self.marketing.messages,[])

    def test_hourly_start_delegates_approved_parameters_and_publishes_both_destinations_once(self):
        calls=[];campaign=str(uuid.uuid4());directory=self.root/'flash'
        class Transport:
            def api(inner,route,data=None):
                calls.append(('api',route,data))
                return {'code':9001,'salesListId':self.payload['SalesListId']}
        def publish(transport,root,campaign_id,**kwargs):
            calls.append(('publish',kwargs.get('destination',-1001283018740)))
            return {'message_id':200,'chat':{'id':-1001283018740}}
        fake=types.SimpleNamespace(STATE=directory,CHANNEL=-1001236168661,Transport=Transport,publish=publish,Rejected=type('Rejected',(Exception,),{}))
        self.payload.update(Destination='flash-start',FlashSale={'CampaignId':campaign,'DurationMinutes':90,'DiscountPercent':0,'DiscountAmountPerMl':5000,'OriginalPricePerMl':20000})
        self.job['payload']=json.dumps(self.payload)
        with patch.object(worker,'module',return_value=fake):
            self.assertEqual(self.run_job()['State'],'sent');self.assertEqual(self.run_job()['State'],'sent')
        self.assertEqual([value[1] for value in calls if value[0]=='publish'],[-1001283018740,-1001236168661])
        request=calls[0][2]
        self.assertEqual(request['durationMinutes'],90);self.assertEqual(request['discountAmountPerMl'],5000);self.assertEqual(request['actorUserId'],1)
        self.assertTrue((directory/campaign/'automatic-lifecycle.json').exists())

    def test_hourly_end_stops_campaign_and_runs_thanks_without_duplicate_job_replay(self):
        calls=[];campaign=str(uuid.uuid4())
        class Transport:
            def api(inner,route,data=None):calls.append(('end',route,data));return {}
        fake=types.SimpleNamespace(STATE=self.root/'flash',Transport=Transport,UnknownAnnouncement=type('Unknown',(Exception,),{}),
            end_thanks=lambda *args:calls.append(('thanks',args[2])))
        self.payload.update(Destination='flash-end',Code=0,SalesListId=str(uuid.UUID(int=0)),FlashSale={'CampaignId':campaign})
        self.job['payload']=json.dumps(self.payload)
        with patch.object(worker,'module',return_value=fake):
            self.assertEqual(self.run_job(),{'State':'sent'});self.assertEqual(self.run_job(),{'State':'sent'})
        self.assertEqual(len(calls),2);self.assertEqual(calls[0][1],campaign+'/end');self.assertEqual(calls[0][2]['actorUserId'],1)

    def test_group_admin_copy_uses_exact_escaped_text_without_auto_research(self):
        self.payload['CustomText']='متن من <b>همین بماند</b>\nخط بعد 🌸'
        self.job['payload']=json.dumps(self.payload)
        self.marketing.caption=lambda *args: (_ for _ in ()).throw(AssertionError('Automatic copy should not run'))
        self.assertEqual(self.run_job()['State'],'sent')
        text=self.marketing.messages[-1]['text']
        self.assertIn('متن من &lt;b&gt;همین بماند&lt;/b&gt;\nخط بعد 🌸',text)
        self.assertIn('کد لیست: 9001',text);self.assertIn('باقی‌مانده: 14 میل',text)
        self.assertEqual(len(self.marketing.albums[0]),3)

    def test_custom_group_text_retains_two_image_fallback(self):
        self.payload['CustomText']='پیشنهاد امروز';self.job['payload']=json.dumps(self.payload)
        self.marketing.photos=self.marketing.photos[:2]
        self.assertEqual(self.run_job()['State'],'sent');self.assertEqual(len(self.marketing.albums[0]),2)

    def test_channel_admin_copy_persists_in_selected_slot(self):
        self.payload.update(Destination='channel',CustomText='متن کانال من')
        self.job['payload']=json.dumps(self.payload)
        self.assertEqual(self.run_job()['State'],'sent')
        active=publisher.Store(publisher.STATE_DIR).data['active']
        self.assertEqual(active['custom_text'],'متن کانال من');self.assertIn('متن کانال من',active['caption'])

    def test_custom_copy_limits_use_telegram_utf16_units(self):
        for destination,text in [('channel','😀'*351),('group','x'*3501),('channel','')]:
            value=dict(self.payload,Destination=destination,CustomText=text)
            with self.assertRaises(worker.Unavailable):worker.validate_payload(value)

class PublicationLaneTests(unittest.TestCase):
    def test_inventory_dispatch_is_independent_of_blocked_report(self):
        import threading
        import logging
        entered, release, delivered = threading.Event(), threading.Event(), threading.Event()
        regular, inventory = str(uuid.uuid4()), str(uuid.uuid4())
        def api(_cfg, path, value=None):
            if path == 'pending?lane=regular': return [{'id': regular}]
            if path == 'pending?lane=inventory': return [{'id': inventory}]
            return None
        def process(job, *_):
            if job['id'] == regular:
                entered.set()
                if not release.wait(5): raise RuntimeError('test timeout')
            else: delivered.set()
            return {'State': 'sent'}
        with patch.object(worker, 'api', side_effect=api), patch.object(worker, 'process', side_effect=process):
            t=threading.Thread(target=worker.dispatch_once,args=(None,{},logging.getLogger('lane-test'),'regular'))
            t.start()
            try:
                self.assertTrue(entered.wait(2))
                worker.dispatch_once(None,{},logging.getLogger('lane-test'),'inventory')
                self.assertTrue(delivered.is_set())
                self.assertTrue(t.is_alive())
            finally: release.set(); t.join(3)

    def test_regular_failure_does_not_consume_inventory_job(self):
        import logging
        paths=[]
        def api(_cfg,path,value=None):
            paths.append(path)
            if path.endswith('regular'): raise TimeoutError()
            return []
        with patch.object(worker,'api',side_effect=api):
            worker.dispatch_once(None,{},logging.getLogger('lane-test'),'regular')
            worker.dispatch_once(None,{},logging.getLogger('lane-test'),'inventory')
        self.assertEqual(paths,['pending?lane=regular','pending?lane=inventory'])

if __name__=='__main__': unittest.main()
