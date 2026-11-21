#!/usr/bin/env bash
# Correctness checks for gate G1 and G5: isolation, image immutability, CHECKDB, restart, error log.
# Usage: ./05-verify.sh <image-name>
source "$(dirname "$0")/lib.sh"

IMAGE="${1:-}"; [[ -n "$IMAGE" ]] || die "usage: $0 <image-name>"
A="verify-a"; B="verify-b"
PASS=0; FAIL=0
ok()  { echo "PASS  $*"; PASS=$((PASS+1)); }
bad() { echo "FAIL  $*"; FAIL=$((FAIL+1)); }

drop_branch "$A"; drop_branch "$B"

if ! in_engine "[ -f /pool/images/$IMAGE/SHA256SUMS ]"; then
  note "Hashing image files (once)"
  in_engine "cd /pool/images/$IMAGE/data && sha256sum * > ../SHA256SUMS"
fi

"$SPIKE_DIR/04-branch.sh" "$A" "$IMAGE"
"$SPIKE_DIR/04-branch.sh" "$B" "$IMAGE"

note "Writing one million rows into $A (and touching existing rows if dbo.Users exists)"
T0=$(now_ms)
DB="$A" sql "
CREATE TABLE dbo.spike_writes (id int IDENTITY PRIMARY KEY, payload char(200) NOT NULL);
INSERT dbo.spike_writes (payload)
SELECT TOP (1000000) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b;
IF OBJECT_ID('dbo.Users') IS NOT NULL
  UPDATE TOP (100000) dbo.Users SET Reputation = Reputation + 1;
CHECKPOINT;" >/dev/null
T1=$(now_ms); echo "write_ms=$((T1-T0))"

ROWS=$(DB="$A" sql_scalar "SELECT COUNT(*) FROM dbo.spike_writes")
if [[ "$ROWS" == "1000000" ]]; then ok "branch $A holds 1000000 written rows"; else bad "branch $A row count is '$ROWS'"; fi

SEEN=$(DB="$B" sql_scalar "SELECT CASE WHEN OBJECT_ID('dbo.spike_writes') IS NULL THEN 0 ELSE 1 END")
if [[ "$SEEN" == "0" ]]; then ok "branch $B does not see the writes (isolation)"; else bad "branch $B sees the writes"; fi

note "Re-checking image hashes"
if in_engine "cd /pool/images/$IMAGE/data && sha256sum --quiet -c ../SHA256SUMS"; then
  ok "image files unchanged after branch writes"
else
  bad "image files changed"
fi

note "DBCC CHECKDB on $A (full) and $B (physical only)"
T0=$(now_ms)
if sql "DBCC CHECKDB (N'$A') WITH NO_INFOMSGS, ALL_ERRORMSGS;" >/dev/null; then ok "CHECKDB clean on written branch"; else bad "CHECKDB reported errors on $A"; fi
if sql "DBCC CHECKDB (N'$B') WITH NO_INFOMSGS, PHYSICAL_ONLY;" >/dev/null; then ok "CHECKDB clean on untouched branch"; else bad "CHECKDB reported errors on $B"; fi
T1=$(now_ms); echo "checkdb_ms=$((T1-T0))"

note "Restarting the engine"
docker restart "$ENGINE" >/dev/null; wait_for_engine
# name and state_desc have different collations, so STRING_AGG needs an explicit one (Msg 4191).
STATE=$(sql_scalar "SELECT STRING_AGG(CONVERT(nvarchar(300), name + N'=' + state_desc COLLATE DATABASE_DEFAULT) COLLATE DATABASE_DEFAULT, N',') FROM sys.databases WHERE name IN (N'$A', N'$B')") \
  || STATE="state query failed"
if [[ "$STATE" == *"$A=ONLINE"* && "$STATE" == *"$B=ONLINE"* ]]; then ok "both branches ONLINE after restart"; else bad "after restart: $STATE"; fi
ROWS=$(DB="$A" sql_scalar "SELECT COUNT(*) FROM dbo.spike_writes") || ROWS="query failed"
if [[ "$ROWS" == "1000000" ]]; then ok "written rows survive the restart"; else bad "row count after restart is '$ROWS'"; fi

note "Error log lines that mention I/O problems (G5). Read these by eye."
# Log 1 is the one from before the restart, which covers the writes and CHECKDB above.
for log in 0 1; do for term in "I/O" "FUA" "stalled" "Error: 82"; do
  sql "EXEC sp_readerrorlog $log, 1, N'$term';" 2>/dev/null | grep -v "rows affected" | grep -v '^[- ]*$' | grep -iv "LogDate" || true
done; done

echo
echo "RESULT: $PASS passed, $FAIL failed"
note "Branches $A and $B are left in place. Remove them with: ./07-cleanup.sh $A $B"
[[ "$FAIL" == "0" ]]
