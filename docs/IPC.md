# Lock-session IPC protocol v2

The engine listens on `winsomnia.engine.v2.<user-sid-hash>` using a named pipe
created with current-user-only access. Each request and response is one UTF-8
JSON object followed by a newline.

## Envelope

Request:

```json
{"version":2,"id":"client-generated-id","command":"status","payload":{}}
```

Success:

```json
{"version":2,"id":"client-generated-id","ok":true,"payload":{}}
```

Failure:

```json
{"version":2,"id":"client-generated-id","ok":false,"error":"message"}
```

Unknown protocol versions and commands fail without changing state.

## Commands

| Command | Purpose |
| --- | --- |
| `status` | Return pause, restriction, credit, pending-change, session state, and `LockAuthorization` (`Disarmed`, `Armed`, or `Faulted` with a reason). |
| `claimWarning` | Atomically claim the current five-minute warning. Returns `WarningClaim(ShouldDisplay, TransitionUtc)`; only the first durable claim for a newer restriction transition can display it. |
| `stageSettings` | Validate replacement settings and stage them for UTC now + 24 hours. |
| `cancelPendingSettings` | Remove a pending replacement; active settings are unchanged. |
| `scheduleException` | Add a local date only when its restriction start is at least 24 hours away. |
| `spendCredit` | Spend 5-minute units up to the current balance and create a bedtime-only override. |
| `startSession` | Start a bounded external session and return its ID and cancellation token. |
| `cancelSession` | Cancel one external session using both its ID and token. |
| `reportUnlock` | Request the session's configured post-unlock grace interval. |
| `reportBedtimeUnlock` | Start the bounded bedtime post-unlock grace screen. |
| `endBedtimeGrace` | End that grace after the user chooses to return to lock. |
| `safeTest` | Validate the current state while requiring the kill switch to remain present. |
| `activate` / `pause` | Explicit setup/recovery operations; never tray actions. |

`startSession` validates duration (1 second to 8 hours), relock interval (1 to
3600 seconds), and grace (0 to 300 seconds). External sessions cannot change
winsomnia settings, consume winsomnia credit, or grant lock authorization.

The Engine derives `claimWarning`'s transition from its own clock and active
policy; clients cannot submit a transition. Claims are at-most-once across
clients and Engine restarts. A failed state save returns an error and never
authorizes a notification. `status` does not claim a warning.

