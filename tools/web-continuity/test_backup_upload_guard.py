import unittest,tempfile,hashlib
from pathlib import Path
from backup_upload_guard import verified_upload,omit_expect_header,RemoteConflict,RemoteVerificationFailed
class Missing(Exception):response={'Error':{'Code':'404'}}
class Denied(Exception):response={'Error':{'Code':'AccessDenied'}}
class Client:
 def __init__(self,heads,error=None):self.heads=iter(heads);self.error=error;self.puts=0
 def head_object(self,**kwargs):
  result=next(self.heads)
  if isinstance(result,Exception):raise result
  return result
 def upload_file(self,*args,**kwargs):
  self.puts+=1
  if self.error:raise self.error
class UploadGuardTests(unittest.TestCase):
 def setUp(self):
  self.folder=tempfile.TemporaryDirectory();self.path=Path(self.folder.name)/'fixture.gz';self.path.write_bytes(b'fixture');self.meta={'sha256':hashlib.sha256(b'fixture').hexdigest()};self.good={'ContentLength':7,'Metadata':self.meta}
 def tearDown(self):self.folder.cleanup()
 def run_upload(self,c):return verified_upload(c,'fixture-bucket',self.path,'fixed-operation-key',self.meta)
 def test_existing_match_does_not_upload(self):
  c=Client([self.good]);self.assertEqual(self.run_upload(c),'already_verified');self.assertEqual(c.puts,0)
 def test_conflict_never_overwrites(self):
  c=Client([{'ContentLength':8,'Metadata':self.meta}])
  with self.assertRaises(RemoteConflict):self.run_upload(c)
  self.assertEqual(c.puts,0)
 def test_head_denied_never_treated_as_missing(self):
  c=Client([Denied()])
  with self.assertRaises(Denied):self.run_upload(c)
  self.assertEqual(c.puts,0)
 def test_success_verifies_receipt(self):self.assertEqual(self.run_upload(Client([Missing(),self.good])),'uploaded_and_verified')
 def test_unknown_upload_response_reconciles(self):
  c=Client([Missing(),self.good],ConnectionError());self.assertEqual(self.run_upload(c),'verified_after_uncertain_response');self.assertEqual(c.puts,1)
 def test_unknown_upload_not_repeated(self):
  c=Client([Missing(),Missing()],ConnectionError())
  with self.assertRaises(ConnectionError):self.run_upload(c)
  self.assertEqual(c.puts,1);self.assertTrue(self.path.exists())
 def test_bad_receipt_fails_and_keeps_source(self):
  with self.assertRaises(RemoteVerificationFailed):self.run_upload(Client([Missing(),{'ContentLength':7,'Metadata':{}}]))
  self.assertTrue(self.path.exists())
 def test_omit_only_expect_before_signing(self):
  class Request:headers={'Expect':'100-continue','Content-Length':'7','x-amz-content-sha256':'payload-hash'}
  r=Request();omit_expect_header(r);self.assertNotIn('Expect',r.headers);self.assertEqual(r.headers['x-amz-content-sha256'],'payload-hash')
if __name__=='__main__':unittest.main()
