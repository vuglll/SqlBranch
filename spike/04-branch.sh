#!/usr/bin/env bash
# Create a branch: clone the image's files and attach them as a new database.
# Usage: ./04-branch.sh <branch-name> <image-name>
# Set MODE=never to force a full copy, for comparison.
source "$(dirname "$0")/lib.sh"

BRANCH="${1:-}"; IMAGE="${2:-}"
[[ -n "$BRANCH" && -n "$IMAGE" ]] || die "usage: $0 <branch-name> <image-name>"
valid_name "$BRANCH"; valid_name "$IMAGE"
branch_exists "$BRANCH" && die "database $BRANCH already exists"

U0=$(pool_used); T0=$(now_ms)
engine_script "$SPIKE_DIR/engine/clone.sh" "$IMAGE" "$BRANCH" "${MODE:-always}"
T1=$(now_ms)
sql "CREATE DATABASE [$BRANCH] ON $(attach_list "/pool/branches/$BRANCH/data") FOR ATTACH;" >/dev/null
T2=$(now_ms); U1=$(pool_used)

echo "branch=$BRANCH image=$IMAGE mode=${MODE:-always} clone_ms=$((T1-T0)) attach_ms=$((T2-T1)) total_ms=$((T2-T0)) pool_growth_mib=$(( (U1-U0)/1048576 ))"
