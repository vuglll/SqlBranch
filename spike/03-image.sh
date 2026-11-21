#!/usr/bin/env bash
# Build a sealed image from database files (mdf, ndf, ldf) in a folder on this machine.
# Usage: ./03-image.sh <image-name> <folder-on-mac>
# Set REBUILD_LOG=1 to replace the log with a fresh, minimal one before sealing.
# Set HASH=1 to also record SHA-256 of the sealed files (slow on big databases).
source "$(dirname "$0")/lib.sh"

IMAGE="${1:-}"; SRC="${2:-}"
[[ -n "$IMAGE" && -n "$SRC" ]] || die "usage: $0 <image-name> <folder-on-mac>"
valid_name "$IMAGE"
[[ -d "$SRC" ]] || die "folder not found: $SRC"
in_engine "[ ! -e /pool/images/$IMAGE ]" || die "image $IMAGE already exists"

shopt -s nullglob nocaseglob
FILES=( "$SRC"/*.mdf "$SRC"/*.ndf "$SRC"/*.ldf )
shopt -u nocaseglob
[[ ${#FILES[@]} -ge 2 ]] || die "expected at least one .mdf and one .ldf in $SRC"

STAGE="/pool/staging/$IMAGE"
TMPDB="img_tmp_${IMAGE//-/_}"
cleanup() {
  sql "IF DB_ID(N'$TMPDB') IS NOT NULL BEGIN ALTER DATABASE [$TMPDB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sp_detach_db N'$TMPDB'; END" >/dev/null 2>&1 || true
  in_engine "rm -rf '$STAGE'" || true
}
trap 'echo "build failed, cleaning up" >&2; cleanup' ERR

in_engine "rm -rf '$STAGE' && mkdir -p '$STAGE'"
T0=$(now_ms)
for f in "${FILES[@]}"; do
  note "Copying $(basename "$f") into the pool ($(du -h "$f" | cut -f1))"
  docker cp "$f" "$ENGINE:$STAGE/"
done
docker exec -u 0 "$ENGINE" chown -R mssql:0 "$STAGE"
T1=$(now_ms)

note "Attaching as $TMPDB (older databases are upgraded here, which can take a while)"
sql "CREATE DATABASE [$TMPDB] ON $(attach_list "$STAGE") FOR ATTACH;"
sql "ALTER DATABASE [$TMPDB] SET RECOVERY SIMPLE;"
DB="$TMPDB" sql "CHECKPOINT;"
note "Source database facts"
DB="$TMPDB" sql "SELECT name, type_desc, size/128 AS size_mb FROM sys.database_files;"
sql "ALTER DATABASE [$TMPDB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sp_detach_db N'$TMPDB';"
if [[ "${REBUILD_LOG:-0}" == "1" ]]; then
  # Every branch attach rewrites part of the log (about 68 MiB of a 256 MiB log on so2010),
  # and DBCC SHRINKFILE cannot go below two VLFs. A rebuilt log is about 0.5 MB.
  # Safe only after the clean detach above.
  note "Rebuilding the log file"
  in_engine "cd '$STAGE' && rm -f -- *.ldf *.LDF"
  sql "CREATE DATABASE [$TMPDB] ON $(attach_list "$STAGE") FOR ATTACH_REBUILD_LOG;"
  DB="$TMPDB" sql "SELECT name, type_desc, size/128.0 AS size_mb FROM sys.database_files;"
  sql "ALTER DATABASE [$TMPDB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sp_detach_db N'$TMPDB';"
fi
T2=$(now_ms)

in_engine "mkdir -p '/pool/images/$IMAGE' && mv '$STAGE' '/pool/images/$IMAGE/data' && chmod a-w /pool/images/$IMAGE/data/*"
if [[ "${HASH:-0}" == "1" ]]; then
  note "Hashing sealed files"
  in_engine "cd '/pool/images/$IMAGE/data' && sha256sum * > ../SHA256SUMS"
fi
trap - ERR

in_engine "ls -l '/pool/images/$IMAGE/data'; du -sh '/pool/images/$IMAGE/data'"
echo "image=$IMAGE copy_ms=$((T1-T0)) seal_ms=$((T2-T1))"
note "OK. Next: ./04-branch.sh <branch-name> $IMAGE"
