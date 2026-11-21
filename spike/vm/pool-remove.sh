#!/usr/bin/env bash
# Runs as root INSIDE the Colima VM. Unmounts the pool and deletes its image file.
set -euo pipefail
MNT="${1:-/mnt/sqlbranch-pool}"
IMG="$(findmnt -n -o SOURCE "$MNT" 2>/dev/null || true)"
BACKING=""
if [ -n "$IMG" ]; then
  BACKING="$(losetup -n -O BACK-FILE "$IMG" 2>/dev/null || true)"
  umount "$MNT"
fi
for f in "$BACKING" /var/lib/sqlbranch/pool.img /mnt/lima-colima/sqlbranch/pool.img /mnt/sqlbranch/pool.img; do
  [ -n "$f" ] && [ -f "$f" ] && { rm -f "$f"; echo "deleted $f"; }
done
echo "pool removed"
