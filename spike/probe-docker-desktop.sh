#!/usr/bin/env bash
# Gate G3: can Docker Desktop's own VM host a thin pool? Leaves nothing behind.
# Usage: ./probe-docker-desktop.sh [docker-context]     default desktop-linux
set -euo pipefail
CTX="${1:-desktop-linux}"
docker --context "$CTX" run --rm --privileged ubuntu:24.04 bash -c '
set -e
apt-get update -qq && apt-get install -y -qq xfsprogs >/dev/null
truncate -s 2G /pool.img && mkfs.xfs -q -m reflink=1 /pool.img
mkdir /pool && mount -o loop /pool.img /pool
dd if=/dev/zero of=/pool/a bs=1M count=100 status=none
cp --reflink=always /pool/a /pool/b && echo THIN-OK'
