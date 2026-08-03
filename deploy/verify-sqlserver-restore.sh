#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "${BASH_SOURCE[0]%/*}" && pwd)"
env_file="$script_dir/.env.production"
compose_file="$script_dir/docker-compose.production.yml"
source_backup="${1:-}"
verification_database='ZibasheERPRestoreVerification'
container_id=''
container_backup="/var/opt/mssql/backup/restore-verification-$$.bak"

fail() { printf 'SQL Server restore verification failed: %s\n' "$1" >&2; exit 1; }
cleanup() {
  if [[ -n "$container_id" ]]; then
    docker exec "$container_id" bash -lc \
      "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"\$MSSQL_SA_PASSWORD\" -C -b -Q \"IF DB_ID(N'$verification_database') IS NOT NULL BEGIN ALTER DATABASE [$verification_database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$verification_database]; END\"" \
      >/dev/null 2>&1 || true
    docker exec "$container_id" rm -f -- "$container_backup" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

[[ -n "$source_backup" ]] || fail 'pass the absolute path of a .bak file created by backup-sqlserver.sh.'
[[ "$source_backup" == /* && "$source_backup" != '/' ]] || fail 'backup path must be absolute.'
[[ -f "$source_backup" && -s "$source_backup" ]] || fail 'backup file does not exist or is empty.'
[[ "$source_backup" == *.bak ]] || fail 'backup file must use the .bak extension.'
[[ -f "$env_file" ]] || fail 'deploy/.env.production does not exist.'

container_id="$(docker compose --env-file "$env_file" -f "$compose_file" ps -q sqlserver)"
[[ -n "$container_id" ]] || fail 'SQL Server container is not running.'
docker cp "$source_backup" "$container_id:$container_backup" >/dev/null
docker exec --user root "$container_id" chown 10001:0 "$container_backup"
docker exec --user root "$container_id" chmod 600 "$container_backup"

docker exec "$container_id" bash -lc \
  "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"\$MSSQL_SA_PASSWORD\" -C -b -r1 -Q \"
IF DB_ID(N'$verification_database') IS NOT NULL
BEGIN
  ALTER DATABASE [$verification_database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  DROP DATABASE [$verification_database];
END;
RESTORE DATABASE [$verification_database]
  FROM DISK = N'$container_backup'
  WITH MOVE N'ZibasheERPDb' TO N'/var/opt/mssql/data/${verification_database}.mdf',
       MOVE N'ZibasheERPDb_log' TO N'/var/opt/mssql/log/${verification_database}_log.ldf',
       RECOVERY, CHECKSUM;
DBCC CHECKDB(N'$verification_database') WITH NO_INFOMSGS, ALL_ERRORMSGS;
ALTER DATABASE [$verification_database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$verification_database];
\""

printf 'PASS  Backup restored into an isolated database and DBCC CHECKDB found no errors.\n'
