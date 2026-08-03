#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "${BASH_SOURCE[0]%/*}" && pwd)"
env_file="$script_dir/.env.production"
compose_file="$script_dir/docker-compose.production.yml"
destination="${1:-}"
database='ZibasheERPDb'
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_name="zibashe-${stamp}.bak"
container_backup="/var/opt/mssql/backup/$backup_name"
container_id=''

fail() { printf 'SQL Server backup failed: %s\n' "$1" >&2; exit 1; }
cleanup() {
  [[ -z "$container_id" ]] || docker exec "$container_id" rm -f -- "$container_backup" >/dev/null 2>&1 || true
}
trap cleanup EXIT

[[ -n "$destination" ]] || fail 'pass a protected absolute host backup directory.'
[[ "$destination" == /* && "$destination" != '/' ]] || fail 'backup destination must be an absolute directory other than root.'
[[ -f "$env_file" ]] || fail 'deploy/.env.production does not exist.'
[[ -d "$destination" ]] || fail 'backup destination does not exist.'
permissions="$(stat -c '%a' "$destination" 2>/dev/null || true)"
[[ "$permissions" == '700' ]] || fail "backup destination permissions must be 700 (current: ${permissions:-unknown})."

container_id="$(docker compose --env-file "$env_file" -f "$compose_file" ps -q sqlserver)"
[[ -n "$container_id" ]] || fail 'SQL Server container is not running.'

docker exec "$container_id" bash -lc \
  "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"\$MSSQL_SA_PASSWORD\" -C -b -r1 -Q \"BACKUP DATABASE [$database] TO DISK = N'$container_backup' WITH INIT, CHECKSUM, STATS = 10; RESTORE VERIFYONLY FROM DISK = N'$container_backup' WITH CHECKSUM;\""
docker cp "$container_id:$container_backup" "$destination/$backup_name" >/dev/null
chmod 600 "$destination/$backup_name"
[[ -s "$destination/$backup_name" ]] || fail 'copied backup is empty.'

printf 'PASS  SQL Server backup and RESTORE VERIFYONLY completed.\n'
printf 'Backup: %s\n' "$destination/$backup_name"
printf 'Run verify-sqlserver-restore.sh against this file before relying on it.\n'
