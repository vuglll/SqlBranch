#!/usr/bin/env bash
# Gate G2: what does the first write to shared (reflinked) blocks cost?
# The same update + insert workload runs on a thin branch (blocks shared with the image)
# and on a full copy (MODE=never, nothing shared), RUNS times each, interleaved, with the
# OS page cache dropped before every run. Then the workload runs again on one branch to see
# whether the cost goes away once its blocks are no longer shared (task 4.4).
#
# Usage: ./08-firstwrite.sh <image-name> [runs]          default 3
# Knobs: EVERY=10   update every Nth row of dbo.Posts (StackOverflow images)
#        INSERT_ROWS=1000000   rows of 200 bytes inserted into a new table
#        PASSES=2   passes on the repeat branch;  KEEP=1 keeps the branches
#        COW_EXTSIZE=8k   set XFS's copy-on-write extent size hint on /pool/branches for this
#                   run (restored afterwards). XFS's default copies 128 KiB around every write to a
#                   shared block; a SQL Server page is 8 KiB. Compare a run with and without it.
# Each full copy needs as much free pool space as the image.
source "$(dirname "$0")/lib.sh"

IMAGE="${1:-}"; RUNS="${2:-3}"
[[ -n "$IMAGE" ]] || die "usage: $0 <image-name> [runs]"
EVERY="${EVERY:-10}"; INSERT_ROWS="${INSERT_ROWS:-1000000}"; PASSES="${PASSES:-2}"

IMG_BYTES=$(in_engine "du -sb /pool/images/$IMAGE/data | cut -f1")
FREE_BYTES=$(in_engine "df --output=avail -B1 /pool | tail -1 | tr -d ' '")
(( FREE_BYTES > IMG_BYTES * 12 / 10 )) || die "pool has $((FREE_BYTES>>30)) GiB free, a full copy needs about $((IMG_BYTES>>30)) GiB plus room to write"

drop_caches() { host_root "sync; echo 3 > /proc/sys/vm/drop_caches"; }
# File fragments in a branch's data files (more fragments = more scattered reads later).
extents() { host_root "filefrag $POOL_MNT/branches/$1/data/* | awk '{s+=\$(NF-2)} END{print s}'"; }
cowext() { host_root "xfs_io -c cowextsize $POOL_MNT/branches" | tr -dc '0-9'; }

COW_BEFORE=$(cowext)
if [[ -n "${COW_EXTSIZE:-}" ]]; then
  host_root "xfs_io -c 'cowextsize $COW_EXTSIZE' $POOL_MNT/branches"
  trap 'host_root "xfs_io -c \"cowextsize $COW_BEFORE\" $POOL_MNT/branches" >/dev/null' EXIT
fi
echo "cowextsize_bytes=$(cowext) (0 means the XFS default, 32 blocks = 128 KiB)"

# Runs the workload once on database $1; table suffix $2. Prints "update_ms insert_ms".
workload() {
  local db="$1" n="$2" t0 t1 t2
  t0=$(now_ms)
  DB="$db" sql "UPDATE dbo.Posts SET Score = Score + 1 WHERE Id % $EVERY = 0; CHECKPOINT;" >/dev/null
  t1=$(now_ms)
  DB="$db" sql "CREATE TABLE dbo.fw_ins_$n (id int IDENTITY PRIMARY KEY, payload char(200) NOT NULL);
INSERT dbo.fw_ins_$n WITH (TABLOCK) (payload)
SELECT TOP ($INSERT_ROWS) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
CHECKPOINT;" >/dev/null
  t2=$(now_ms)
  echo "$((t1-t0)) $((t2-t1))"
}

median() { tr ' ' '\n' | grep . | sort -n | awk '{a[NR]=$1} END{ if (NR%2) print a[(NR+1)/2]; else printf "%d\n", (a[NR/2]+a[NR/2+1])/2 }'; }
ratio()  { perl -e "printf '%.2f', $1/$2"; }

note "Checking the image has dbo.Posts"
drop_branch "fw-check"
"$SPIKE_DIR/04-branch.sh" "fw-check" "$IMAGE" >/dev/null
ROWS=$(DB="fw-check" sql_scalar "SELECT CASE WHEN OBJECT_ID('dbo.Posts') IS NULL THEN -1 ELSE (SELECT COUNT_BIG(*) FROM dbo.Posts WHERE Id % $EVERY = 0) END")
drop_branch "fw-check"
[[ "$ROWS" != "-1" ]] || die "image $IMAGE has no dbo.Posts; use a StackOverflow image"
echo "image=$IMAGE size_gib=$((IMG_BYTES>>30)) rows_updated_per_run=$ROWS insert_rows=$INSERT_ROWS runs=$RUNS"

TU=""; TI=""; CU=""; CI=""
for i in $(seq 1 "$RUNS"); do
  for kind in thin copy; do
    b="fw-$kind-$i"; drop_branch "$b"
    if [[ "$kind" == thin ]]; then MODE=always "$SPIKE_DIR/04-branch.sh" "$b" "$IMAGE"
    else MODE=never "$SPIKE_DIR/04-branch.sh" "$b" "$IMAGE"; fi
    drop_caches
    U0=$(pool_used)
    read -r U I < <(workload "$b" 1)
    G=$(( ($(pool_used) - U0) / 1048576 ))
    echo "run=$i kind=$kind update_ms=$U insert_ms=$I total_ms=$((U+I)) pool_growth_mib=$G extents=$(extents "$b")"
    if [[ "$kind" == thin ]]; then TU+="$U "; TI+="$I "; else CU+="$U "; CI+="$I "; fi
    [[ "${KEEP:-0}" == "1" ]] || drop_branch "$b"
  done
done

note "Repeat passes on one branch (task 4.4)"
b="fw-repeat"; drop_branch "$b"
"$SPIKE_DIR/04-branch.sh" "$b" "$IMAGE"
for p in $(seq 1 "$PASSES"); do
  drop_caches
  U0=$(pool_used)
  read -r U I < <(workload "$b" "$p")
  echo "repeat pass=$p update_ms=$U insert_ms=$I total_ms=$((U+I)) pool_growth_mib=$(( ($(pool_used) - U0) / 1048576 )) extents=$(extents "$b")"
done
[[ "${KEEP:-0}" == "1" ]] || drop_branch "$b"

MTU=$(echo "$TU" | median); MTI=$(echo "$TI" | median)
MCU=$(echo "$CU" | median); MCI=$(echo "$CI" | median)
echo
echo "median thin: update_ms=$MTU insert_ms=$MTI total_ms=$((MTU+MTI))"
echo "median copy: update_ms=$MCU insert_ms=$MCI total_ms=$((MCU+MCI))"
echo "ratio thin/copy: update=$(ratio "$MTU" "$MCU") insert=$(ratio "$MTI" "$MCI") total=$(ratio "$((MTU+MTI))" "$((MCU+MCI))")"
note "G2 records the ratio; 1.00 means shared blocks cost nothing extra on first write."
