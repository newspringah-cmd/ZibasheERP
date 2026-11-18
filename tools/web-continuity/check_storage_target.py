"""Small non-customer S3 acceptance probe. Dry-run by default; no backup upload or account purchase."""
import argparse,hashlib,json,os,uuid,ipaddress
from urllib.parse import urlsplit
from pathlib import Path
from backup_upload_guard import omit_expect_header

def validate_endpoint(endpoint):
    u=urlsplit(endpoint)
    if u.scheme!='https' or not u.hostname or u.username or u.password or u.query or u.fragment or u.path not in ('','/'):
        raise ValueError('HTTPS storage endpoint without credentials, query or path required')
    if u.hostname.lower() in ['localhost','metadata.google.internal'] or u.hostname.lower().endswith(('.localhost','.local','.internal')):raise ValueError('Public provider endpoint required')
    try:
        if not ipaddress.ip_address(u.hostname).is_global:raise ValueError('Private endpoint refused')
    except ValueError as e:
        if str(e)=='Private endpoint refused':raise
    return u.hostname

def run(client,bucket,data):
    key='zibashe-continuity-probes/'+uuid.uuid4().hex;digest=hashlib.sha256(data).hexdigest()
    attempted=False
    report={'schema_version':1,'customer_data_used':False,'backup_keys_modified':False,'bytes':len(data),'tls_verify':True,'cleanup':'not_needed'}
    try:
        try:client.head_object(Bucket=bucket,Key=key)
        except Exception as e:
            if getattr(e,'response',{}).get('Error',{}).get('Code') not in ['404','NoSuchKey','NotFound']:raise
        else:raise RuntimeError('Probe collision; no write performed')
        attempted=True
        client.put_object(Bucket=bucket,Key=key,Body=data,ContentType='application/octet-stream',Metadata={'sha256':digest})
        report['cleanup']='pending'
        head=client.head_object(Bucket=bucket,Key=key)
        body=client.get_object(Bucket=bucket,Key=key)['Body']
        try:received=body.read(len(data)+1)
        finally:body.close()
        if head.get('ContentLength')!=len(data) or head.get('Metadata',{}).get('sha256')!=digest or hashlib.sha256(received).hexdigest()!=digest:raise RuntimeError('Round-trip verification failed')
        report.update(upload=True,head_verified=True,download_verified=True)
    except Exception as e:
        # Provider error strings may contain signed URLs; expose only the class.
        report.update(upload_verified=False,error_type=type(e).__name__)
        # An attempted PUT can have an uncertain outcome. Reconcile only this unique probe.
    finally:
        try:
            if not attempted:return report
            head=client.head_object(Bucket=bucket,Key=key)
            if head.get('ContentLength')==len(data) and head.get('Metadata',{}).get('sha256')==digest:
                args={'Bucket':bucket,'Key':key}
                if head.get('VersionId'):args['VersionId']=head['VersionId']
                client.delete_object(**args);report['cleanup']='completed'
            elif report['cleanup']=='pending':report['cleanup']='manual_review'
        except Exception as e:
            report['cleanup']='not_present' if getattr(e,'response',{}).get('Error',{}).get('Code') in ['404','NoSuchKey','NotFound'] else 'unknown'
    return report

def main():
    p=argparse.ArgumentParser();p.add_argument('--execute',action='store_true');p.add_argument('--output',required=True);args=p.parse_args()
    if not args.execute:
        result={'dry_run':True,'network_requests':0,'customer_data_used':False,'requires':['provider account','private test bucket','scoped key','endpoint approval','budget approval'],'payload_bytes':4096}
    else:
        prefix='ZIBASHE_BACKUP_S3_';values={k:os.environ.get(prefix+k,'') for k in ['ENDPOINT','REGION','BUCKET','ACCESS_KEY_ID','SECRET_ACCESS_KEY']}
        if not all(values.values()):raise ValueError('Storage settings missing; enter secrets only through protected environment settings')
        validate_endpoint(values['ENDPOINT'])
        import boto3
        from botocore.config import Config
        client=boto3.client('s3',endpoint_url=values['ENDPOINT'],region_name=values['REGION'],aws_access_key_id=values['ACCESS_KEY_ID'],aws_secret_access_key=values['SECRET_ACCESS_KEY'],config=Config(signature_version='s3v4',connect_timeout=10,read_timeout=20,retries={'max_attempts':0}),verify=True)
        client.meta.events.register('before-sign.s3.PutObject',omit_expect_header)
        result=run(client,values['BUCKET'],os.urandom(4096))
        # A normal key working proves compatibility only, not provider location, public access policy or outage resilience.
        result.update(provider_location_verified=False,anonymous_access_verified=False,real_restore_verified=False)
    Path(args.output).write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
if __name__=='__main__':main()
