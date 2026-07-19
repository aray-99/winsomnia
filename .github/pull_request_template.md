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
- [ ] .NET build and Core/IPC tests
- [ ] Repository policy check
- [ ] Release ZIP and SHA-256 inspection
- [ ] Manual verification not run is listed below

<!-- Do not claim that log output alone proves the Windows lock screen was displayed. -->

## GUI release manual safety evidence

<!-- Required only for release/* -> main PRs. Link a CLOSED Issue in this repository with the manual-test label; keep the evidence in that Issue. -->

Completed manual-test Issue: <!-- https://github.com/OWNER/REPOSITORY/issues/123 or #123 -->

- [ ] Notification warning was verified once for each of two restriction transitions.
- [ ] Deleting the enable marker stopped locking, including after restart.
- [ ] Alternate-account recovery was demonstrated or recorded as unavailable/conditional.
- [ ] WinRE/Safe Mode recovery was demonstrated or recorded as unavailable/conditional.
- [ ] The final state was safe: marker absent, Engine disarmed/paused, and task disabled.
