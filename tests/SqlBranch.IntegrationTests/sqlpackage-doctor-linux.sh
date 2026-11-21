#!/usr/bin/env bash
set -euo pipefail

if [[ ${SQLBRANCH_IT:-} != 1 ]]; then
    echo "Needs Docker. Set SQLBRANCH_IT=1 to run the SqlPackage doctor integration test." >&2
    exit 1
fi

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
socket=${SQLBRANCH_IT_DOCKER_SOCKET:-/var/run/docker.sock}
# The Docker daemon and test runner must see the temporary pool at the same path.
scratch=$(mktemp -d "$repo/.sqlbranch-it-sqlpackage.XXXXXX")
trap 'if [[ -n ${scratch:-} ]]; then rm -rf -- "$scratch"; fi' EXIT

# Images pinned by multi-arch digest (looked up 2026-11-19), because this container holds the Docker socket.
docker build -t sqlbranch-it-sqlpackage:170.5.96 - <<'DOCKERFILE'
FROM docker:cli@sha256:34507eb5069d8be82c95dd3b6987d4250fd5fee49b82e2509b7d7a7030740d6d AS docker_cli
FROM mcr.microsoft.com/dotnet/runtime:8.0@sha256:21b0f8ba67bef2f121781d17a8e21956aeaa2d8da8e4b5db0da1ea8f91e2b6a1 AS runtime8
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4
COPY --from=docker_cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=runtime8 /usr/share/dotnet/shared/Microsoft.NETCore.App/ /usr/share/dotnet/shared/Microsoft.NETCore.App/
RUN dotnet tool install --tool-path /opt/sqlpackage microsoft.sqlpackage --version 170.5.96
DOCKERFILE

# Killing the outer container skips EngineWorld disposal and can leave test resources behind.
docker run --rm \
    --mount "type=bind,src=$socket,dst=/var/run/docker.sock" \
    --mount "type=bind,src=$repo,dst=/source,readonly" \
    --mount "type=bind,src=$scratch,dst=$scratch" \
    --env "TMPDIR=$scratch" \
    --env SQLBRANCH_IT=1 \
    --env SQLBRANCH_IT_SQLPACKAGE=/opt/sqlpackage/sqlpackage \
    sqlbranch-it-sqlpackage:170.5.96 \
    bash -euo pipefail -c '
        mkdir /work
        tar -C /source --exclude=bin --exclude=obj -cf - .editorconfig Directory.Build.props global.json SqlBranch.slnx src tests |
            tar -C /work -xf -
        cd /work
        dotnet test tests/SqlBranch.IntegrationTests/SqlBranch.IntegrationTests.csproj -c Release "$@"
    ' _ \
        --filter 'Category=Integration&FullyQualifiedName~SqlPackageDoctorIntegrationTests' \
        --logger 'console;verbosity=detailed'
