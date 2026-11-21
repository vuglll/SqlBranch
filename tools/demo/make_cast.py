"""Builds docs/demo.gif's recording: a replay of a real SqlBranch session.

The commands, their output and the timing of `branch create` come from the
README quick-start run on x86-64 Linux (Hetzner CCX33, 2026-11-20) with the
50.78 GiB StackOverflow2013 image; see
docs/research/2026-11-20-x86-64-release-gate-run.md. `init` and `image build`
are shown sped up, with their real durations noted on screen. The password
from `branch conn` is masked.

Render it with agg (https://github.com/asciinema/agg):

    python3 tools/demo/make_cast.py demo.cast
    agg --font-size 18 --theme github-dark --idle-time-limit 4 demo.cast docs/demo.gif
"""
import json, sys
out = sys.argv[1]
events = []
t = 0.0
G, DIM, BOLD, R = "\x1b[32m", "\x1b[2m", "\x1b[1m", "\x1b[0m"
def emit(text, dt=0.0):
    global t
    t += dt
    events.append([round(t, 3), "o", text])
def prompt(): emit(f"{G}${R} ", 0.4)
def typed(cmd):
    for ch in cmd:
        emit(ch, 0.035)
    emit("\r\n", 0.35)
def lines(text, dt=0.0):
    first = True
    for line in text.strip("\n").split("\n"):
        emit(line + "\r\n", dt if first else 0.02)
        first = False
def note(text, dt): emit(f"{DIM}{text}{R}\r\n", dt)

prompt(); typed("sqlbranch init")
note("  … 31 s on the real run, sped up", 0.8)
lines("""SqlBranch is ready.
Engine: SQL Server 16.0.4325.4, Developer Edition (64-bit)
Pool backend: loop-xfs
Clone mode: thin""", 0.6)
prompt(); typed("sqlbranch image build so2013 --from-bak so2013.bak")
note("  … 117 s on the real run, one time only, sped up", 0.8)
lines("""Image so2013 is built from so2013.bak.
Files: 5, 50.78 GiB
Clone mode: thin""", 0.9)
prompt(); typed("sqlbranch branch create pr-142 --from so2013")
lines(f"""{BOLD}Branch pr-142 is created from the image so2013.{R}
Clone mode: thin
Server: 127.0.0.1,14330
Database: pr-142""", 3.527)
prompt(); typed("sqlbranch branch list")
lines("""NAME    IMAGE   CLONE MODE  CREATED               STATE   OWN DATA  EPHEMERAL
pr-142  so2013  thin        2026-11-20 01:25 UTC  online  288 KiB   no""", 0.8)
prompt(); typed("sqlbranch branch conn pr-142")
lines("""Data Source=127.0.0.1,14330;Initial Catalog=pr-142;User ID=sa;Password=********;Trust Server Certificate=True""", 0.6)
prompt(); typed("sqlbranch branch rm pr-142")
lines("Branch pr-142 is removed.", 0.8)
prompt(); emit("", 3.0)

with open(out, "w") as f:
    f.write(json.dumps({"version": 2, "width": 112, "height": 22, "title": "SqlBranch: replay of a recorded session on x86-64 Linux"}) + "\n")
    for e in events:
        f.write(json.dumps(e) + "\n")
print(f"{len(events)} events, {t:.1f} s")
