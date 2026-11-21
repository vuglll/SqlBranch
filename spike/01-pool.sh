#!/usr/bin/env bash
# Create (or re-mount) the XFS pool: inside the Colima VM on a Mac, or on the Linux host.
# Usage: ./01-pool.sh [size-GB] [image-dir]     default 120 (a sparse loopback file)
#        POOL_DEVICE=/dev/nvme1n1 ./01-pool.sh   format a whole empty data disk instead (Linux)
# Run it again after every restart: the mount does not survive one.
source "$(dirname "$0")/lib.sh"
[[ "$HOST_MODE" == native ]] || need colima "Run ./00-colima.sh first"

vm_script "$SPIKE_DIR/vm/pool.sh" "${1:-120}" "$POOL_MNT" "${2:-}" "${POOL_DEVICE:-}"
note "OK. Next: ./02-engine.sh"
