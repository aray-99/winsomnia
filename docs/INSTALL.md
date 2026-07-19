# winsomnia installation

## Windows desktop release

Use the `winsomnia-<version>-desktop.zip` asset from the GitHub Release. The desktop package contains the WPF application, engine, and per-user installer.

1. Extract the ZIP to a normal user-writable folder. Keep the extracted `winsomnia-<version>-desktop` folder intact; `Winsomnia.Setup.exe` must stay beside its `app` folder.
2. Run `Winsomnia.Setup.exe` from that extracted folder.
3. Open winsomnia from the Start menu and review the schedule.
4. Run `winsomnia.ps1 test -TestDurationSeconds 60` from the script package, or use the diagnostics flow in the desktop app, before resuming.
5. Resume only after reviewing the schedule and confirming that the emergency kill switch is available.

The installer registers a per-user logon task and leaves winsomnia paused. It does not remove the kill switch or start real locking. Use the normal pause and resume controls after the bounded safety test.

## Verify the installation

The installed files are under `%LOCALAPPDATA%\Programs\winsomnia`. The installed `VERSION` file must match the release version. The task is named `winsomnia` and the default emergency kill switch is:

```text
C:\temp\win-somnia-unlock.txt
```

Check the state before resuming:

```powershell
Get-Content "$env:LOCALAPPDATA\Programs\winsomnia\VERSION"
Get-ScheduledTask -TaskName winsomnia | Select-Object TaskName, State
Test-Path C:\temp\win-somnia-unlock.txt
```

If installation fails, leave the kill switch in place and keep the task disabled. See [EMERGENCY.md](EMERGENCY.md) for recovery steps.

## Updating or uninstalling

Pause winsomnia before replacing an existing installation. Extract the new desktop package to a new folder and run its `Winsomnia.Setup.exe`. The v0.3 installer creates and verifies the legacy kill switch, disables and stops both legacy task names, verifies that no Engine or legacy monitor remains, and migrates only settings into a disarmed v3 state. It stages and validates the complete payload before replacing the installed directory and registers the new task disabled. The v2 state file remains unchanged as a read-only migration source.

If any step fails, setup returns a nonzero exit code after repeating the safety barrier. Do not resume until the legacy kill switch exists, `C:\temp\winsomnia-lock-enabled.json` is absent, Engine state is disarmed, and the `winsomnia` task is disabled.

The installer records replacement phases in a journal beside the installation directory. A later setup run repairs an interrupted replacement before staging another payload: uncommitted replacements restore the previous installation, while a committed replacement keeps the new installation and retries backup cleanup.

To remove winsomnia, run `Winsomnia.Setup.exe uninstall` from a freshly extracted release package outside `%LOCALAPPDATA%\Programs\winsomnia`. The installed copy cannot safely delete its own running executable and therefore refuses self-uninstall. Uninstall applies the same safety barrier before deleting tasks and binaries; the legacy kill switch and user data are retained.
