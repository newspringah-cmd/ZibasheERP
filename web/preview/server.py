"""Local-only, read-only synthetic UI. Not a production web server or an ERP adapter."""
import argparse,json,os
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit
ROOT=Path(__file__).resolve().parent
ALLOWED={'/':'index.html','/index.html':'index.html','/styles.css':'styles.css','/app.js':'app.js','/account.js':'account.js','/preview-config.json':'preview-config.json','/wallpaper.svg':'wallpaper.svg'}
FLAGS=['databaseEnabled','customerLoginEnabled','orderWritesEnabled','financialWritesEnabled','telegramEnabled','smsEnabled','externalRequestsEnabled']
def validate_environment(env,config):
    if any(env.get(k,'').lower()=='production' for k in ['ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','APP_ENV']):raise ValueError('Production environment refused')
    if any(v and (k.startswith('ConnectionStrings__') or k in ['DATABASE_URL','DB_CONNECTION_STRING','SQLCONNSTR_DefaultConnection']) for k,v in env.items()):raise ValueError('Database connection settings refused')
    if config.get('mode')!='isolated-preview' or config.get('syntheticDataOnly') is not True or any(config.get(k) is not False for k in FLAGS):raise ValueError('Only synthetic preview with every integration disabled is allowed')
class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def respond(self,head=False):
        path=urlsplit(self.path).path
        if path=='/health':body=b'{"mode":"isolated-preview","database":false,"synthetic":true}';mime='application/json'
        elif path in ALLOWED:
            file=ROOT/ALLOWED[path]
            if file.resolve().parent!=ROOT:self.send_error(403);return
            body=file.read_bytes();mime={'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.js':'text/javascript; charset=utf-8','.json':'application/json','.svg':'image/svg+xml'}[file.suffix]
        else:self.send_error(404);return
        self.send_response(200);self.send_header('Content-Type',mime);self.send_header('Content-Length',str(len(body)));self.send_header('Cache-Control','no-store');self.send_header('X-Content-Type-Options','nosniff');self.send_header('Referrer-Policy','no-referrer');self.send_header('X-Frame-Options','DENY');self.send_header('Content-Security-Policy',"default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");self.end_headers()
        if not head:self.wfile.write(body)
    def do_GET(self):self.respond()
    def do_HEAD(self):self.respond(True)
    def no_write(self):self.send_response(405);self.send_header('Allow','GET, HEAD');self.send_header('Content-Length','0');self.end_headers()
    do_POST=no_write;do_PUT=no_write;do_PATCH=no_write;do_DELETE=no_write
if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--port',type=int,default=4180);args=parser.parse_args()
    validate_environment(os.environ,json.loads((ROOT/'preview-config.json').read_text()))
    server=ThreadingHTTPServer(('127.0.0.1',args.port),Handler)
    print(f'Isolated synthetic preview: http://127.0.0.1:{server.server_port}',flush=True)
    server.serve_forever()
