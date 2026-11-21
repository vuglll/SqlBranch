#!/usr/bin/env bash
# Start one SQL Server container with the pool mounted at /pool.
# Usage: ./02-engine.sh
source "$(dirname "$0")/lib.sh"
need docker "Run ./00-colima.sh (Mac) or ./00-linux.sh (Linux) first"

if docker ps -a --format '{{.Names}}' | grep -qx "$ENGINE"; then
  note "Starting existing container $ENGINE"
  docker start "$ENGINE" >/dev/null
else
  note "Creating container $ENGINE from $ENGINE_IMAGE"
  docker run -d --name "$ENGINE" --platform linux/amd64 \
    --label io.sqlbranch.spike=true \
    -e ACCEPT_EULA=Y -e MSSQL_PID=Developer \
    -e MSSQL_SA_PASSWORD="$(sa_password)" \
    -p "127.0.0.1:${PORT}:1433" \
    -v "${POOL_MNT}:/pool" \
    -v "${POOL_MNT}/engine:/var/opt/mssql" \
    "$ENGINE_IMAGE" >/dev/null
fi

note "Waiting for SQL Server (first start can take a minute, longer under Rosetta)"
wait_for_engine
sql "SELECT @@VERSION AS version"

note "Reflink probe as the engine's own user"
in_engine 'set -e; rm -f /pool/.probe/a /pool/.probe/b
  dd if=/dev/zero of=/pool/.probe/a bs=1M count=1 status=none
  cp --reflink=always /pool/.probe/a /pool/.probe/b && echo "PROBE: thin (reflink works inside the container)"
  rm -f /pool/.probe/a /pool/.probe/b'

note "OK. Connect locally at 127.0.0.1,${PORT} as sa (password in spike/.sa-password)"
note "Next: ./03-image.sh <image-name> <folder-with-mdf-ldf-files>"
