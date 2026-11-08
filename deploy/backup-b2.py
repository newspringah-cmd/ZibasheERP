#!/usr/bin/python3
import datetime as dt
import fcntl
import hashlib
import gzip
import shutil
import tempfile
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import boto3
from botocore.config import Config
from boto3.s3.transfer import TransferConfig

CONFIG=Path("/etc/zibashe-backup/b2.json")
CONTAINER="deploy-sqlserver-1"
API_CONTAINER="deploy-zibashe-api-1"
CONTAINER_DIR="/var/opt/mssql/backup/zibashe-erp-backups"
HOST_DIR=Path("/var/lib/docker/volumes/deploy_sqlserver_backup/_data/zibashe-erp-backups")
PREFIX="zibashe-erp-backups/"
LOCAL_RETENTION_DAYS=28

def load_client():
 c=json.loads(CONFIG.read_text())
 s=boto3.client("s3",endpoint_url=c["endpoint"],region_name=c["region"],
  aws_access_key_id=c["key_id"],aws_secret_access_key=c["application_key"],
  config=Config(signature_version="s3v4",connect_timeout=20,read_timeout=900,
   retries={"mode":"standard","max_attempts":5}))
 return c,s

def send_telegram(text):
 try:
  raw=subprocess.check_output(["docker","inspect",API_CONTAINER,"--format","{{range .Config.Env}}{{println .}}{{end}}"],text=True)
  env={}
  for line in raw.splitlines():
   if "=" in line:
    k,v=line.split("=",1); env[k]=v
  token=env.get("Telegram__BotToken")
  chat=env.get("Telegram__OwnerUserId")
  if not token or not chat:
   raise RuntimeError("Telegram destination is not configured")
  body=urllib.parse.urlencode({"chat_id":chat,"text":text}).encode()
  req=urllib.request.Request("https://api.telegram.org/bot"+token+"/sendMessage",data=body,
   headers={"Content-Type":"application/x-www-form-urlencoded"})
  with urllib.request.urlopen(req,timeout=20) as response:
   result=json.loads(response.read())
  if not result.get("ok"):
   raise RuntimeError("Telegram sendMessage was rejected")
  print("Telegram backup notification sent.")
 except Exception as exc:
  print(f"Telegram notification failed ({type(exc).__name__}); backup result is unchanged.")

def sha256(path):
 h=hashlib.sha256()
 with path.open("rb") as f:
  for block in iter(lambda:f.read(8*1024*1024),b""): h.update(block)
 return h.hexdigest()

def compress_backup(path):
 target=path.with_suffix(path.suffix+".gz")
 with tempfile.NamedTemporaryFile(dir=path.parent,prefix=".gzip-",delete=False) as out:
  temporary=Path(out.name)
  try:
   with path.open("rb") as src,gzip.GzipFile(filename="",fileobj=out,mode="wb",compresslevel=6,mtime=0) as gz:
    shutil.copyfileobj(src,gz,8*1024*1024)
   out.flush();os.fsync(out.fileno())
  except BaseException:
   temporary.unlink(missing_ok=True);raise
 try:
  digest=hashlib.sha256();size=0
  with gzip.open(temporary,"rb") as src:
   for block in iter(lambda:src.read(8*1024*1024),b""):
    digest.update(block);size+=len(block)
  if size!=path.stat().st_size or digest.hexdigest()!=sha256(path):
   raise RuntimeError("Lossless backup compression verification failed; original retained.")
  temporary.chmod(0o600);os.replace(temporary,target)
 finally:temporary.unlink(missing_ok=True)
 return target

def upload(c,s,path,key,original=None):
 digest=sha256(path)
 metadata={"sha256":digest}
 if original is not None:
  metadata.update({"original-sha256":sha256(original),"original-size":str(original.stat().st_size),"compression":"gzip"})
 cfg=TransferConfig(multipart_threshold=128*1024*1024,multipart_chunksize=128*1024*1024,
  max_concurrency=2,use_threads=True)
 s.upload_file(str(path),c["bucket"],key,ExtraArgs={"Metadata":metadata},Config=cfg)
 obj=s.head_object(Bucket=c["bucket"],Key=key)
 if obj["ContentLength"]!=path.stat().st_size or any(obj.get("Metadata",{}).get(k)!=v for k,v in metadata.items()):
  raise RuntimeError("Remote size/checksum metadata mismatch; local file retained.")
 print(f"Verified upload: {key} ({path.stat().st_size} bytes)")

