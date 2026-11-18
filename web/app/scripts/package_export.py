"""Fixed synthetic export only; no source tree, dependencies or environment files."""
import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

ROOT=Path(__file__).resolve().parents[1]

def package(output):
    output.mkdir(parents=True,exist_ok=True)
    files={}
    for file in (ROOT/'out').rglob('*'):
        if file.is_file():
            if file.is_symlink() or not file.resolve().is_relative_to((ROOT/'out').resolve()):
                raise ValueError('Unexpected export link')
            files[str(file.relative_to(ROOT))]=file.read_bytes()
    if 'out/index.html' not in files:raise ValueError('Build the static site first')
    files['scripts/serve.py']=(ROOT/'scripts/serve.py').read_bytes()
    files['README.txt']='Synthetic Zibashe preview. No customer data or ERP connection.\nRun: python3 scripts/serve.py --port 4181\nOpen: http://127.0.0.1:4181\n'.encode()
    manifest={name:hashlib.sha256(data).hexdigest() for name,data in files.items()}
    (output/'export-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    with ZipFile(output/'zibashe-next-preview.zip','w',ZIP_DEFLATED) as bundle:
        for name,data in files.items():bundle.writestr(name,data)
        bundle.writestr('manifest.json',json.dumps(manifest,indent=2)+'\n')
    print(f'Packaged {len(files)} synthetic export files')

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',required=True,type=Path)
    package(parser.parse_args().output)
