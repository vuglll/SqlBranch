#!/usr/bin/env bash
# Timing and space for gate G1: N timed branch creations, then a batch for pool growth.
# Usage: ./06-bench.sh <image-name> [timed-runs] [batch-size]     defaults 5 and 20
source "$(dirname "$0")/lib.sh"

IMAGE="${1:-}"; RUNS="${2:-5}"; BATCH="${3:-20}"
[[ -n "$IMAGE" ]] || die "usage: $0 <image-name> [timed-runs] [batch-size]"

IMG_BYTES=$(in_engine "du -sb /pool/images/$IMAGE/data | cut -f1")
echo "image=$IMAGE size_gib=$(( IMG_BYTES / 1073741824 ))"

note "$RUNS timed creations"
for i in $(seq 1 "$RUNS"); do
  drop_branch "bench-t$i"
  "$SPIKE_DIR/04-branch.sh" "bench-t$i" "$IMAGE"
done

note "Pool growth for $BATCH branches"
for i in $(seq 1 "$BATCH"); do drop_branch "bench-b$i"; done
U0=$(pool_used)
for i in $(seq 1 "$BATCH"); do "$SPIKE_DIR/04-branch.sh" "bench-b$i" "$IMAGE" >/dev/null; done
U1=$(pool_used)
GROWTH=$(( U1 - U0 ))
echo "batch=$BATCH growth_mib=$(( GROWTH / 1048576 )) growth_percent_of_image=$(perl -e "printf '%.2f', 100*$GROWTH/$IMG_BYTES")"
echo "G1 needs: every total_ms under 10000, growth under 5 percent"

if [[ "${KEEP:-0}" != "1" ]]; then
  note "Removing bench branches (set KEEP=1 to keep them)"
  for i in $(seq 1 "$RUNS");  do drop_branch "bench-t$i"; done
  for i in $(seq 1 "$BATCH"); do drop_branch "bench-b$i"; done
fi
