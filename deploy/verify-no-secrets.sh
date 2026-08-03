#!/usr/bin/env bash
set -euo pipefail

script_path="${BASH_SOURCE[0]}"
script_dir="${script_path%/*}"
[[ "$script_dir" != "$script_path" ]] || script_dir='.'
repo_root="$(cd -- "$script_dir/.." && pwd)"
cd "$repo_root"

fail() {
  printf 'Secret safety check failed: %s\n' "$1" >&2
  exit 1
}

command -v git >/dev/null 2>&1 || fail 'git is required.'

forbidden_files="$({
  git ls-files --cached --others --exclude-standard -z |
    while IFS= read -r -d '' path; do
      case "$path" in
        *.session|*.session-journal|*.pfx|*.p12|*.pem|*.key|id_rsa|id_ed25519|*/.env|*/.env.*)
          case "$path" in
            *.example) ;;
            *) printf '%s\n' "$path" ;;
          esac
          ;;
      esac
    done
} || true)"
[[ -z "$forbidden_files" ]] || {
  printf '%s\n' "$forbidden_files" >&2
  fail 'a private credential or environment file is tracked.'
}

tracked_files=()
while IFS= read -r -d '' path; do
  case "$path" in
    *.example|deploy/verify-no-secrets.sh) ;;
    *) tracked_files+=("$path") ;;
  esac
done < <(git ls-files --cached --others --exclude-standard -z)

if (( ${#tracked_files[@]} > 0 )); then
  if git grep --no-index -I -n -E -e \
    '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|[0-9]{7,12}:[A-Za-z0-9_-]{30,}' \
    -- "${tracked_files[@]}"; then
    fail 'a private key or Telegram bot-token pattern was found in tracked content.'
  fi
fi

printf 'Secret safety check passed. No forbidden credential files or high-confidence token patterns are tracked.\n'
