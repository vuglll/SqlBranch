#!/usr/bin/env bash
# Prepare a rented x86-64 Ubuntu VM for the G2 run, and record its facts.
# Usage: ./00-linux.sh            (needs sudo; Ubuntu 22.04 or newer)
# Installs Docker (Ubuntu's docker.io), xfsprogs and 7-Zip, adds you to the docker group,
# and writes the machine facts to spike/linux-facts.txt for docs/research/spike-results.md.
source "$(dirname "$0")/lib.sh"
[[ "$HOST_MODE" == native ]] || die "this is for a Linux host; on a Mac use ./00-colima.sh"
[[ "$(uname -m)" == x86_64 ]] || note "WARNING: $(uname -m) is not x86_64; G2 timings are meant for x86-64"

note "Installing docker.io, xfsprogs, 7zip"
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker.io xfsprogs 7zip perl openssl >/dev/null
sudo systemctl enable --now docker >/dev/null 2>&1 || true
if ! id -nG | grep -qw docker; then
  sudo usermod -aG docker "$USER"
  note "Added $USER to the docker group. Log out and back in (or run 'newgrp docker'), then continue with ./01-pool.sh"
fi

FACTS="$SPIKE_DIR/linux-facts.txt"
{
  echo "date: $(date -u +%Y-%m-%dT%H:%MZ)"
  echo "os: $(. /etc/os-release && echo "$PRETTY_NAME")"
  echo "kernel: $(uname -r)"
  echo "cpu: $(lscpu | sed -n 's/^Model name:[[:space:]]*//p' | head -1), $(nproc) vCPU"
  echo "memory_gib: $(free -g | awk '/^Mem:/{print $2}')"
  echo "virtualization: $(systemd-detect-virt 2>/dev/null || echo unknown)"
  echo "docker: $(sudo docker version --format '{{.Server.Version}}' 2>/dev/null || echo 'not running')"
  echo "disks (ROTA=1 means spinning; TRAN/MODEL hint at local NVMe vs network storage):"
  lsblk -d -e 7 -o NAME,SIZE,ROTA,TRAN,MODEL,MOUNTPOINT | sed 's/^/  /'
} | tee "$FACTS"
note "Facts saved to $FACTS. Also write down the provider, instance type and disk type by hand."
note "Next: pick the empty data disk from the list above, then POOL_DEVICE=/dev/<disk> ./01-pool.sh"
