# winsomnia v0.3 architecture

## Process boundary

`Winsomnia.Engine` is the only process that owns lock-session state or calls the
Windows workstation-lock API. `Winsomnia.Desktop` and future applications are
clients. They communicate through a versioned, newline-delimited JSON protocol
on a current-user-only named pipe. No TCP listener is used.

The engine is intentionally independent of the bedtime product. A future focus
application can start a bounded, cancelable session without depending on the
winsomnia UI, schedule, unlock credits, or persisted settings.

## Safety boundary

- Real locking requires the engine's explicit `--enable-lock` startup switch,
  schema-v3 `Armed=true` state, and the fixed affirmative marker
  `C:\temp\winsomnia-lock-enabled.json`.
- State and marker contain the same random activation ID. Missing, malformed,
  mismatched, directory, reparse-point, unknown-version, and I/O-failed markers
  deny locking and expose a `Disarmed` or `Faulted` reason through IPC.
- The engine validates the marker at least once per second and again immediately
  before the injected lock helper. Activation commits armed state first and the
  marker last; pause latches denial, durably disarms state, and then revokes the
  marker so deletion failure remains restart-safe. The immediate check is a
  conservative TOCTOU reduction, not a claim that hostile local filesystem races
  are fully eliminated.
- One per-user named mutex permits only one Engine, including offline state
  commands. A FIFO gate serializes client transitions and monitor decisions.
  Monitor checks use a monotonic one-second cadence rather than work plus delay.
- Session cancellation is scoped by an unguessable cancellation token. One
  client cannot cancel another session by knowing only its public identifier.
- Automated tests inject a fake locker. They never pass `--enable-lock` to a
  process that can reach the Windows lock API.

## Persistent state

Schema version 3 separates user settings from mutable state and adds
affirmative lock authorization:

- one daily local-time restriction window;
- a pending settings replacement with an exact UTC application time;
- date-specific exceptions submitted at least 24 hours before use;
- unlock-credit balance and monotonic accrual checkpoint;
- a bounded temporary override;
- persisted external sessions with expiry and cancellation-token hash.

Settings and exceptions never affect the next 24 hours. Credit is the only
normal immediate override. Credit is charged up front in five-minute units and
is not refunded. Safety pause through marker revocation and state disarm remains unlimited and
is not a credit feature.

Version 1 configuration and version 2 state are imported without deleting the
original file. Only user settings migrate; dynamic state and authorization are
reset. Migration creates no marker and starts disarmed.

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

