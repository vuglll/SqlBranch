#!/usr/bin/env bash
# Runs as root INSIDE the Colima VM. Creates and mounts the XFS pool.
# Args: SIZE_GB  MOUNT_POINT  [IMAGE_DIR]  [DEVICE]
# With DEVICE (a whole data disk), the pool is that disk instead of a loopback file.
# An empty disk is formatted; a disk that is already XFS is mounted as is; anything else is refused.
set -euo pipefail
SIZE_GB="${1:-120}"
MNT="${2:-/mnt/sqlbranch-pool}"
IMG_DIR_ARG="${3:-}"
DEVICE="${4:-}"

if mountpoint -q "$MNT"; then
  echo "pool already mounted at $MNT"
elif [ -n "$DEVICE" ]; then
  [ -b "$DEVICE" ] || { echo "not a block device: $DEVICE" >&2; exit 1; }
  if findmnt -rn -S "$DEVICE" >/dev/null || lsblk -nr -o MOUNTPOINT "$DEVICE" | grep -q .; then
    echo "$DEVICE (or a partition on it) is mounted somewhere else; refusing" >&2; exit 1
  fi
  if ! command -v mkfs.xfs >/dev/null 2>&1; then
    apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq xfsprogs >/dev/null
  fi
  FSTYPE="$(blkid -o value -s TYPE "$DEVICE" 2>/dev/null || true)"
  PARTS="$(lsblk -nr -o NAME "$DEVICE" | wc -l)"
  if [ "$FSTYPE" = "xfs" ]; then
    echo "$DEVICE already holds XFS, mounting it"
  elif [ -z "$FSTYPE" ] && [ "$PARTS" -le 1 ]; then
    echo "formatting empty $DEVICE as XFS with reflink"
    mkfs.xfs -q -m reflink=1 "$DEVICE"
  else
    echo "$DEVICE holds '${FSTYPE:-partitions}'; refusing to format it. Wipe it yourself if it is really spare." >&2
    exit 1
  fi
  mkdir -p "$MNT"
  mount -o noatime "$DEVICE" "$MNT"
  echo "mounted $DEVICE at $MNT"
else
  if ! command -v mkfs.xfs >/dev/null 2>&1; then
    echo "installing xfsprogs"
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq xfsprogs >/dev/null
  fi

  IMG=""
  for d in "$IMG_DIR_ARG" /var/lib/sqlbranch /mnt/lima-colima/sqlbranch /mnt/sqlbranch; do
    [ -n "$d" ] && [ -f "$d/pool.img" ] && { IMG="$d/pool.img"; break; }
  done

  if [ -z "$IMG" ]; then
    best=""; bestfree=0
    if [ -n "$IMG_DIR_ARG" ]; then
      best="$IMG_DIR_ARG"; mkdir -p "$best"
      bestfree=$(df --output=avail -BG "$best" | tail -1 | tr -dc '0-9')
    else
      for d in /var/lib /mnt/lima-colima /mnt; do
        [ -d "$d" ] || continue
        free=$(df --output=avail -BG "$d" | tail -1 | tr -dc '0-9')
        if [ "$free" -gt "$bestfree" ]; then best="$d/sqlbranch"; bestfree=$free; fi
      done
    fi
    [ -n "$best" ] || { echo "no place found for the pool image" >&2; exit 1; }
    if [ "$bestfree" -lt "$SIZE_GB" ]; then
      echo "WARNING: only ${bestfree} GB free under $best, pool size is ${SIZE_GB} GB (the file is sparse, but it can fill the disk)" >&2
    fi
    mkdir -p "$best"
    IMG="$best/pool.img"
    echo "creating ${SIZE_GB} GB sparse XFS image at $IMG"
    truncate -s "${SIZE_GB}G" "$IMG"
    mkfs.xfs -q -m reflink=1 "$IMG"
  fi

  modprobe xfs 2>/dev/null || true
  mkdir -p "$MNT"
  mount -o loop,noatime "$IMG" "$MNT"
  echo "mounted $IMG at $MNT"
fi

# The engine runs as uid 10001 (mssql).
mkdir -p "$MNT/engine" "$MNT/images" "$MNT/branches" "$MNT/staging" "$MNT/.probe"
chown 10001:0 "$MNT" "$MNT/engine" "$MNT/images" "$MNT/branches" "$MNT/staging" "$MNT/.probe"

echo "--- file system"
xfs_info "$MNT" | grep -o 'reflink=[01]'
df -h "$MNT" | tail -1

echo "--- reflink probe"
rm -f "$MNT/.probe/a" "$MNT/.probe/b"
dd if=/dev/zero of="$MNT/.probe/a" bs=1M count=1 status=none
if cp --reflink=always "$MNT/.probe/a" "$MNT/.probe/b"; then
  echo "PROBE: thin (reflink works)"
else
  echo "PROBE: copy (reflink failed)"; exit 1
fi
rm -f "$MNT/.probe/a" "$MNT/.probe/b"
