"""Read tracked source only. Never reads environment files, credentials or production data."""
import argparse,csv,hashlib,json,re,subprocess
from pathlib import Path

def collect(root):
    root=Path(root)
    tracked=subprocess.check_output(['git','ls-files'],cwd=root,text=True).splitlines()
    controllers=[]
    for name in tracked:
        if name.startswith('ZibasheERP.API/Controllers/') and name.endswith('.cs'):
            source=(root/name).read_text(encoding='utf-8-sig')
            controllers.append({'file':name,'sha256':hashlib.sha256((root/name).read_bytes()).hexdigest(),
                'routes':re.findall(r'\[Route\("([^"\n]+)"\)\]',source),
                'roles':re.findall(r'Authorize\(Roles\s*=\s*"([^"\n]+)"',source),
                'http_action_attributes':len(re.findall(r'\[Http(?:Get|Post|Put|Patch|Delete)',source)),
                'telegram_partial':name.startswith('ZibasheERP.API/Controllers/TelegramWebhookController.')})
    with (root/'docs/web-continuity/capability-register.csv').open(encoding='utf-8-sig',newline='') as f:features=list(csv.DictReader(f))
    program=(root/'ZibasheERP.API/Program.cs').read_text()
    return {'schema_version':1,'scope':'tracked source declarations; not a runtime route/authorization audit',
        'source_commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
        'capabilities':len(features),'feature_ids':[r['شناسه'] for r in features],
        'controllers':controllers,'hosted_service_declarations':sorted(set(re.findall(r'AddHostedService<([^>]+)>',program))),
        'runtime_verified':False,'financial_writes':False}

def main():
    p=argparse.ArgumentParser();p.add_argument('--root',default=str(Path(__file__).resolve().parents[2]));p.add_argument('--output',required=True);args=p.parse_args()
    target=Path(args.output);target.parent.mkdir(parents=True,exist_ok=True);target.write_text(json.dumps(collect(args.root),ensure_ascii=False,indent=2)+'\n')
if __name__=='__main__':main()
