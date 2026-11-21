#!/usr/bin/env bash
# G4 probe: can a GitHub-hosted runner host a thin pool for SQL Server branches?
# 1 runner facts, 2 XFS reflink pool, 3 SQL Server container, 4 small image built
# from scripts, 5 branches (timing, pool growth, isolation, image unchanged, CHECKDB).
#
# Usage on a fresh Linux runner (needs sudo and docker):   ./probe.sh
# Reuse a running engine that has the pool at /pool:
#   SKIP_SETUP=1 ENGINE=<container> SA_PASSWORD=<pw> ./probe.sh
# Knobs: POOL_GB (40), DATA_MB (1024), BRANCHES (5)
set -euo pipefail
cd "$(dirname "$0")"

ENGINE="${ENGINE:-sqlbranch-probe}"
ENGINE_IMAGE="${ENGINE_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
POOL_MNT="${POOL_MNT:-/mnt/sqlbranch-pool}"
POOL_GB="${POOL_GB:-40}"
DATA_MB="${DATA_MB:-1024}"
BRANCHES="${BRANCHES:-5}"
SKIP_SETUP="${SKIP_SETUP:-0}"
P="g4probe"                         # prefix of every database and folder made here
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"
PASS=0; FAIL=0

if [[ "$SKIP_SETUP" == "1" ]]; then
  PW="${SA_PASSWORD:?set SA_PASSWORD when SKIP_SETUP=1}"
else
  PW="Pb1-$(openssl rand -hex 12)"   # throwaway, lives only for this run
  [[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::add-mask::$PW"
fi

now_ms()    { perl -MTime::HiRes=time -e 'printf "%d\n", time*1000'; }
note()      { echo "==> $*" >&2; }
record()    { echo "$1=$2"; echo "| $1 | $2 |" >> "$SUMMARY"; }
ok()        { echo "PASS  $*"; echo "| PASS | $* |" >> "$SUMMARY"; PASS=$((PASS+1)); }
bad()       { echo "FAIL  $*"; echo "| FAIL | $* |" >> "$SUMMARY"; FAIL=$((FAIL+1)); }
in_engine() { docker exec -i "$ENGINE" bash -c "$1"; }
sql() {
  docker exec -i -e SQLCMDPASSWORD="$PW" "$ENGINE" \
    "$SQLCMD" -C -b -S localhost -U sa -d "${DB:-master}" -Q "$1"
}
scalar() {
  docker exec -i -e SQLCMDPASSWORD="$PW" "$ENGINE" \
    "$SQLCMD" -C -b -h -1 -W -S localhost -U sa -d "${DB:-master}" \
    -Q "SET NOCOUNT ON; $1" | tr -d '\r' | sed 's/[[:space:]]*$//' | head -1
}
pool_used() { in_engine "df --output=used -B1 /pool | tail -1 | tr -d ' '"; }
attach_list() {
  local out="" f
  while IFS= read -r f; do
    [[ -n "$f" ]] && out+="${out:+, }(FILENAME = N'$1/$f')"
  done < <(in_engine "cd '$1' && ls -1")
  echo "$out"
}
detach() { sql "IF DB_ID(N'$1') IS NOT NULL BEGIN ALTER DATABASE [$1] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sp_detach_db N'$1'; END" >/dev/null; }
drop_db() { sql "IF DB_ID(N'$1') IS NOT NULL BEGIN ALTER DATABASE [$1] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$1]; END" >/dev/null; }

echo "| Item | Value |" >> "$SUMMARY"; echo "|---|---|" >> "$SUMMARY"

# 1. Runner facts ---------------------------------------------------------------
if [[ "$SKIP_SETUP" != "1" ]]; then
  note "Runner facts"
  record os "$(. /etc/os-release && echo "$PRETTY_NAME")"
  record kernel "$(uname -r)"
  record cpus "$(nproc)"
  record mem_gib "$(free -g | awk '/^Mem:/{print $2}')"
  record root_free_gib "$(df -BG --output=avail / | tail -1 | tr -dc '0-9')"
  [[ -d /mnt ]] && record mnt_free_gib "$(df -BG --output=avail /mnt | tail -1 | tr -dc '0-9')"
  record docker "$(docker version --format '{{.Server.Version}}')"
fi

# 2. Pool ---------------------------------------------------------------------
if [[ "$SKIP_SETUP" != "1" ]]; then
  note "Creating a ${POOL_GB} GB XFS pool at $POOL_MNT"
  T0=$(now_ms)
  sudo bash ../vm/pool.sh "$POOL_GB" "$POOL_MNT" | tee pool.log
  record pool_setup_ms $(( $(now_ms) - T0 ))
  record pool_file "$(grep -o 'at /[^ ]*pool.img' pool.log | head -1 | cut -c4-)"
  if grep -q "PROBE: thin" pool.log; then ok "reflink works on the runner"; else bad "reflink probe on the runner"; fi
  if ! grep -q "reflink=1" pool.log; then bad "pool was not made with reflink=1"; fi
fi

# 3. Engine -------------------------------------------------------------------
if [[ "$SKIP_SETUP" != "1" ]]; then
  note "Pulling $ENGINE_IMAGE"
  T0=$(now_ms); docker pull -q "$ENGINE_IMAGE" >/dev/null; record image_pull_ms $(( $(now_ms) - T0 ))
  note "Starting SQL Server"
  T0=$(now_ms)
  docker run -d --name "$ENGINE" \
    -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e MSSQL_SA_PASSWORD="$PW" \
    -v "$POOL_MNT:/pool" -v "$POOL_MNT/engine:/var/opt/mssql" \
    "$ENGINE_IMAGE" >/dev/null
  for _ in $(seq 1 90); do sql "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
  sql "SELECT 1" >/dev/null || { docker logs --tail 50 "$ENGINE"; bad "engine did not start"; exit 1; }
  record engine_ready_ms $(( $(now_ms) - T0 ))
  record sql_version "$(scalar "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(30)) + N' ' + CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(30))")"
