#!/usr/bin/env bash
# Gate G3: create (or re-mount) the XFS pool inside Docker Desktop's VM.
# Usage: source docker-desktop.env && ./01-pool-docker-desktop.sh [size-GB]    default 60
# Then run 02 to 06 as usual in the same shell (the env file points them at Docker Desktop).
#
# Docker Desktop allows bind mounts of VM paths only under /var/lib (others are taken
# as Mac paths), and the VM has no mkfs.xfs. So the pool file is formatted from a
# privileged Ubuntu container, and mounted in the VM's own mount namespace with nsenter,
# where the engine container's bind mount can see it. Run again after every Docker
# Desktop restart: the mount does not survive one, the pool file does.
source "$(dirname "$0")/lib.sh"
need docker "Install Docker Desktop"
[[ "${DOCKER_CONTEXT:-}" == "desktop-linux" ]] || die "run 'source docker-desktop.env' first"
[[ "$POOL_MNT" == /var/lib/* ]] || die "POOL_MNT must be under /var/lib on Docker Desktop"

SIZE_GB="${1:-60}"
DIR="$(dirname "$POOL_MNT")"
IMG="$DIR/pool.img"
HELPER="ubuntu:24.04"

note "Pool file $IMG in the Docker Desktop VM"
docker run --rm --privileged -v "$DIR:$DIR" "$HELPER" bash -c "set -e
  if [ -f '$IMG' ]; then echo 'pool file exists'; exit 0; fi
  apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq xfsprogs >/dev/null
  truncate -s ${SIZE_GB}G '$IMG' && mkfs.xfs -q -m reflink=1 '$IMG'
  xfs_info '$IMG' | grep -o 'reflink=[01]'
  echo 'created ${SIZE_GB} GB sparse XFS file'"

note "Mounting it at $POOL_MNT in the VM's mount namespace"
docker run --rm --privileged --pid=host "$HELPER" nsenter -t 1 -m -- sh -c "set -e
  mkdir -p '$POOL_MNT'
  if mountpoint -q '$POOL_MNT'; then echo 'already mounted'; else mount -o loop,noatime '$IMG' '$POOL_MNT'; echo mounted; fi
  cd '$POOL_MNT' && mkdir -p engine images branches staging .probe
  chown 10001:0 . engine images branches staging .probe
  df -h '$POOL_MNT' | tail -1"

note "Reflink probe from an ordinary container"
docker run --rm -v "$POOL_MNT:/pool" "$HELPER" bash -c 'set -e
  rm -f /pool/.probe/a /pool/.probe/b
  dd if=/dev/zero of=/pool/.probe/a bs=1M count=1 status=none
  cp --reflink=always /pool/.probe/a /pool/.probe/b && echo "PROBE: thin (reflink works)"
  rm -f /pool/.probe/a /pool/.probe/b'
note "OK. Next: ./02-engine.sh"
