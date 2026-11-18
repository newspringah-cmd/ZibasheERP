set -euo pipefail
python3 - <<'PY'
import subprocess,json,re
result={}
for unit in ['zibashe-b2-full.service','zibashe-b2-log.service']:
 p=subprocess.run(['sudo','-n','journalctl','-u',unit,'-n','160','--no-pager','-o','cat'],capture_output=True,text=True)
 text=(p.stdout+'\n'+p.stderr).lower()
 eof=any(term in text for term in ['unexpected_eof','eof occurred','unexpected eof','ssleoferror'])
 patterns={'quota_or_cap':r'cap_exceeded|storage.cap|storage.limit|daily.cap|quota|limit exceeded','authentication':r'bad_auth_token|expired_auth_token|unauthorized|invalid.application.key','disk_full':r'no space left|disk is full','network':r'connection timed out|timed out|name resolution|network is unreachable|connection refused','tls':r'certificate verify failed|sslerror|\[ssl:','sql_failure':r'sqlcmd: error|backup database is terminating|operating system error','permission':r'permission denied|access is denied'}
 codes=[c for c in ['storage_cap_exceeded','cap_exceeded','bad_auth_token','expired_auth_token','unauthorized','service_unavailable','account_trouble'] if c in text]
 result[unit]={'journal_accessible':p.returncode==0,'matched_categories':[k for k,v in patterns.items() if re.search(v,text)],'allowlisted_error_codes':codes,'http_statuses':sorted(set(re.findall(r'http(?:error| error| status)?[: ]+(4\d\d|5\d\d)',text))),'unexpected_tls_eof':eof,'exception_types':[e for e in ['KeyError','ValueError','PermissionError','FileNotFoundError','CalledProcessError','HTTPError','URLError','RuntimeError','TimeoutError','JSONDecodeError','ModuleNotFoundError'] if e.lower() in text],'visible_nonempty_lines':len([line for line in p.stdout.splitlines() if line.strip() and 'no entries' not in line.lower()]),'raw_logs_exported':False}
print(json.dumps(result))
PY
