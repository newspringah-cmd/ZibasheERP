import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
def load(name,path):
 spec=importlib.util.spec_from_file_location(name,ROOT/path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
worker=load('inventory_dispatch','admin-promotions.py')
flash=load('inventory_flash','flash-sale-promotion.py')

class InventoryPublicationTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name)
  self.cfg={'Telegram__BotToken':'fixture','Telegram__ReadyInventoryChannelId':'@zibasheperfume_mojodi','Telegram__NewPaymentsChatId':'-1009876'}
  self.payload={'SalesListId':'11111111-1111-1111-1111-111111111111','Code':1234,'Destination':'inventory-publish','AdminChatId':1,'AdminUserId':1,'HoldHours':24}
  self.info={'id':self.payload['SalesListId'],'code':1234,'chatId':'@zibasheperfume_mojodi','text':'عطر موجودی','photo':'fixture-photo','callback':'slv:fixture:3','messageId':None,'photoMessageId':None}
  self.calls=[];self.binds=[];self.fail_bind=False;self.fail_send=None
  class Marketing:
   def post(_,url,data):
    if url.endswith('/getChat'):
     return {'ok':True,'result':{'id':-1009876 if data['chat_id']=='-1009876' else -1001234,'username':'zibasheperfume_mojodi','type':'channel'}}
    self.calls.append((url.rsplit('/',1)[-1],data))
    if self.fail_send:raise self.fail_send
    return {'ok':True,'result':{'message_id':100+len(self.calls),'chat':{'id':data['chat_id']}}}
  self.marketing=Marketing()
  def api(cfg,route,value=None):
   if value is None:return dict(self.info)
   if self.fail_bind:raise RuntimeError('bind interrupted')
   self.binds.append(value)
  self.p=patch.object(worker,'inventory_api',api);self.p.start();self.addCleanup(self.p.stop)
 def test_new_inventory_publishes_photo_and_purchase_button_to_exact_channel(self):
  self.assertEqual(worker.inventory_job(self.root,self.payload,self.marketing,self.cfg),{'State':'sent'})
  self.assertEqual(len(self.calls),1);method,data=self.calls[0]
  self.assertEqual(method,'sendPhoto');self.assertEqual(data['chat_id'],-1001234)
  self.assertEqual(data['reply_markup']['inline_keyboard'][0][0]['callback_data'],'slv:fixture:3')
  self.assertEqual(self.binds[0]['messageId'],101)
 def test_unknown_binding_resumes_from_photo_receipt_without_duplicate(self):
  self.fail_bind=True
  with self.assertRaises(RuntimeError):worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  self.fail_bind=False;worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  self.assertEqual(len(self.calls),1);self.assertEqual(len(self.binds),1)
 def test_unknown_public_send_is_never_blindly_repeated(self):
  self.fail_send=TimeoutError()
  with self.assertRaises(worker.UnknownDelivery):worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  self.fail_send=None
  with self.assertRaises(worker.UnknownDelivery):worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  self.assertEqual(len(self.calls),1)
 def test_wrong_channel_or_package_is_rejected_before_send(self):
  self.info['chatId']='@wrong'
  with self.assertRaises(worker.Unavailable):worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  self.assertEqual(self.calls,[])
 def test_payment_report_has_photo_and_text_and_binds_both_receipts(self):
  self.payload.update(Destination='inventory-report',InvoiceBatchId='22222222-2222-2222-2222-222222222222');self.info.update(chatId='-1009876',callback=None)
  worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  self.assertEqual([m for m,_ in self.calls],['sendPhoto','sendMessage'])
  self.assertEqual(self.binds[-1],{'messageId':102,'photoMessageId':101,'batchId':self.payload['InvoiceBatchId']})
 def test_bound_inventory_post_is_not_sent_again(self):
  self.info['messageId']=99;worker.inventory_job(self.root,self.payload,self.marketing,self.cfg)
  self.assertEqual(self.calls,[])
 def test_payload_requires_valid_batch_for_inventory_report(self):
  worker.validate_payload(self.payload)
  self.payload['Destination']='inventory-report'
  with self.assertRaises(worker.Unavailable):worker.validate_payload(self.payload)
 def test_inventory_promotion_source_cannot_point_to_another_channel(self):
  offer={'isInventoryOffer':True,'listChatId':'-1001234','listUrl':'https://t.me/c/1234/99'}
  self.assertTrue(flash.valid_source(offer));offer['listUrl']='https://t.me/c/5678/99';self.assertFalse(flash.valid_source(offer))
  offer.update(listChatId='@zibasheperfume_mojodi',listUrl='https://t.me/zibasheperfume_mojodi/22');self.assertTrue(flash.valid_source(offer))

if __name__=='__main__':unittest.main()
