set -euo pipefail
python3 - <<'PY'
import pathlib,json,subprocess,datetime,os
now=datetime.datetime.now(datetime.timezone.utc);home=pathlib.Path.home()
roots=[home/'backups',home/'.local/state/zibashe-backups',pathlib.Path('/opt/zibashe/backups'),pathlib.Path('/var/backups/zibashe')]
# Discover directory literals in the installed backup program, never print its contents or secrets.
program=home/'.local/bin/zibashe-b2-backup.py'
if program.exists():
 import re
 text=program.read_text()
 for literal in re.findall(r'''["'](/[^"'\n]+)["']''',text):
  if 'backup' in literal and '{' not in literal and '$' not in literal and len(literal)<180:
   p=pathlib.Path(literal)
   if p.is_dir():roots.append(p)
found=[];locations=[]
for root in dict.fromkeys(roots):
 try:
  if not root.exists():continue
  candidates=[p for p in root.rglob('*') if p.is_file() and p.name.endswith(('.bak','.bak.gz','.bak.enc','.bak.gz.enc'))]
  for p in candidates:
   s=p.stat();found.append({'bytes':s.st_size,'modified_utc':datetime.datetime.fromtimestamp(s.st_mtime,datetime.timezone.utc).isoformat(),'age_hours':round((now.timestamp()-s.st_mtime)/3600,2),'mode':oct(s.st_mode&0o777),'suffix':''.join(p.suffixes[-3:])})
  locations.append({'location_id':len(locations)+1,'accessible':True,'backup_files':len(candidates)})
 except (OSError,PermissionError):locations.append({'location_id':len(locations)+1,'accessible':False})
timers=subprocess.run(['systemctl','--user','list-timers','--all','--no-pager','--no-legend'],capture_output=True,text=True)
active=[]
for line in timers.stdout.splitlines():
 if 'zibashe' in line and ('backup' in line or 'b2' in line):active.append(line.split()[-2:])
usage=os.statvfs('/');print(json.dumps({'at_utc':now.isoformat(),'scope':'local host metadata only; no file-content verification or foreign object-store calls','backup_files':sorted(found,key=lambda r:r['age_hours']),'locations':locations,'backup_timer_units':active,'root_free_bytes':usage.f_bavail*usage.f_frsize,'restore_proven':False,'writes_performed':False}))
PY
