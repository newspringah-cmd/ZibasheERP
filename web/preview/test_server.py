import unittest,json,threading,urllib.request,urllib.error
from server import validate_environment,Handler,ROOT,ThreadingHTTPServer
class PreviewSafetyTests(unittest.TestCase):
 def config(self):return json.loads((ROOT/'preview-config.json').read_text())
 def test_every_feature_is_off(self):validate_environment({},self.config())
 def test_refuses_production_and_database_settings(self):
  for env in [{'ASPNETCORE_ENVIRONMENT':'Production'},{'DOTNET_ENVIRONMENT':'production'},{'ConnectionStrings__DefaultConnection':'fake-connection'},{'DATABASE_URL':'fake-db'}]:
   with self.assertRaises(ValueError):validate_environment(env,self.config())
 def test_refuses_enabled_or_missing_flag(self):
  for mutate in ['enable','missing','real']:
   c=self.config()
   if mutate=='enable':c['financialWritesEnabled']=True
   elif mutate=='missing':del c['databaseEnabled']
   else:c['syntheticDataOnly']=False
   with self.assertRaises(ValueError):validate_environment({},c)
 def test_readonly_routes_and_security_headers(self):
  s=ThreadingHTTPServer(('127.0.0.1',0),Handler);t=threading.Thread(target=s.serve_forever,daemon=True);t.start();base='http://127.0.0.1:'+str(s.server_port)
  opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
  try:
   with opener.open(base+'/') as r:self.assertIn("connect-src 'none'",r.headers['Content-Security-Policy']);self.assertIn('اطلاعات ساختگی',r.read().decode())
   for path in ['/api/invoices','/../../.env.production','/server.py']:
    with self.assertRaises(urllib.error.HTTPError) as e:opener.open(base+path)
    self.assertEqual(e.exception.code,404)
   for method in ['POST','PUT','PATCH','DELETE']:
    with self.assertRaises(urllib.error.HTTPError) as e:opener.open(urllib.request.Request(base+'/',data=b'fixture',method=method))
    self.assertEqual(e.exception.code,405)
  finally:s.shutdown();s.server_close();t.join()
if __name__=='__main__':unittest.main()
