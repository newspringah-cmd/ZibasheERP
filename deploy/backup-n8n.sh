#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$script_dir/.env.n8n"
compose_file="$script_dir/docker-compose.n8n.production.yml"
backup_dir="${1:-}"

fail() {
  printf 'n8n backup failed: %s\n' "$1" >&2
  exit 1
}

[[ -n "$backup_dir" ]] || fail 'Pass an existing protected backup directory outside the repository.'
[[ -d "$backup_dir" && -w "$backup_dir" ]] || fail 'The backup directory does not exist or is not writable.'
[[ -f "$env_file" ]] || fail 'deploy/.env.n8n does not exist.'
command -v docker >/dev/null 2>&1 || fail 'Docker is not available.'

umask 077
timestamp="$(date -u +'%Y%m%dT%H%M%SZ')"
database_path="${backup_dir%/}/n8n-${timestamp}.dump"
files_path="${backup_dir%/}/n8n-${timestamp}-files.tgz"
temporary_database="$(mktemp "${backup_dir%/}/.n8n-db-${timestamp}.XXXXXX")"
temporary_files="$(mktemp "${backup_dir%/}/.n8n-files-${timestamp}.XXXXXX")"
trap 'rm -f "$temporary_database" "$temporary_files"' EXIT

docker compose --env-file "$env_file" -f "$compose_file" \
  exec -T n8n-postgres pg_dump --username=n8n --dbname=n8n --format=custom > "$temporary_database"
[[ -s "$temporary_database" ]] || fail 'pg_dump produced an empty backup.'
docker compose --env-file "$env_file" -f "$compose_file" \
  exec -T n8n tar --directory=/home/node --create --gzip --file=- .n8n > "$temporary_files"
[[ -s "$temporary_files" ]] || fail 'n8n data volume backup is empty.'
mv "$temporary_database" "$database_path"
mv "$temporary_files" "$files_path"
trap - EXIT
printf 'n8n database backup created: %s\n' "$database_path"
printf 'n8n file backup created: %s\n' "$files_path"
