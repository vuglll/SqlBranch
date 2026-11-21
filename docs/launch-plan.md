# Launch plan

How to get attention once the tool exists. Launch at release 0.2, when there
is both the fast clone and the one-line test fixture to show.

## One message per audience

| Audience | Message |
|---|---|
| Testers and .NET developers | Every test starts from the same full-size database. |
| DBAs and DevOps | Clone a 50 GB SQL Server database in seconds, free. |
| AI tooling crowd | A disposable SQL Server database for your coding agent. |

## Before launch (while building 0.1)

- **Benchmark.** The spike already has the headline: a 51 GiB database
  branched in about half a second on a laptop, and in about 260 ms on a
  GitHub runner. Re-measure on x86-64 Linux before publishing, then turn
  the numbers into an honest table against
  restore-from-backup, a fresh Testcontainers database, and Respawn. Publish
  the script so anyone can rerun it.
- **Demo.** A 60-second video of the 50 GB clone at the top of the README,
  next to the one-line test fixture.
- **Early users.** Three to five teams using it, and one quotable sentence
  from each.
- **Name.** Settle it and check NuGet, GitHub and trademarks (see below).

## Launch week (at 0.2)

- **Anchor post:** "I cloned a 50 GB SQL Server database in X ms." It uses
  Brent Ozar's Stack Overflow database, so credit him and send him the link.
- **Reddit:** r/dotnet on a weekend with the Promotion flair, written by
  hand (the subreddit removes AI-written promotion). Add r/SQLServer and
  r/programiranje.
- **Hacker News:** "Show HN: DBLab-style branching for SQL Server." Show HN
  was blocked for the account last time, so build some comment history
  first.
- **Newsletters and creators:** send the post to the .NET and SQL Server
  weeklies. A benchmark with a video is easy for them to feature.

## After launch

- **One integration per release:** GitHub Action, Aspire, MCP server. Each
  is a reason to post again, and each has its own directory where people
  look for tools.
- **Work with Testcontainers, not against it.** A "use both" guide reaches
  its users without a fight.
- **Talks:** SQL Saturday and Data Saturday events, Boston Code Camp, .NET
  user groups in Boston and Belgrade.
- **Existing audience:** link it from the EFDoctor README.
- **Search pages:** "SQL Clone alternative" and "database branching for SQL
  Server" comparison pages in the docs.

## What to measure

- Downloads of the testing package (CI restores mean real use)
- Issues opened by strangers
- Replies to a "using this at work?" link in the README
- GitHub stars, as a weak signal only

## Name options

| Name | For | Against |
|---|---|---|
| **SqlBranch** (working name) | Matches what people search for: "database branching" | "Branch" suggests merging back, which it does not do |
| **DbFork** | Short and accurate: a fork never merges back. Not tied to one engine | Less searchable |
| **SqlFork** | Same idea, tied to SQL Server | Harder to extend to other databases |
| **FreshDb** | Speaks to testers: a fresh database per test | Undersells the developer and DBA use |
| **ThinClone** | The term DBAs already use | Describes the technique, not the benefit |

Current pick: keep SqlBranch for the search match, DbFork as runner-up.

A web search on 2026-11-16 found nothing using DbFork, SqlFork, FreshDb or
ThinClone. That is not proof they are free: check NuGet and GitHub directly.
"Offshoot" was dropped because a tool by that name already branches SQLite
databases.