def upload_existing(c,s):
 seeds=[
  (Path("/var/lib/docker/volumes/deploy_sqlserver_backup/_data/zibashe-full-20261027T090628Z.bak"),PREFIX+"seed/full-20261027T090628Z.bak"),
  (Path("/var/lib/docker/volumes/deploy_sqlserver_backup/_data/zibashe-log-20261027T090628Z.trn"),PREFIX+"seed/log-20261027T090628Z.trn")]
 for p,k in seeds:
  if not p.is_file(): raise FileNotFoundError("A previously verified local backup is missing.")
  upload(c,s,compress_backup(p),k+".gz",original=p)

def sql_backup(kind):
 if kind not in ("full","log"): raise ValueError("Expected full or log.")
 stamp=dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
 suffix="bak" if kind=="full" else "trn"
 filename=f"{kind}-{stamp}.{suffix}"
 inside=f"{CONTAINER_DIR}/{filename}"
 HOST_DIR.mkdir(parents=True,exist_ok=True)
 subprocess.run(["docker","exec","--user","mssql",CONTAINER,"mkdir","-p",CONTAINER_DIR],check=True)
 if kind=="full":
  sql=f":ON ERROR EXIT\nSET NOCOUNT ON;\nBACKUP DATABASE [ZibasheERPDb] TO DISK=N'{inside}' WITH CHECKSUM, STATS=10;\nRESTORE VERIFYONLY FROM DISK=N'{inside}' WITH CHECKSUM;\n"
 else:
  sql=f":ON ERROR EXIT\nSET NOCOUNT ON;\nBACKUP LOG [ZibasheERPDb] TO DISK=N'{inside}' WITH CHECKSUM, STATS=10;\nRESTORE VERIFYONLY FROM DISK=N'{inside}' WITH CHECKSUM;\n"
 command='/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W -i /dev/stdin'
 subprocess.run(["docker","exec","-i",CONTAINER,"/bin/bash","-lc",command],
  input=sql,text=True,check=True,timeout=6*60*60)
 path=HOST_DIR/filename
 deadline=time.time()+60
 while not path.is_file() and time.time()<deadline: time.sleep(1)
 if not path.is_file() or path.stat().st_size==0: raise RuntimeError("SQL backup file not found or empty.")
 return path,PREFIX+("full/" if kind=="full" else "log/")+filename

def cleanup_local(c,s):
 cutoff=time.time()-LOCAL_RETENTION_DAYS*86400
 for p in list(HOST_DIR.glob("full-*.bak"))+list(HOST_DIR.glob("log-*.trn")):
  if p.stat().st_mtime>=cutoff: continue
  key=PREFIX+("full/" if p.name.startswith("full-") else "log/")+p.name
  try:
   digest=sha256(p)
   try:
    obj=s.head_object(Bucket=c["bucket"],Key=key+".gz")
    metadata=obj.get("Metadata",{})
    verified=(metadata.get("original-sha256")==digest and metadata.get("original-size")==str(p.stat().st_size)
     and metadata.get("compression")=="gzip" and bool(metadata.get("sha256")))
   except Exception:
    obj=s.head_object(Bucket=c["bucket"],Key=key)
    verified=obj["ContentLength"]==p.stat().st_size and obj.get("Metadata",{}).get("sha256")==digest
   if verified:
    p.unlink();p.with_suffix(p.suffix+".gz").unlink(missing_ok=True)
    print(f"Removed local copy after verified remote retention: {p.name}")
  except Exception: continue

def main(mode):
 os.makedirs("/run/lock",exist_ok=True)
 with open("/run/lock/zibashe-b2-backup.lock","w") as lock:
  fcntl.flock(lock,fcntl.LOCK_EX)
  c,s=load_client()
  if mode=="upload-existing":
   upload_existing(c,s); return
  try:
   p,k=sql_backup(mode)
   upload(c,s,compress_backup(p),k+".gz",original=p)
   cleanup_local(c,s)
  except Exception as exc:
   now=dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
   send_telegram(f"⚠️ خطا در بکاپ {mode} دیتابیس ZibasheERPDb\nزمان: {now}\nجزئیات فنی در لاگ سرور ثبت شده است.")
   raise
  if mode=="full":
   now=dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
   send_telegram(f"✅ بکاپ کامل روزانهٔ ZibasheERPDb موفق شد.\nزمان: {now}\nحجم: {p.stat().st_size:,} بایت\nمقصد: Backblaze B2 (نگهداری ۳۰روزه)")

if __name__=="__main__":
 if len(sys.argv)!=2:
  print("Usage: zibashe-b2-backup.py full|log|upload-existing",file=sys.stderr);sys.exit(2)
 main(sys.argv[1])
