#!/usr/bin/env bash
# Runs INSIDE the engine container. Clones an image's files for a branch.
# Args: IMAGE BRANCH [MODE]     MODE is "always" (thin, default) or "never" (full copy)
set -euo pipefail
IMAGE="$1"; BRANCH="$2"; MODE="${3:-always}"
SRC="/pool/images/$IMAGE/data"; DST="/pool/branches/$BRANCH/data"
[ -d "$SRC" ] || { echo "image $IMAGE not found" >&2; exit 1; }
[ -e "/pool/branches/$BRANCH" ] && { echo "branch folder $BRANCH already exists" >&2; exit 1; }
mkdir -p "$DST"
for f in "$SRC"/*; do
  cp --reflink="$MODE" "$f" "$DST/"
done
chmod u+w "$DST"/*
