# Contributing to winsomnia

winsomnia controls workstation locking, so changes are reviewed as safety-sensitive even when they appear small.

## Branch workflow

| Work | Start from | Branch | PR target |
| --- | --- | --- | --- |
| Feature or normal fix | `develop` | `feature/*`, `fix/*`, `docs/*`, `chore/*`, `refactor/*`, `ci/*`, `test/*` | `develop` |
| Release blocker | affected `release/*` | `fix/*`, `docs/*`, `chore/*`, `ci/*`, `test/*` | affected `release/*` |
| Release | `develop` | `release/*` | `main`, then merge back to `develop` |
| Production hotfix | `main` | `hotfix/*` | `main`, then merge back to `develop` |

Do not commit directly to `main`, `develop`, or `release/*`. Commit messages must follow Conventional Commits, for example `fix: stop before lock when kill switch appears`.

## Safety rules

- Keep winsomnia paused during development.
- Use the Desktop diagnostics safe test and injected fake lockers for ordinary validation.
- Never run an actual lock test, remove the kill switch, enable the scheduled task, or reboot a workstation without explicit approval immediately before that action.
- Any lock-path change needs tests for marker denial at startup and immediately before locking, invalid state/configuration, concurrency, and error handling.
- Review [the emergency recovery guide](docs/EMERGENCY.md) whenever recovery behavior changes.

## Privacy rules

Public files and release assets must not contain real developer names, Windows profile paths, machine names, local workspace paths, credentials, or personal contact details. Use neutral placeholders. Additional private terms can be supplied to CI through the `WINSOMNIA_PRIVATE_IDENTIFIERS` repository secret as newline- or semicolon-separated values.

## Required checks

```powershell
Invoke-ScriptAnalyzer -Path . -Recurse -Severity Warning,Error
Invoke-Pester -Path .\tests -CI -Output Detailed
.\scripts\Test-RepositoryPolicy.ps1
.\build-release.ps1
```

Complete the pull request safety checklist and disclose any check that was not run. A real lock test is a manual release gate, not a CI test.
