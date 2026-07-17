using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Winsomnia.Core;

namespace Winsomnia.Engine;

public sealed class EngineHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly StateManager stateManager;
    private readonly ISystemClock clock;
    private readonly IWorkstationLocker locker;
    private readonly bool realLockEnabled;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private PersistentState state;
    private DateTimeOffset nextLockUtc = DateTimeOffset.MinValue;
    private string? lastError;

    public EngineHost(StateManager stateManager, ISystemClock clock, IWorkstationLocker locker,
        bool realLockEnabled, string? legacyConfigPath = null)
    {
        this.stateManager = stateManager;
        this.clock = clock;
        this.locker = locker;
        this.realLockEnabled = realLockEnabled;
        state = stateManager.LoadOrCreate(legacyConfigPath);
    }

    public static string GetPipeName()
    {
        var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? Environment.UserName;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant()[..16];
        return $"winsomnia.engine.v1.{digest}";
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        var monitor = MonitorAsync(linked.Token);
        var server = AcceptClientsAsync(linked.Token);
        await Task.WhenAll(monitor, server);
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await stateGate.WaitAsync(cancellationToken);
                try
                {
                    state = stateManager.Normalize(state);
                    var killSwitch = File.Exists(state.KillSwitchPath) || Directory.Exists(state.KillSwitchPath);
                    var scheduleActive = TimeRules.IsRestricted(state.Settings, clock.LocalNow) &&
                        !state.Exceptions.Any(item => item.Date == TimeRules.RestrictionStartDate(state.Settings, clock.LocalNow));
                    if (scheduleActive && state.OverrideUntilUtc is null && state.BedtimeGraceUntilUtc is null)
                        state = state with { BedtimeGraceUntilUtc = clock.UtcNow.AddSeconds(30) };
                    if (!scheduleActive)
                        state = state with { BedtimeGraceUntilUtc = null };
                    var decision = PolicyEvaluator.Evaluate(state, clock.UtcNow, clock.LocalNow, killSwitch);
                    if (decision.ShouldLock && clock.UtcNow >= nextLockUtc)
                    {
                        // Re-check immediately before the only user-visible irreversible action.
                        killSwitch = File.Exists(state.KillSwitchPath) || Directory.Exists(state.KillSwitchPath);
                        if (!killSwitch && realLockEnabled)
                        {
                            locker.Lock();
                            AppendLog("Lock requested by engine.");
                        }
                        nextLockUtc = clock.UtcNow.AddSeconds(decision.RelockIntervalSeconds);
                    }
                    else if (!decision.ShouldLock)
                    {
                        nextLockUtc = DateTimeOffset.MinValue;
                    }
                    stateManager.Save(state);
                    lastError = null;
                }
                finally
                {
                    stateGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Fail without locking and retain the external kill switch state.
                nextLockUtc = DateTimeOffset.MaxValue;
                lastError = exception.Message;
                AppendLog($"Engine monitor paused after error: {exception.Message}", "ERROR");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(GetPipeName(), PipeDirection.InOut, 10,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
                _ = HandleClientAsync(server, cancellationToken);
            }
            catch
            {
                await server.DisposeAsync();
                if (!cancellationToken.IsCancellationRequested) throw;
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            ApiResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<ApiRequest>(line ?? string.Empty, JsonOptions)
                    ?? throw new InvalidDataException("Request was empty.");
                response = request.Version == 1
                    ? await ExecuteAsync(request, cancellationToken)
                    : ApiResponse.Failure(request.Id, "Unsupported protocol version.");
            }
            catch (Exception exception)
            {
                response = ApiResponse.Failure(string.Empty, exception.Message);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    private async Task<ApiResponse> ExecuteAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        await stateGate.WaitAsync(cancellationToken);
        try
        {
            state = stateManager.Normalize(state);
            switch (request.Command)
            {
                case "status":
                    return ApiResponse.Success(request.Id, CreateStatus());
                case "stageSettings":
                    state = stateManager.StageSettings(state,
                        request.Payload.Deserialize<UserSettings>(JsonOptions) ?? throw new InvalidDataException("Settings missing."));
                    break;
                case "cancelPendingSettings":
                    state = state with { PendingSettings = null };
                    break;
                case "scheduleException":
                    var exception = request.Payload.Deserialize<ScheduleExceptionPayload>(JsonOptions)
                        ?? throw new InvalidDataException("Exception date missing.");
                    state = stateManager.ScheduleException(state,
                        DateOnly.ParseExact(exception.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture));
                    break;
                case "spendCredit":
                    var spend = request.Payload.Deserialize<SpendCreditPayload>(JsonOptions)
                        ?? throw new InvalidDataException("Credit amount missing.");
                    state = stateManager.SpendCredit(state, spend.Minutes);
                    break;
                case "startSession":
                    var start = request.Payload.Deserialize<StartSessionPayload>(JsonOptions)
                        ?? throw new InvalidDataException("Session request missing.");
                    var kind = Enum.Parse<SessionKind>(start.Kind, true);
                    var created = LockSession.Create(kind, start.Source, clock.UtcNow,
                        TimeSpan.FromSeconds(start.DurationSeconds), start.RelockIntervalSeconds,
                        start.UnlockGraceSeconds, start.Cancelable);
                    state = state with { Sessions = state.Sessions.Append(created.Session).ToList() };
                    stateManager.Save(state);
                    return ApiResponse.Success(request.Id, new { sessionId = created.Session.Id, cancellationToken = created.Token });
                case "cancelSession":
                    var cancel = request.Payload.Deserialize<CancelSessionPayload>(JsonOptions)
                        ?? throw new InvalidDataException("Cancellation request missing.");
                    var session = state.Sessions.SingleOrDefault(item => item.Id == cancel.SessionId)
                        ?? throw new InvalidDataException("Session not found.");
                    if (!session.Cancelable || !session.AcceptsToken(cancel.Token))
                        throw new UnauthorizedAccessException("Session cancellation was rejected.");
                    state = state with { Sessions = state.Sessions.Where(item => item.Id != session.Id).ToList() };
                    break;
                case "reportUnlock":
                    var unlock = request.Payload.Deserialize<ReportUnlockPayload>(JsonOptions)
                        ?? throw new InvalidDataException("Session identifier missing.");
                    state = state with
                    {
                        Sessions = state.Sessions.Select(item => item.Id == unlock.SessionId
                            ? item with { GraceUntilUtc = clock.UtcNow.AddSeconds(item.UnlockGraceSeconds) }
                            : item).ToList()
                    };
                    break;
                case "reportBedtimeUnlock":
                    state = state with { BedtimeGraceUntilUtc = clock.UtcNow.AddSeconds(15) };
                    break;
                case "endBedtimeGrace":
                    state = state with { BedtimeGraceUntilUtc = clock.UtcNow };
                    break;
                case "safeTest":
                    state.Settings.Validate();
                    if (!File.Exists(state.KillSwitchPath) && !Directory.Exists(state.KillSwitchPath))
                        throw new InvalidOperationException("Safe test requires the kill switch to exist.");
                    break;
                case "activate":
                    var activation = request.Payload.Deserialize<ActivationPayload>(JsonOptions)
                        ?? throw new InvalidDataException("Activation confirmation missing.");
                    if (activation.Confirmation != "ACTIVATE") throw new InvalidDataException("Explicit activation confirmation is required.");
                    if (Directory.Exists(state.KillSwitchPath))
                        throw new InvalidOperationException("A directory kill switch must be removed manually after review.");
                    state = state with { Armed = true };
                    stateManager.Save(state);
                    if (File.Exists(state.KillSwitchPath)) File.Delete(state.KillSwitchPath);
                    break;
                case "pause":
                    EnsureKillSwitch();
                    state = state with { Armed = false };
                    break;
                default:
                    return ApiResponse.Failure(request.Id, "Unknown command.");
            }
            stateManager.Save(state);
            return ApiResponse.Success(request.Id, CreateStatus());
        }
        catch (Exception exception)
        {
            return ApiResponse.Failure(request.Id, exception.Message);
        }
        finally
        {
            stateGate.Release();
        }
    }

    private EngineStatus CreateStatus()
    {
        var paused = File.Exists(state.KillSwitchPath) || Directory.Exists(state.KillSwitchPath) || !state.Armed;
        var decision = PolicyEvaluator.Evaluate(state, clock.UtcNow, clock.LocalNow, paused);
        return new EngineStatus(state.Settings, paused, state.Armed, decision.BedtimeRestricted, decision.Phase,
            decision.NextTransitionUtc, state.Credit.BalanceMinutes, state.PendingSettings?.ApplyAtUtc,
            state.OverrideUntilUtc, state.BedtimeGraceUntilUtc, decision.ActiveSessions, lastError);
    }

    private void EnsureKillSwitch()
    {
        var parent = Path.GetDirectoryName(state.KillSwitchPath)!;
        Directory.CreateDirectory(parent);
        if (!File.Exists(state.KillSwitchPath) && !Directory.Exists(state.KillSwitchPath))
            File.WriteAllText(state.KillSwitchPath, string.Empty);
    }

    private void AppendLog(string message, string level = "INFO")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(state.LogPath)!);
            File.AppendAllText(state.LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging failure must not trigger a lock or remove a safety control.
        }
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        stop.Dispose();
        stateGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
