#!/usr/bin/env bash
# Remove spike artifacts.
# Usage: ./07-cleanup.sh <branch> [<branch>...]   remove named branches
#        ./07-cleanup.sh --branches               remove every branch
#        ./07-cleanup.sh --engine                 stop and remove the container
#        ./07-cleanup.sh --pool                   also unmount and delete the pool (all data gone)
source "$(dirname "$0")/lib.sh"
[[ $# -ge 1 ]] || die "see the usage lines at the top of this script"

case "$1" in
  --branches)
    for b in $(in_engine "ls -1 /pool/branches 2>/dev/null"); do note "removing $b"; drop_branch "$b"; done ;;
  --engine)
    docker rm -f "$ENGINE" >/dev/null 2>&1 || true; note "container $ENGINE removed" ;;
  --pool)
    docker rm -f "$ENGINE" >/dev/null 2>&1 || true
    if [[ "${DOCKER_CONTEXT:-}" == "desktop-linux" ]]; then
      # Never fall through to vm_script here: that runs in the Colima VM and would remove Colima's pool.
      [[ "$POOL_MNT" == /var/lib/sqlbranch* ]] || die "unexpected POOL_MNT for Docker Desktop: $POOL_MNT"
      docker run --rm --privileged --pid=host ubuntu:24.04 nsenter -t 1 -m -- sh -c \
        "if mountpoint -q '$POOL_MNT'; then umount '$POOL_MNT'; fi; rm -rf '$(dirname "$POOL_MNT")'; echo 'Docker Desktop pool removed'"
      note "kept $PW_FILE (the Colima engine may still use it)"
    else
      vm_script "$SPIKE_DIR/vm/pool-remove.sh" "$POOL_MNT"
      rm -f "$PW_FILE"
    fi ;;
  *)
    for b in "$@"; do valid_name "$b"; note "removing $b"; drop_branch "$b"; done ;;
esac
