#!/usr/bin/env bash
# Shared settings and helpers for the spike scripts. Source it, do not run it.
set -euo pipefail

SPIKE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE="${ENGINE:-sqlbranch-spike}"
ENGINE_IMAGE="${ENGINE_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
PORT="${PORT:-14330}"
POOL_MNT="${POOL_MNT:-/mnt/sqlbranch-pool}"   # path inside the Colima VM, or on the Linux host
# "colima" on a Mac; "native" on a Linux host where Docker runs directly (the x86-64 G2 run).
HOST_MODE="${HOST_MODE:-$([[ "$(uname -s)" == Linux ]] && echo native || echo colima)}"
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
PW_FILE="$SPIKE_DIR/.sa-password"

die()  { echo "ERROR: $*" >&2; exit 1; }
note() { echo "==> $*" >&2; }

need() { command -v "$1" >/dev/null 2>&1 || die "'$1' not found. $2"; }

# Milliseconds since the epoch. macOS date has no %N, perl ships with macOS.
now_ms() { perl -MTime::HiRes=time -e 'printf "%d\n", time*1000'; }

valid_name() {
  [[ "$1" =~ ^[a-z0-9][a-z0-9_-]{0,62}$ ]] || die "invalid name '$1' (lowercase letters, digits, - and _)"
}

# The sa password is generated once and kept in spike/.sa-password (git-ignored).
sa_password() {
  if [[ ! -f "$PW_FILE" ]]; then
    ( umask 077; echo "Sb1-$(openssl rand -hex 12)" > "$PW_FILE" )
  fi
  cat "$PW_FILE"
}

# Run a script file as root on the Docker host: inside the Colima VM, or locally on Linux.
vm_script() {
  local script="$1"; shift
  if [[ "$HOST_MODE" == native ]]; then
    sudo bash -s -- "$@" < "$script"
  else
    colima ssh -- sudo bash -s -- "$@" < "$script"
  fi
}

# Run one shell command as root on the Docker host.
host_root() {
  if [[ "$HOST_MODE" == native ]]; then sudo bash -c "$1"; else colima ssh -- sudo bash -c "$1"; fi
}

# Run a script file inside the engine container (as the mssql user).
engine_script() {
  local script="$1"; shift
  docker exec -i "$ENGINE" bash -s -- "$@" < "$script"
}

# Run a shell command inside the engine container.
in_engine() { docker exec -i "$ENGINE" bash -c "$1"; }

# Run T-SQL. Set DB=name to choose a database. Fails on SQL errors.
sql() {
  docker exec -i -e SQLCMDPASSWORD="$(sa_password)" "$ENGINE" \
    "$SQLCMD" -C -b -S localhost -U sa -d "${DB:-master}" -Q "$1"
}

# Run T-SQL and print one bare value.
sql_scalar() {
  docker exec -i -e SQLCMDPASSWORD="$(sa_password)" "$ENGINE" \
    "$SQLCMD" -C -b -h -1 -W -S localhost -U sa -d "${DB:-master}" \
    -Q "SET NOCOUNT ON; $1" | tr -d '\r' | sed 's/[[:space:]]*$//' | head -1
}

# Bytes used in the pool.
pool_used() { in_engine "df --output=used -B1 /pool | tail -1 | tr -d ' '"; }

wait_for_engine() {
  local _
  for _ in $(seq 1 90); do
    if sql "SELECT 1" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  docker logs --tail 30 "$ENGINE" >&2 || true
  die "engine did not become ready in 180 seconds (see the log lines above)"
}

# Build the ON (...) list for CREATE DATABASE ... FOR ATTACH from a folder in the engine.
attach_list() {
  local dir="$1" out="" f
  while IFS= read -r f; do
    [[ -n "$f" ]] || continue
    out+="${out:+, }(FILENAME = N'$dir/$f')"
  done < <(in_engine "cd '$dir' && ls -1")
  [[ -n "$out" ]] || die "no files found in $dir"
  echo "$out"
}

branch_exists() { [[ "$(sql_scalar "SELECT COUNT(*) FROM sys.databases WHERE name = N'$1'")" == "1" ]]; }

drop_branch() {
  local b="$1"
  if branch_exists "$b"; then
    sql "ALTER DATABASE [$b] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$b];" >/dev/null
  fi
  in_engine "rm -rf '/pool/branches/$b'"
}
