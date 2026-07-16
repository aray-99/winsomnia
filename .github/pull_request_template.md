## Summary

<!-- Explain the user-visible outcome and why this change is needed. -->

## Risk

- [ ] This changes locking, scheduling, configuration, pause/resume, setup, or recovery behavior.
- [ ] This is a release-only fix targeting `release/*`.
- [ ] This change contains no developer-specific or personal information.

## Safety checklist

- [ ] winsomnia remained paused while developing.
- [ ] No real lock, task enable/start, kill-switch removal, or reboot was performed without explicit approval.
- [ ] Bounded dry-run and kill-switch behavior remain covered by tests.
- [ ] `docs/EMERGENCY.md` was reviewed and updated if recovery behavior changed.
- [ ] Public files and the release ZIP were checked for private identifiers and local paths.

## Verification

- [ ] PSScriptAnalyzer
- [ ] Pester
- [ ] Repository policy check
- [ ] Release ZIP and SHA-256 inspection
- [ ] Manual verification not run is listed below

<!-- Do not claim that log output alone proves the Windows lock screen was displayed. -->
