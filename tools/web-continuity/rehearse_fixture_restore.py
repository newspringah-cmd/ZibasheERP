"""Synthetic fixture backup/restore only, on the labelled local development SQL container."""
import argparse,json,subprocess,time,uuid
DOCKER=['docker','--host=unix:///var/run/docker.sock']
CONTAINER='zibashe-onboarding-sql'
def validate(info):
 if info['Name']!='/'+CONTAINER or info['Config'].get('Labels',{}).get('zibashe.onboarding')!='development':raise ValueError('Development container required')
 ports=info['HostConfig']['PortBindings'].get('1433/tcp',[])
 if ports!=[{'HostIp':'127.0.0.1','HostPort':'14333'}]:raise ValueError('Local development port required')
def main():
 p=argparse.ArgumentParser();p.add_argument('--output',required=True);p.add_argument('--start-development-container',action='store_true');args=p.parse_args()
 inspect=json.loads(subprocess.check_output(DOCKER+['inspect',CONTAINER],text=True))[0];validate(inspect)
 if not inspect['State']['Running']:
  if not args.start_development_container:raise ValueError('Development SQL must be running')
  subprocess.run(DOCKER+['start',CONTAINER],check=True,capture_output=True)
 def sql(query):
  cmd=DOCKER+['exec',CONTAINER,'bash','-c','export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W -Q "$1"','fixture',query]
  r=subprocess.run(cmd,capture_output=True,text=True,timeout=90)
  if r.returncode:raise RuntimeError('Synthetic SQL operation failed; no private diagnostics emitted')
  return r.stdout
 deadline=time.monotonic()+60
 while True:
  try:sql('SELECT 1');break
  except RuntimeError:
   if time.monotonic()>deadline:raise
   time.sleep(2)
 suffix=uuid.uuid4().hex;source='ZibasheContinuityFixture_'+suffix;restored='ZibasheContinuityRestored_'+suffix
 backup='/var/opt/mssql/backup/continuity-fixture-'+suffix+'.bak';created=[];start=time.monotonic()
 try:
  # No caller-provided identifiers, backup files, WITH REPLACE, existing-database deletion or production target.
  sql(f"IF DB_ID(N'{source}') IS NOT NULL OR DB_ID(N'{restored}') IS NOT NULL THROW 51000,'Name collision',1; CREATE DATABASE [{source}];")
  created.append(source)
  sql(f"USE [{source}]; CREATE TABLE dbo.Probe(Id int PRIMARY KEY, Amount decimal(18,2) NOT NULL); INSERT dbo.Probe VALUES(1,564000),(2,150000),(3,0); BACKUP DATABASE [{source}] TO DISK=N'{backup}' WITH CHECKSUM; RESTORE VERIFYONLY FROM DISK=N'{backup}' WITH CHECKSUM;")
  created.append(restored)
  sql(f"RESTORE DATABASE [{restored}] FROM DISK=N'{backup}' WITH MOVE N'{source}' TO N'/var/opt/mssql/data/{restored}.mdf',MOVE N'{source}_log' TO N'/var/opt/mssql/data/{restored}_log.ldf',CHECKSUM,RECOVERY;")
  result=sql(f"SET NOCOUNT ON; DBCC CHECKDB(N'{restored}') WITH NO_INFOMSGS,ALL_ERRORMSGS; SELECT COUNT(*),SUM(Amount) FROM [{restored}].dbo.Probe;")
  if not any(line.split()==['3','714000.00'] for line in result.splitlines()):raise RuntimeError('Restored synthetic contents do not match')
  elapsed=round(time.monotonic()-start,3)
 finally:
  for name in reversed(created):
   assert name in [source,restored] and name.endswith(suffix)
   sql(f"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END;")
  subprocess.run(DOCKER+['exec',CONTAINER,'rm','-f','--',backup],check=True,capture_output=True)
 report={'synthetic_fixture_only':True,'checksum_verifyonly':True,'actual_restore':True,'dbcc_checkdb':True,'expected_rows':3,'expected_sum':'714000.00','duration_seconds':elapsed,'production_backup_tested':False,'real_customer_data_used':False,'temporary_databases_removed':True}
 from pathlib import Path
 Path(args.output).write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report))
if __name__=='__main__':main()
