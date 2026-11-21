#!/usr/bin/env bash
# Start Colima with Rosetta (needed for SQL Server on Apple Silicon).
# Usage: ./00-colima.sh [disk-GB]     default 150
source "$(dirname "$0")/lib.sh"

need colima "Install it with: brew install colima"
need docker "Install the CLI with: brew install docker"
DISK="${1:-150}"

if colima status >/dev/null 2>&1; then
  note "Colima is already running. If it was started without Rosetta, run:"
  note "  colima stop && colima start --vm-type vz --vz-rosetta"
else
  note "Starting Colima (vz, Rosetta, 4 CPUs, 8 GB memory, ${DISK} GB disk)"
  colima start --vm-type vz --vz-rosetta --cpu 4 --memory 8 --disk "$DISK"
fi

note "Docker context: $(docker context show)"
docker info --format 'Docker host: {{.OperatingSystem}} / {{.Architecture}} / kernel {{.KernelVersion}}'
colima ssh -- uname -a
note "OK. Next: ./01-pool.sh"
