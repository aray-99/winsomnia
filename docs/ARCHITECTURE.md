# winsomnia v0.2 architecture

## Process boundary

`Winsomnia.Engine` is the only process that owns lock-session state or calls the
Windows workstation-lock API. `Winsomnia.Desktop` and future applications are
clients. They communicate through a versioned, newline-delimited JSON protocol
on a current-user-only named pipe. No TCP listener is used.

The engine is intentionally independent of the bedtime product. A future focus
application can start a bounded, cancelable session without depending on the
winsomnia UI, schedule, unlock credits, or persisted settings.

## Safety boundary

- Real locking requires the engine's explicit `--enable-lock` startup switch.
- A filesystem object at the configured kill-switch path pauses every session.
- The engine polls the kill switch at least once per second and checks it again
  immediately before a lock request.
- Invalid configuration, invalid state, IPC failure, or lock-helper failure does
  not remove the kill switch and does not retry an unverified lock operation.
- Session cancellation is scoped by an unguessable cancellation token. One
  client cannot cancel another session by knowing only its public identifier.
- Automated tests inject a fake locker. They never pass `--enable-lock` to a
  process that can reach the Windows lock API.

## Persistent state

Schema version 2 separates user settings from mutable state:

- one daily local-time restriction window;
- a pending settings replacement with an exact UTC application time;
- date-specific exceptions submitted at least 24 hours before use;
- unlock-credit balance and monotonic accrual checkpoint;
- a bounded temporary override;
- persisted external sessions with expiry and cancellation-token hash.

Settings and exceptions never affect the next 24 hours. Credit is the only
normal immediate override. Credit is charged up front in five-minute units and
is not refunded. Safety pause through the kill switch remains unlimited and is
not a credit feature.

Version 1 configuration is imported without deleting the original file. The
migrated engine starts disarmed and preserves the configured kill-switch and log
paths. Installation and migration leave the kill switch present.

## Arbitration

The bedtime restriction and external sessions are evaluated independently. An
active winsomnia credit override suppresses only the bedtime restriction. A
focus-session cancellation ends only the matching external session. If any
remaining source requests locking, the engine continues using the shortest
applicable relock interval.

## User interfaces

The notification-area icon is a read-only instrument. It shows current state,
the next transition, credit balance, pending-change time, and errors. It exposes
no pause, resume, settings, or exit action.

The Start-menu application owns setup and settings. Restriction settings are
staged for 24 hours. Five minutes before restriction it shows an informational
notice. At restriction start it shows a 30-second choice between returning to
the lock screen and spending finite credit. External focus sessions may request
a short, explicitly cancelable post-unlock grace screen.

## Focus application extraction

The focus application remains a separate repository and product. Initially the
engine and protocol live here. Before the focus application consumes them, the
engine, protocol contract, and client conformance tests can move to a dedicated
repository without moving either product UI.

