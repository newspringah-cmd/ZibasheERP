"""Package only fixed public synthetic preview assets; no environment or ERP access."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parent


def export(output):
    output.mkdir(parents=True, exist_ok=True)
    wallpaper = base64.b64encode((ROOT / 'wallpaper.svg').read_bytes()).decode('ascii')
    css = (ROOT / 'styles.css').read_text().replace(
        "url('wallpaper.svg')", f"url('data:image/svg+xml;base64,{wallpaper}')")
    html = (ROOT / 'index.html').read_text().replace(
        '<link rel="stylesheet" href="styles.css">', f'<style>{css}</style>')
    for name in ['account.js', 'app.js']:
        html = html.replace(f'<script src="{name}" defer></script>', '')
    scripts = '\n'.join((ROOT / name).read_text() for name in ['account.js', 'app.js'])
    html = html.replace('</body>', f'<script>{scripts}</script></body>')
    target = output / 'zibashe-web-preview.html'
    target.write_text(html)
    names = [target.name] + [name for name in [
        'desktop.png', 'mobile.png', 'conversation.png', 'account-desktop.png',
        'account-mobile.png', 'invoice-mobile.png', 'verification.json'
    ] if (output / name).is_file()]
    manifest = {name: hashlib.sha256((output / name).read_bytes()).hexdigest() for name in names}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    with ZipFile(output / 'zibashe-web-preview.zip', 'w', ZIP_DEFLATED) as bundle:
        for name in names + ['manifest.json']:
            bundle.write(output / name, name)
    print(f'Synthetic preview exported: {target}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    export(parser.parse_args().output)