fi
if in_engine 'set -e; rm -f /pool/.probe/a /pool/.probe/b
    dd if=/dev/zero of=/pool/.probe/a bs=1M count=1 status=none
    cp --reflink=always /pool/.probe/a /pool/.probe/b; rm -f /pool/.probe/a /pool/.probe/b'; then
  ok "reflink works inside the container as the engine's user"
else
  bad "reflink inside the container"
fi

# 4. Image --------------------------------------------------------------------
SRC="${P}_src"; STAGE="/pool/staging/$P"; IMG="/pool/images/$P/data"
note "Building a ${DATA_MB} MB image from scripts"
drop_db "$SRC"; in_engine "rm -rf '$STAGE' '/pool/images/$P' && mkdir -p '$STAGE'"
T0=$(now_ms)
sql "CREATE DATABASE [$SRC]
  ON (NAME = ${P}_data, FILENAME = N'$STAGE/${P}.mdf', SIZE = 64MB, FILEGROWTH = 256MB)
  LOG ON (NAME = ${P}_log, FILENAME = N'$STAGE/${P}_log.ldf', SIZE = 8MB, FILEGROWTH = 64MB);
ALTER DATABASE [$SRC] SET RECOVERY SIMPLE;" >/dev/null
# char(3000) rows: two per 8 KB page, so 256 rows per MB.
DB="$SRC" sql "
CREATE TABLE dbo.items (id int IDENTITY PRIMARY KEY, payload char(3000) NOT NULL);
INSERT dbo.items WITH (TABLOCK) (payload)
SELECT TOP ($DATA_MB * 256) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b;
CHECKPOINT;" >/dev/null
detach "$SRC"
in_engine "rm -f '$STAGE'/*.ldf"                      # fresh minimal log, as in spike REBUILD_LOG=1
sql "CREATE DATABASE [$SRC] ON $(attach_list "$STAGE") FOR ATTACH_REBUILD_LOG;" >/dev/null 2>&1
detach "$SRC"
in_engine "mkdir -p '/pool/images/$P' && mv '$STAGE' '$IMG' && chmod a-w '$IMG'/* && cd '$IMG' && sha256sum * > ../SHA256SUMS"
record image_build_ms $(( $(now_ms) - T0 ))
IMG_BYTES=$(in_engine "du -sb '$IMG' | cut -f1")
record image_mib $(( IMG_BYTES / 1048576 ))

