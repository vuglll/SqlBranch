# Agent workflow

Who does what when a feature is built with agents. It extends "How a feature
ships" in `docs/roadmap.md`. The four agents are defined in
`.claude/agents/`.

**Build the first feature by hand with this split before automating it.** An
orchestration system is its own project. Run the steps yourself once, see
where it hurts, then script only that.

## Roles

| Step | Agent | Model | Writes files? |
|---|---|---|---|
| Propose (proposal, specs, design, tasks) | `sqlbranch-proposer` | Opus | Only under `openspec/changes/` and `docs/research/` |
| Spec review | `sqlbranch-spec-reviewer`, then **you** | Opus | No |
| Apply, one task group at a time | `sqlbranch-implementer` | Sonnet | Yes |
| Code review per task group | `sqlbranch-code-reviewer` | Opus | No |
| Archive | `sqlbranch-implementer` | Sonnet | Yes |
| Orchestrate | The main session | Opus, or you | No product code |

Why this split:

- **The stronger model writes the proposal.** Specs decide everything
  downstream. A strong author costs fewer round trips than a strong reviewer
  fixing a weak draft.
- **Reviewers run as separate agents.** Each has its own context and sees
  only the specs, the diff and the rules. A model that watched the code being
  written tends to approve it.
- **The orchestrator stays thin.** It picks the next step, hands it over and
  checks gates. Most gates are commands, not opinions.

### When to use Fable instead of Opus

Change `model:` in the agent file for that run. Worth it where a subtle
mistake is expensive:

- proposing or reviewing anything in the storage core (cloning, sealing,
  attach and detach);
- cloud credentials and anything that reaches outside the machine;
- the Windows backend;
- code that deletes files, drops databases or removes containers.

Check what your plan allows before building the loop around it.

## The loop

```
1  Orchestrator   pick the next queue row whose "Needs first" is met
2  Proposer       write the change            -> gate A
3  Spec reviewer  review the change           -> verdict + questions
4  YOU            read specs, answer questions, approve or send back
5  Implementer    apply task group N          -> gate B
6  Code reviewer  review group N              -> verdict
7                 CHANGES REQUIRED -> implementer fixes -> back to 6 (max 2 rounds)
8                 APPROVE -> next group, back to 5
9  Orchestrator   all groups done             -> gate C, open pull request to dev
10 YOU            merge
11 Implementer    archive, update roadmap
```

## Gates

Commands decide these, not a model's judgment.

| Gate | When | Must pass |
|---|---|---|
| A | After propose | `openspec validate <change> --strict` |
| B | After each task group | `dotnet build` with zero warnings; `dotnet test`; the group's integration tests with `SQLBRANCH_IT=1` |
| C | Before the pull request | Everything in the change's final "Integration checks" group |

Integration tests need Docker with the SqlBranch pool. Run the apply and
review steps on a machine that has it (your Mac with Colima, or a Linux
box), or let CI run the suite and feed the result back. A session without
Docker cannot pass gate B, and the code reviewer is told not to approve what
it could not check.

## When to stop and come to you

- The spec reviewer says NEEDS REWORK twice for the same change.
- Review and fix on one task group reaches a third round.
- The implementer reports a spec that looks wrong or impossible.
- A gate fails for a reason outside the change (broken environment, flaky
  infrastructure).
- Anything needs a decision the specs do not cover.
- A proposer could not verify a fact the design depends on.

## Your gate

Step 4 is the one place a person must look. The spec reviewer's "Questions
for the human" list is there to make it quick: answer the questions, skim
the scenarios for anything that deletes or drops, and approve. Design and
tasks follow from the specs, so they need less of your time.

## Orchestrator prompt

Paste into the main session, opened at the repository root on a machine with
Docker:

```
You orchestrate one SqlBranch feature using the agents in .claude/agents/.
You write no product code yourself.

Feature: <change name or queue row>

Follow docs/agent-workflow.md exactly:
1. Have sqlbranch-proposer write the change. Run gate A yourself.
2. Have sqlbranch-spec-reviewer review it. Show me its full output and stop.
   Wait for my approval before any code is written.
3. After I approve: for each task group in order, have
   sqlbranch-implementer apply it, run gate B yourself, then have
   sqlbranch-code-reviewer review it. Pass findings back to the implementer.
   At most two fix rounds per group, then stop and tell me.
4. When all groups are approved, run gate C and summarize: what was built,
   every command you ran with its result, anything skipped or unverified.
   Do not open or merge the pull request without asking me.

Stop and ask me whenever docs/agent-workflow.md says to. Never mark
something as passing that you did not run. Only touch the SqlBranch pool and
its own container.
```

For the changes that already exist (`add-branching-core`,
`add-test-fixtures`), skip step 1 and start at the spec review.

## Running features in parallel

After 0.2, the roadmap lists features that touch different parts of the
code. Give each its own git worktree and its own orchestrator session. Two
sessions must not share a pool: set a different `pool` and `port` for each,
or run them on different machines.

## A second implementer

The orchestrator can hand a task group of a second feature to another coding
agent, for example OpenAI's Codex CLI, while its own implementer works on the
first. The second agent replaces only the implementer step; the spec review,
the code review and the gates stay the same.

- It works in its own git worktree, never in the orchestrator's checkout.
- It runs in a sandbox that can edit files and run local commands in that
  worktree only: no Docker, no network, no `git push`, and no commits. The
  orchestrator rebases, runs the Docker integration tests, ticks the verified
  tasks, commits and pushes. Unrestricted modes are not used: they remove the
  only boundary around an unattended agent.
- The prompt names one task group and the rules from this file and
  `openspec/config.yaml` (specs are fixed, no AI attribution, no real cloud
  accounts), and asks for a final report: files changed, tests added,
  commands run and their results, and which Docker checks the orchestrator
  must run.
- Its work is reviewed like any other group, with at most two fix rounds.
  A review by a second model is a useful extra opinion, not a replacement
  for the reviewer agent.

## Keeping the agents honest

- Agents report commands and results, not "tests pass".
- A task is ticked only by the implementer and only when its verification
  ran.
- Reviewers list what they could not verify.
- Commits and pull requests carry no AI attribution lines.
- When an agent keeps making the same mistake, fix the rule in
  `openspec/config.yaml` or the agent file, not the single instance.
