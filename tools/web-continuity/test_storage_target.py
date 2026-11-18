import io,unittest,hashlib
from check_storage_target import run,validate_endpoint
class Missing(Exception):response={'Error':{'Code':'404'}}
class Fake:
 def __init__(self,bad=False):self.data=None;self.bad=bad;self.deleted=[]
 def head_object(self,**k):
  if self.data is None:raise Missing()
  return {'ContentLength':len(self.data),'Metadata':{'sha256':hashlib.sha256(self.data).hexdigest()},'VersionId':'test-version'}
 def put_object(self,**k):self.data=k['Body']
 def get_object(self,**k):return {'Body':io.BytesIO(b'corrupt' if self.bad else self.data)}
 def delete_object(self,**k):self.deleted.append(k)
class StorageTargetTests(unittest.TestCase):
 def test_endpoint_blocks_credentials_and_private_hosts(self):
  for u in ['http://example.test','https://localhost','https://127.0.0.1','https://10.0.0.1','https://user:secret@example.test','https://example.test/?token=secret','https://example.test/path']:
   with self.assertRaises(ValueError):validate_endpoint(u)
 def test_valid_endpoint(self):self.assertEqual(validate_endpoint('https://storage.example.test/'),'storage.example.test')
 def test_roundtrip_and_version_scoped_cleanup(self):
  c=Fake();r=run(c,'fixture',b'only-test');self.assertTrue(r['download_verified']);self.assertEqual(c.deleted[0]['VersionId'],'test-version');self.assertTrue(c.deleted[0]['Key'].startswith('zibashe-continuity-probes/'));self.assertFalse(r['backup_keys_modified'])
 def test_corruption_is_not_success(self):
  r=run(Fake(True),'fixture',b'only-test');self.assertFalse(r['upload_verified']);self.assertNotIn('download_verified',r)
 def test_preexisting_probe_never_deleted(self):
  c=Fake();c.data=b'only-test';r=run(c,'fixture',b'only-test');self.assertFalse(r['upload_verified']);self.assertEqual(c.deleted,[])
if __name__=='__main__':unittest.main()
