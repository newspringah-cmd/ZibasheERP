"""Loopback-only static export server. No Next runtime, API, DB, proxy or writes."""
import argparse
import base64
import hashlib
from html.parser import HTMLParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1] / 'out'
MIME = {'.html':'text/html; charset=utf-8','.css':'text/css','.js':'text/javascript',
        '.svg':'image/svg+xml','.txt':'text/plain','.ico':'image/x-icon','.png':'image/png'}


class InlineHashes(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=False)
        self.tag = None
        self.parts = []
        self.hashes = {'script':[], 'style':[]}

    def handle_starttag(self, tag, attrs):
        if tag in self.hashes and not dict(attrs).get('src'):
            self.tag, self.parts = tag, []

    def handle_data(self, data):
        if self.tag:
            self.parts.append(data)

    def handle_endtag(self, tag):
        if self.tag == tag:
            digest = base64.b64encode(hashlib.sha256(''.join(self.parts).encode()).digest()).decode()
            self.hashes[tag].append(f"'sha256-{digest}'")
            self.tag = None


def policy(body):
    parser = InlineHashes()
    parser.feed(body.decode('utf-8'))
    scripts = ' '.join(parser.hashes['script'])
    styles = ' '.join(parser.hashes['style'])
    return (f"default-src 'none'; script-src 'self' {scripts}; style-src 'self' {styles}; "
            "img-src 'self'; connect-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        self.read(False)

    def do_HEAD(self):
        self.read(True)

    def read(self, head):
        path = unquote(urlsplit(self.path).path)
        if path=='/':path='/index.html'
        target = ROOT / path.lstrip('/')
        if (not target.resolve().is_relative_to(ROOT.resolve()) or not target.is_file()
                or target.is_symlink() or target.suffix not in MIME
                or any(part.startswith('.') or part=='..' for part in Path(path).parts)):
            self.send_error(404)
            return
        body = target.read_bytes()
        self.send_response(200)
        self.send_header('Content-Type', MIME[target.suffix])
        self.send_header('Content-Length',str(len(body)))
        self.send_header('Cache-Control','no-store')
        self.send_header('X-Content-Type-Options','nosniff')
        self.send_header('Referrer-Policy','no-referrer')
        self.send_header('X-Frame-Options','DENY')
        self.send_header('Content-Security-Policy',policy(body) if target.suffix=='.html' else "default-src 'none'")
        self.end_headers()
        if not head:self.wfile.write(body)

    def no_write(self):
        self.send_response(405)
        self.send_header('Allow','GET, HEAD')
        self.send_header('Content-Length','0')
        self.end_headers()

    do_POST=no_write
    do_PUT=no_write
    do_PATCH=no_write
    do_DELETE=no_write


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--port',type=int,default=4181)
    args=parser.parse_args()
    if not (ROOT/'index.html').is_file():parser.error('Run npm run build first')
    server=ThreadingHTTPServer(('127.0.0.1',args.port),Handler)
    print(f'Isolated Next export: http://127.0.0.1:{server.server_port}',flush=True)
    server.serve_forever()
