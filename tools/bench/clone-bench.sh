#!/usr/bin/env bash
# Times `sqlbranch branch create` against one image and shows how much the pool grew.
#
# Usage: tools/bench/clone-bench.sh <image> [count] [prefix]
#
#   image   an image that already exists (sqlbranch image build ...)
#   count   how many branches to create, default 5
#   prefix  start of the branch names, default "bench"; the names are <prefix>-1, <prefix>-2, ...
#
# The command to run is $SQLBRANCH (default: sqlbranch on PATH). It may hold arguments split on spaces,
# for example SQLBRANCH="dotnet run --project src/SqlBranch.Cli --".
#
# The script only removes branches it created itself. If a branch named <prefix>-<n> already exists, the
# create fails, the script says so, and that branch is left alone. Exit codes: 0 every create worked,
# 1 a create failed or the pool could not be read, 2 bad arguments.
#
# For the 50 GB release check use an image of that size on a pool with room for it. A thin branch should
# take under 10 seconds each. Run it on the hardware you want numbers for.
set -euo pipefail

if [[ $# -lt 1 || $# -gt 3 || "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '2,/^set /p' "$0" | sed '$d' | sed 's/^# \{0,1\}//' >&2
    [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]] && exit 0
    exit 2
fi

image=$1
count=${2:-5}
prefix=${3:-bench}

if [[ ! "$count" =~ ^[1-9][0-9]*$ ]]; then
    echo "error: count must be a positive whole number, got '$count'" >&2
    exit 2
fi
if [[ ! "$image" =~ ^[a-z0-9][a-z0-9_-]{0,62}$ || ! "$prefix" =~ ^[a-z0-9][a-z0-9_-]{0,55}$ ]]; then
    echo "error: image and prefix use lowercase letters, digits, hyphen and underscore (prefix at most 55 characters)" >&2
    exit 2
fi

read -r -a cli <<<"${SQLBRANCH:-sqlbranch}"

work=$(mktemp -d)
created=()

# Removes only the branches this run created, then the temporary folder.
cleanup() {
    local status=$?
    set +e
    local name
    for name in ${created[@]+"${created[@]}"}; do
        if "${cli[@]}" branch rm "$name" >"$work/rm.out" 2>&1; then
            echo "removed $name"
        else
            echo "warning: could not remove branch $name; run: sqlbranch branch rm $name" >&2
        fi
    done
    rm -rf "$work"
    exit "$status"
}
trap cleanup EXIT

# Prints the pool's used bytes from `status --format json`.
pool_used_bytes() {
    local json rc=0 used
    json=$("${cli[@]}" status --format json 2>"$work/status.err") || rc=$?
    used=$(printf '%s\n' "$json" | sed -n 's/.*"usedBytes": *\([0-9][0-9]*\).*/\1/p' | head -n 1)
    if [[ $rc -ne 0 ]]; then
        # status exits 1 when the engine is not healthy, but it can still report the pool.
        if [[ -n "$used" ]]; then
            echo "warning: sqlbranch status exited $rc; using the usedBytes it reported" >&2
        else
            echo "error: sqlbranch status failed with exit code $rc" >&2
            cat "$work/status.err" >&2
            printf '%s\n' "$json" >&2
            return 1
        fi
    fi
    if [[ -z "$used" ]]; then
        echo "error: no usedBytes in the output of sqlbranch status:" >&2
        printf '%s\n' "$json" >&2
        return 1
    fi
    printf '%s\n' "$used"
}

human() {
    awk -v b="$1" 'BEGIN {
        split("B KiB MiB GiB TiB", u, " "); i = 1; v = b + 0
        if (v < 0) { s = "-"; v = -v } else { s = "" }
        while (v >= 1024 && i < 5) { v /= 1024; i++ }
        if (i == 1) printf "%s%d %s", s, v, u[i]; else printf "%s%.1f %s", s, v, u[i]
    }'
}

used_before=$(pool_used_bytes)
echo "image: $image, branches: $count, names: $prefix-1 .. $prefix-$count"
echo "pool used before: $(human "$used_before") ($used_before bytes)"

TIMEFORMAT=%R
total_ms=0
failed=0
for ((i = 1; i <= count; i++)); do
    name="$prefix-$i"
    rc=0
    { time "${cli[@]}" branch create "$name" --from "$image" >"$work/create.out" 2>"$work/create.err"; } 2>"$work/time" || rc=$?
    seconds=$(tr -d '[:space:]' <"$work/time")
    if [[ $rc -ne 0 ]]; then
        echo "create $name: FAILED with exit code $rc after ${seconds}s" >&2
        cat "$work/create.err" >&2
        failed=1
        break
    fi
    created+=("$name")
    mode=$(sed -n 's/^[Cc]lone mode: *//p' "$work/create.out" | head -n 1)
    ms=$(awk -v s="${seconds/,/.}" 'BEGIN { printf "%d", s * 1000 + 0.5 }')
    total_ms=$((total_ms + ms))
    echo "create $name: ${seconds}s (clone mode: ${mode:-unknown})"
done

if [[ $failed -eq 0 ]]; then
    used_after=$(pool_used_bytes)
    growth=$((used_after - used_before))
    echo "pool used after:  $(human "$used_after") ($used_after bytes)"
    echo "pool growth: $(human "$growth") ($growth bytes) for $count branches, $(human $((growth / count))) per branch"
    awk -v t="$total_ms" -v n="$count" 'BEGIN { printf "creates: %d, average %.2fs, total %.2fs\n", n, t / n / 1000, t / 1000 }'
fi

exit "$failed"
