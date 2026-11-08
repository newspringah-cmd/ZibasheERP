import importlib.util
import json
from pathlib import Path
import sqlite3
import unittest

spec=importlib.util.spec_from_file_location('education',Path(__file__).resolve().parents[1]/'educational-publisher.py')
e=importlib.util.module_from_spec(spec);spec.loader.exec_module(e)

def row(i,volume=9):
    return {'Id':str(i),'PublicCode':i,'EnglishName':'Fragrance '+str(i),'PersianName':'عطر '+str(i),
            'Accords':'مشک، وانیل، چوب','TopNotes':'ترنج','TotalVolume':100,'ReservedVolume':100-volume,
            'Status':1,'TelegramPhotoFileId':'photo'+str(i),'TelegramChannelId':'-1001236168661','TelegramMessageId':100+i}

class Adapter:
    CHAT='-1001283018740'
    def __init__(self):self.rows=[row(1),row(2,13),row(3,14),row(4,50)];self.calls=[];self.fail=None
    def eligible(self,r,maximum):return r['Status']==1 and r['TotalVolume']>r['ReservedVolume'] and r['PublicCode']!=7888 and bool(r['TelegramPhotoFileId'])
    def perfume_frequency_key(self,r):return r['EnglishName']
    def config(self,name):return {}
    def query(self,cfg,list_id=None,maximum_remaining=None):return [r for r in self.rows if list_id is None or r['Id']==list_id]
    def post(self,url,payload,timeout):
        method=url.split('/')[-1];self.calls.append((method,payload))
        if self.fail==method:raise RuntimeError('Outcome uncertain')
        if method=='sendMessage':return {'ok':True,'result':{'chat':{'id':int(self.CHAT)},'message_id':1000}}
        return {'ok':True,'result':[{'chat':{'id':int(self.CHAT)},'message_id':1001+i} for i in range(3)]}

class Tests(unittest.TestCase):
    def setUp(self):
        self.db=sqlite3.connect(':memory:');self.addCleanup(self.db.close)
        self.db.execute('CREATE TABLE slots(slot INTEGER PRIMARY KEY,status TEXT,list_id TEXT,at REAL,message_id INTEGER,error TEXT,perfume_key TEXT)')
        self.a=Adapter();self.cfg={'Telegram__BotToken':'fake'}
    def test_two_hour_windows_are_offset_from_direct_ads(self):
        self.assertEqual(e.slot_for(3600),e.slot_for(10799));self.assertNotEqual(e.slot_for(10799),e.slot_for(10800))
    def test_low_remaining_preferred_but_large_lists_available_if_needed(self):
        values=e.select_rows(self.a.rows,e.TOPICS[0],{}, {'Fragrance 1':99},self.a)
        self.assertEqual({r['PublicCode'] for r in values},{1,2,3})
    def test_excluded_closed_missing_photo_and_daily_cap_removed(self):
        bad=row(7888);closed=row(5);closed['Status']=2;no_photo=row(6);no_photo['TelegramPhotoFileId']=''
        selected=e.select_rows(self.a.rows+[bad,closed,no_photo],e.TOPICS[0],{'Fragrance 1':2},{},self.a)
        self.assertEqual({r['PublicCode'] for r in selected},{2,3,4})
    def test_same_fragrance_different_cycles_not_duplicated(self):
        alias=row(7);alias['EnglishName']='Fragrance 1'
        values=e.select_rows(self.a.rows+[alias],e.TOPICS[0],{}, {},self.a)
        self.assertEqual(len({r['EnglishName'] for r in values}),3)
    def test_specific_topics_match_actual_notes(self):
        values=e.select_rows(self.a.rows,('leather','title','body',['چرم']),{}, {},self.a)
        self.assertEqual(values,[])
    def test_caption_html_limits_and_photo_links(self):
        r=row(1);r['PersianName']='<خاص> & عطر'
        self.assertIn('&lt;خاص&gt; &amp;',e.guide(e.TOPICS[0],[r,row(2),row(3)]))
        media=e.media([r,row(2),row(3)]);self.assertEqual(len(media),3)
        self.assertIn('/101',media[0]['caption'])
    def test_publish_one_guide_with_three_real_product_photos(self):
        result=e.run(self.a,self.db,self.cfg,10000)
        self.assertEqual(result['status'],'sent');self.assertEqual([c[0] for c in self.a.calls],['sendMessage','sendMediaGroup'])
        album=self.a.calls[1][1];self.assertEqual(len(album['media']),3);self.assertEqual(album['reply_parameters']['message_id'],1000)
        self.assertEqual(self.db.execute("SELECT COUNT(*) FROM slots WHERE status='sent'").fetchone()[0],3)
        self.assertEqual(e.run(self.a,self.db,self.cfg,10001)['status'],'slot_already_handled');self.assertEqual(len(self.a.calls),2)
    def test_next_window_changes_topic(self):
        first=e.run(self.a,self.db,self.cfg,10000);second=e.run(self.a,self.db,self.cfg,17200)
        self.assertEqual(second['status'],'sent');self.assertNotEqual(first['topic'],second['topic'])
    def test_uncertain_send_is_not_blindly_repeated(self):
        self.a.fail='sendMessage'
        with self.assertRaises(RuntimeError):e.run(self.a,self.db,self.cfg,10000)
        self.assertEqual(e.run(self.a,self.db,self.cfg,10001)['status'],'slot_already_handled');self.assertEqual(len(self.a.calls),1)
    def test_uncertain_album_does_not_duplicate_text_or_photos(self):
        self.a.fail='sendMediaGroup'
        with self.assertRaises(RuntimeError):e.run(self.a,self.db,self.cfg,10000)
        self.assertEqual(e.run(self.a,self.db,self.cfg,10001)['status'],'slot_already_handled');self.assertEqual(len(self.a.calls),2)
    def test_wait_for_three_without_sending_incomplete_recommendations(self):
        self.a.rows=self.a.rows[:2]
        self.assertEqual(e.run(self.a,self.db,self.cfg,10000)['status'],'waiting_for_three_available_fragrances');self.assertEqual(self.a.calls,[])
    def test_preview_sends_nothing(self):
        self.assertEqual(e.run(self.a,self.db,self.cfg,10000,preview=True)['status'],'preview_no_messages_sent');self.assertEqual(self.a.calls,[])

if __name__=='__main__':unittest.main()
