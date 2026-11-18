set -euo pipefail
python3 - <<'PY'
import json,subprocess
units=['zibashe-b2-full.service','zibashe-b2-log.service','zibashe-b2-full.timer','zibashe-b2-log.timer'];report={}
for unit in units:
 r=subprocess.run(['systemctl','show',unit,'--property=LoadState,ActiveState,Result,ExecMainStatus,ExecMainExitTimestamp,LastTriggerUSec,NextElapseUSecRealtime'],capture_output=True,text=True)
 report[unit]=dict(line.split('=',1) for line in r.stdout.splitlines() if '=' in line)
print(json.dumps({'scope':'system unit status only; success does not prove restore or foreign object existence','units':report}))
PY
