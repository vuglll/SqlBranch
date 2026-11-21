# SqlBranch planning documents

Everything decided so far, in one place. Last regenerated 2026-11-16.

## Read in this order

| File | What it is |
|---|---|
| `docs/roadmap.md` | Releases, the feature queue, and the loop for building each feature with an agent |
| `docs/user-experience.md` | What a developer needs and types, per platform, and the use cases |
| `docs/feature-drafts.md` | Drafts for the 12 features after release 0.2, one OpenSpec change each |
| `docs/agent-workflow.md` | Which agent and model does each step, the gates, when to stop, and the orchestrator prompt |
| `docs/engine.md` | `init`, `status`, `doctor`, `engine start` and `stop`, and `teardown`, with what `init` creates |
| `docs/images.md` | `image build`, `image list`, `image rm`, `image export` and `image import`: the limits, the manifest, how an image is removed, the archive layout, and what an import checks |
| `docs/branches.md` | `branch create`, `list`, `conn`, `reset` and `rm`: the steps, `--copy`, `branch.json`, what a failed command leaves, and the benchmark setup |
| `docs/launch-plan.md` | How to promote it, and name options |
| `docs/research/2026-11-15-market-and-storage.md` | The facts the plan rests on, with sources, and what is unverified |
| `docs/research/windows-check.md` | How to run `tools/windows-check.ps1` on Docker Desktop for Windows, and the empty results table for task 8.3 |
| `docs/research/spike-results.md` | The spike's numbers, surprises and decision, as recorded on 2026-11-15 |

## OpenSpec changes

| Change | Release | State |
|---|---|---|
| `openspec/changes/spike-thin-clone` | 0.0 | Passed on a Mac (G1, G3, G4, G5). Open: the x86-64 Linux run for G2 |
| `openspec/changes/add-branching-core` | 0.1 | Updated with the spike's findings. Ready for spec review |
| `openspec/changes/add-test-fixtures` | 0.2 | Ready after 0.1 is archived |

`openspec/config.yaml` holds the project context and authoring rules that
OpenSpec feeds to every workflow. Running `openspec init` leaves it alone.

## Agents

`.claude/agents/` defines the four agents the workflow uses: proposer, spec
reviewer, implementer and code reviewer.

## Spike scripts

`spike/` holds the scripts as the spike session left them, including the
Docker Desktop pool, the first-write test and the GitHub runner probe.

## Kept separately

The commercial plan is in its own archive and belongs in a private
repository.

## What changed since the first version (2026-11-15)

- Spike rewritten for an Apple Silicon Mac with Colima; storage step verified.
- Cloud sources added and moved to the front of 0.3: backups in Azure Blob
  Storage or S3, and Azure SQL Database through BACPAC.
- Image export and import, and image refresh with `--replace`, added to 0.3.
- 0.1 gained `engineEdition`, `branch create --copy` and
  `image build --keep-recovery-model`.
- User experience, use cases and launch plan written down.
- Agent workflow and four agent definitions added.
- Spike findings folded in: images rebuild the log at seal time; the tool
  creates and remounts its own thin pool, so Docker Desktop for macOS gets
  fast clones in 0.1; a copy-on-write hint is set; the fragmentation risk is
  recorded; the Docker Desktop feature left the queue (12 features remain).
