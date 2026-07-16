# winsomnia repository instructions

These rules apply to every automated agent and human contributor working in this repository.

## Safety invariant

winsomnia can repeatedly lock an interactive Windows session. Treat every change as safety-sensitive.

- Keep the kill switch present while developing unless the user explicitly authorizes its removal for an immediate, controlled test.
- Do not enable or start the scheduled task, invoke real locking, reboot Windows, or remove the kill switch without explicit approval in the current task.
- Automated tests and CI must never pass `-EnableLock` unless the test first proves that a kill switch already exists and the process exits before the lock helper can run.
- Prefer bounded `-DryRun` tests. Every loop must retain a bounded test mode and poll the kill switch at least once per second.
- A change to locking, scheduling, configuration, pause/resume, or recovery behavior requires corresponding safety tests and review of `docs/EMERGENCY.md`.
- On uncertainty or validation failure, leave winsomnia paused and fail without locking.

## Required start-of-work checks

Before changing files:

1. Run `git status --short --branch` and inspect existing changes.
2. Confirm the current branch is appropriate for the work.
3. For runtime or safety work, confirm the kill switch exists and no monitor process is running.
4. Read the affected implementation, tests, README, and emergency instructions before editing.

Never overwrite unrelated user changes. Never use destructive Git commands unless the user explicitly approved the exact history operation.

## Git Flow

- Never develop directly on `main`, `develop`, or `release/*`.
- Start normal work from `develop` using `feature/*`, `fix/*`, `docs/*`, `chore/*`, `refactor/*`, `ci/*`, or `test/*`, then open a PR to `develop`.
- Only release blockers may target `release/*`. Use a dedicated `fix/*`, `docs/*`, `chore/*`, `ci/*`, or `test/*` branch created from that release branch.
- Only `release/*` and `hotfix/*` merge into `main`.
- Merge every completed `release/*` or `hotfix/*` back into `develop` after the main merge.
- Use Conventional Commits. Keep each commit focused and explain the intent, not just the changed file.
- Do not tag a commit unless it is contained in `main` and its tag matches `VERSION` and `CHANGELOG.md`.

## Privacy and public documentation

- Never put a real Windows user name, computer name, profile directory, local workspace path, email address, token, or other developer-specific identifier in tracked files, examples, fixtures, logs, screenshots, commit messages, or release assets.
- Use placeholders such as `USER`, `<repository-folder>`, and environment variables such as `%LOCALAPPDATA%`.
- Do not encode private identifiers directly in repository tests. Supply additional blocked values through the `WINSOMNIA_PRIVATE_IDENTIFIERS` CI secret.
- Before commit and release, run the repository policy check and inspect the generated ZIP.

## Verification and handoff

Run these checks before requesting review:

```powershell
Invoke-ScriptAnalyzer -Path . -Recurse -Severity Warning,Error
Invoke-Pester -Path .\tests -CI -Output Detailed
.\scripts\Test-RepositoryPolicy.ps1
.\build-release.ps1
```

Report the branch, commits, tests, runtime pause state, and any verification that still requires a real Windows session. Do not describe a logged lock request as proof that the lock screen was visibly displayed.
