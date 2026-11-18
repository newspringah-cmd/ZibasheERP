import threading
import unittest
import urllib.error
import urllib.request
from serve import Handler, ThreadingHTTPServer


class ExportBoundaryTests(unittest.TestCase):
    def test_export_serves_locally_with_hashed_hydration_and_no_api_or_writes(self):
        server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True)
        thread.start()
        base='http://127.0.0.1:'+str(server.server_port)
        opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
        try:
            with opener.open(base+'/') as response:
                self.assertIn('اطلاعات ساختگی',response.read().decode())
                csp=response.headers['Content-Security-Policy']
                self.assertIn("connect-src 'none'",csp)
                self.assertIn("'sha256-",csp)
                self.assertNotIn('unsafe-inline',csp)
                self.assertEqual(response.headers['Cache-Control'],'no-store')
            with opener.open(base+'/login/') as response:
                self.assertIn('شماره موبایل',response.read().decode())
                self.assertIn("connect-src 'none'",response.headers['Content-Security-Policy'])
            with opener.open(urllib.request.Request(base+'/',method='HEAD')) as response:
                self.assertEqual(response.read(),b'')
            for path in ['/api/me/invoices','/scripts/serve.py','/package.json','/.env','/%2e%2e/package.json']:
                with self.assertRaises(urllib.error.HTTPError) as error:opener.open(base+path)
                self.assertEqual(error.exception.code,404)
            for method in ['POST','PUT','PATCH','DELETE']:
                with self.assertRaises(urllib.error.HTTPError) as error:
                    opener.open(urllib.request.Request(base+'/',data=b'synthetic',method=method))
                self.assertEqual(error.exception.code,405)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()
