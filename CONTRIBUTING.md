# Contributing to SqlBranch

Thanks for helping. Bug reports, documentation fixes and code are all welcome.

## Contributor License Agreement

Every contributor signs the [SqlBranch Contributor License Agreement](CLA.md)
before a pull request can be merged. On your first pull request a bot
(CLA Assistant) comments with a link; sign there with your GitHub account.
Signing once covers all your later contributions.

You keep the copyright in your contribution. The agreement gives the
maintainer a license to use it, including under terms other than Apache-2.0,
so the project's licensing options stay open. If your employer has rights in
your work, make sure you have their permission first.

## Before you start

- **Bugs:** open an issue with the bug template. Include `sqlbranch doctor`
  output, your platform and the exact command. Never paste a connection string
  or the output of `branch conn`: it contains the engine password.
- **Security issues:** do not open an issue; see [SECURITY.md](SECURITY.md).
- **Features:** open an issue first. Behaviour changes go through an
  [OpenSpec](https://github.com/Fission-AI/OpenSpec) change under
  `openspec/changes/` and are agreed before code is written. The roadmap is in
  [docs/roadmap.md](docs/roadmap.md).

## How this project is built

SqlBranch is developed mostly with AI coding agents, driven and reviewed by
the maintainer. The workflow is in [docs/agent-workflow.md](docs/agent-workflow.md)
and the agent definitions are in `.claude/`. You don't need any of it to
contribute; a normal pull request is fine.

## Build and test

You need the .NET 10 SDK and Docker.

```sh
dotnet build -c Release
dotnet test tests/SqlBranch.Core.Tests -c Release
dotnet test tests/SqlBranch.Testing.Tests -c Release
```

The build treats warnings as errors. Integration tests run against a real
engine in Docker and are skipped unless you opt in:

```sh
SQLBRANCH_IT=1 dotnet test tests/SqlBranch.IntegrationTests -c Release
```

`SQLBRANCH_IT_POOL_BACKEND` selects the pool (`loop-xfs` or `plain`), and
`SQLBRANCH_IT_POOL_FORMAT` selects `1` (default) or `2`; format `2` upgrades each
test pool immediately after initialization and verifies its status before use.
Run both formats with both pool backends. Upgrade-transition tests are skipped
on format `2`, and dedicated legacy pools retain format `1`.
`SQLBRANCH_IT_SQLPACKAGE` points the BACPAC tests at a SqlPackage executable.
A full run on both pools takes one to two hours. CI runs the build and the unit
tests on every pull request. The integration tests on both pools run on
release pull requests into `main`; if your change touches the engine, pools,
images or branches, run them locally and say which pool in the pull request.

## Pull requests

- Target the `dev` branch. `main` holds releases only.
- Keep a pull request to one change, with tests, and update the docs it
  affects.
- Describe what you ran: build, unit tests, and which integration tests on
  which pool.
- Never commit secrets, real connection strings, backups or BACPACs, or data
  from a real database. Test fixtures are synthetic.

By contributing you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