# 5. Branches -----------------------------------------------------------------
branch() {   # name -> prints clone_ms attach_ms
  local b="$1" dst="/pool/branches/$1/data" t0 t1 t2
  drop_db "$b"; in_engine "rm -rf '/pool/branches/$b'"
  t0=$(now_ms)
  in_engine "set -e; mkdir -p '$dst'; for f in '$IMG'/*; do cp --reflink=always \"\$f\" '$dst/'; done; chmod u+w '$dst'/*"
  t1=$(now_ms)
  sql "CREATE DATABASE [$b] ON $(attach_list "$dst") FOR ATTACH;" >/dev/null
  t2=$(now_ms)
  echo "$((t1-t0)) $((t2-t1))"
}

note "Creating $BRANCHES branches"
U0=$(pool_used); MAX_MS=0
for i in $(seq 1 "$BRANCHES"); do
  read -r C A < <(branch "${P}_b$i")
  record "branch_${i}_ms" "clone $C + attach $A = $((C+A))"
  (( C+A > MAX_MS )) && MAX_MS=$((C+A))
done
GROWTH=$(( $(pool_used) - U0 ))
record "growth_${BRANCHES}_branches_mib" $(( GROWTH / 1048576 ))
record growth_percent_of_image "$(perl -e "printf '%.2f', 100*$GROWTH/$IMG_BYTES")"
if (( MAX_MS < 10000 )); then ok "every branch under 10 s (slowest ${MAX_MS} ms)"; else bad "slowest branch ${MAX_MS} ms"; fi
if (( GROWTH * 20 < IMG_BYTES )); then ok "pool growth under 5 percent of the image"; else bad "pool growth $(( GROWTH / 1048576 )) MiB"; fi

note "Writing into branch 1"
T0=$(now_ms)
DB="${P}_b1" sql "UPDATE TOP (10000) dbo.items SET payload = 'changed';
CREATE TABLE dbo.probe_writes (id int IDENTITY PRIMARY KEY, v char(200) NOT NULL);
INSERT dbo.probe_writes (v) SELECT TOP (100000) 'w' FROM sys.all_objects a CROSS JOIN sys.all_objects b;
CHECKPOINT;" >/dev/null
record write_ms $(( $(now_ms) - T0 ))
[[ "$(DB="${P}_b1" scalar "SELECT COUNT(*) FROM dbo.probe_writes")" == "100000" ]] \
  && ok "branch 1 holds its writes" || bad "branch 1 lost its writes"
[[ "$(DB="${P}_b2" scalar "SELECT COUNT(*) FROM dbo.items WHERE payload = 'changed'")" == "0" ]] \
  && ok "branch 2 does not see branch 1's writes" || bad "branch 2 sees branch 1's writes"
in_engine "cd '$IMG' && sha256sum --quiet -c ../SHA256SUMS" \
  && ok "image files unchanged" || bad "image files changed"
sql "DBCC CHECKDB (N'${P}_b1') WITH NO_INFOMSGS;" >/dev/null \
  && ok "CHECKDB clean on the written branch" || bad "CHECKDB errors on branch 1"

# Cleanup when borrowing someone else's engine; a runner is thrown away anyway.
if [[ "$SKIP_SETUP" == "1" ]]; then
  note "Removing everything named $P*"
  for i in $(seq 1 "$BRANCHES"); do drop_db "${P}_b$i"; in_engine "rm -rf '/pool/branches/${P}_b$i'"; done
  in_engine "rm -rf '/pool/images/$P' '$STAGE'"
fi

echo; echo "RESULT: $PASS passed, $FAIL failed"
echo "" >> "$SUMMARY"; echo "**RESULT: $PASS passed, $FAIL failed**" >> "$SUMMARY"
[[ "$FAIL" == "0" ]]
