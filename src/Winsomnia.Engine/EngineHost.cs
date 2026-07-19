using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    private readonly ILockMarkerStore markerStore;
    private readonly bool realLockEnabled;
    private readonly string pipeName;
    private readonly string mutexName;
    private readonly Channel<byte> stateGate = CreateStateGate();
    private readonly CancellationTokenSource stop = new();
    private PersistentState state;
    private DateTimeOffset nextLockUtc = DateTimeOffset.MinValue;
    private string? lastError;
    private bool authorizationDenialLatched;

    public EngineHost(StateManager stateManager, ISystemClock clock, IWorkstationLocker locker,
        bool realLockEnabled, string? legacyStatePath = null, string? legacyConfigPath = null,
        string? pipeName = null, ILockMarkerStore? markerStore = null, string? mutexName = null)
    {
        this.stateManager = stateManager;
        this.clock = clock;
        this.locker = locker;
        this.realLockEnabled = realLockEnabled;
        this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? GetPipeName() : pipeName;
        this.markerStore = markerStore ?? new LockMarkerStore();
        this.mutexName = string.IsNullOrWhiteSpace(mutexName) ? GetMutexName() : mutexName;
        state = stateManager.LoadOrCreate(legacyStatePath, legacyConfigPath);
    }

    private static string UserDigest()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? Environment.UserName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant()[..16];
    }

    public static string GetPipeName() => $"winsomnia.engine.v2.{UserDigest()}";
    public static string GetMutexName() => $"Local\\winsomnia.engine.v2.{UserDigest()}";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lease = new EngineInstanceLease(mutexName);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await Task.WhenAll(MonitorAsync(linked.Token), AcceptClientsAsync(linked.Token));
    }

    private static Channel<byte> CreateStateGate()
    {
        var channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
        if (!channel.Writer.TryWrite(0)) throw new InvalidOperationException("State gate initialization failed.");
        return channel;
    }

    private ValueTask<byte> EnterStateGateAsync(CancellationToken cancellationToken) =>
        stateGate.Reader.ReadAsync(cancellationToken);

    private void ExitStateGate()
    {
        if (!stateGate.Writer.TryWrite(0)) throw new InvalidOperationException("State gate release failed.");
    }
    private LockAuthorization InspectAuthorization() => authorizationDenialLatched
        ? new(LockAuthorizationStates.Faulted, "runtime-denial-latched")
        : markerStore.Inspect(state, realLockEnabled);

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var nextCheck = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnterStateGateAsync(cancellationToken);
                try
                {
                    state = stateManager.Normalize(state);
                    var authorization = InspectAuthorization();
                    var scheduleActive = TimeRules.IsRestricted(state.Settings, clock.LocalNow) &&
                        !state.Exceptions.Any(item => item.Date == TimeRules.RestrictionStartDate(state.Settings, clock.LocalNow));
                    if (scheduleActive && state.OverrideUntilUtc is null && state.BedtimeGraceUntilUtc is null)
                        state = state with { BedtimeGraceUntilUtc = clock.UtcNow.AddSeconds(30) };
                    if (!scheduleActive) state = state with { BedtimeGraceUntilUtc = null };
                    var authorized = authorization.State == LockAuthorizationStates.Armed;
                    var decision = PolicyEvaluator.Evaluate(state, clock.UtcNow, clock.LocalNow, authorized);
                    if (decision.ShouldLock && clock.UtcNow >= nextLockUtc)
                    {
                        var immediate = InspectAuthorization();
                        if (immediate.State == LockAuthorizationStates.Armed)
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
                    lastError = authorization.State == LockAuthorizationStates.Faulted ? authorization.Reason : null;
                }
                finally { ExitStateGate(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                authorizationDenialLatched = true;
                nextLockUtc = DateTimeOffset.MaxValue;
                lastError = exception.Message;
                AppendLog($"Engine monitor paused after error: {exception.Message}", "ERROR");
            }
            nextCheck += Stopwatch.Frequency;
            var remainingTicks = nextCheck - Stopwatch.GetTimestamp();
            if (remainingTicks > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(
                    (double)remainingTicks / Stopwatch.Frequency), cancellationToken);
            }
            else
            {
                nextCheck = Stopwatch.GetTimestamp();
            }
        }
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 10,
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
                response = request.Version == 2
                    ? await ExecuteAsync(request, cancellationToken)
                    : ApiResponse.Failure(request.Id, "Unsupported protocol version.");
            }
            catch (Exception exception) { response = ApiResponse.Failure(string.Empty, exception.Message); }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    private async Task<ApiResponse> ExecuteAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        await EnterStateGateAsync(cancellationToken);
        try
        {
            state = stateManager.Normalize(state);
            switch (request.Command)
            {
                case "status": return ApiResponse.Success(request.Id, CreateStatus());
                case "stageSettings":
                    state = stateManager.StageSettings(state, request.Payload.Deserialize<UserSettings>(JsonOptions)
                        ?? throw new InvalidDataException("Settings missing."));
                    break;
                case "cancelPendingSettings": state = state with { PendingSettings = null }; break;
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
                    var created = LockSession.Create(Enum.Parse<SessionKind>(start.Kind, true), start.Source, clock.UtcNow,
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
                    state = state with { Sessions = state.Sessions.Select(item => item.Id == unlock.SessionId
                        ? item with { GraceUntilUtc = clock.UtcNow.AddSeconds(item.UnlockGraceSeconds) } : item).ToList() };
                    break;
                case "reportBedtimeUnlock": state = state with { BedtimeGraceUntilUtc = clock.UtcNow.AddSeconds(15) }; break;
                case "endBedtimeGrace": state = state with { BedtimeGraceUntilUtc = clock.UtcNow }; break;
                case "safeTest":
                    state.Settings.Validate();
                    if (InspectAuthorization().State == LockAuthorizationStates.Armed)
                        throw new InvalidOperationException("Safe test requires locking to be disarmed.");
                    break;
                case "activate":
                    Activate(request);
                    return ApiResponse.Success(request.Id, CreateStatus());
                case "pause":
                    Pause();
                    return ApiResponse.Success(request.Id, CreateStatus());
                default: return ApiResponse.Failure(request.Id, "Unknown command.");
            }
            stateManager.Save(state);
            return ApiResponse.Success(request.Id, CreateStatus());
        }
        catch (Exception exception) { return ApiResponse.Failure(request.Id, exception.Message); }
        finally { ExitStateGate(); }
    }

    private void Activate(ApiRequest request)
    {
        var activation = request.Payload.Deserialize<ActivationPayload>(JsonOptions)
            ?? throw new InvalidDataException("Activation confirmation missing.");
        if (activation.Confirmation != "ACTIVATE")
            throw new InvalidDataException("Explicit activation confirmation is required.");
        var activationId = Guid.NewGuid().ToString("N");
        try
        {
            state = state with { Armed = true, ActivationId = activationId };
            stateManager.Save(state);
            markerStore.Commit(activationId);
            authorizationDenialLatched = false;
            lastError = null;
        }
        catch
        {
            authorizationDenialLatched = true;
            try { markerStore.Revoke(); } catch { }
            try
            {
                state = state with { Armed = false, ActivationId = null };
                stateManager.Save(state);
            }
            catch { }
            throw;
        }
    }

    private void Pause()
    {
        authorizationDenialLatched = true;
        state = state with { Armed = false, ActivationId = null };
        try
        {
            stateManager.Save(state);
        }
        catch
        {
            try { markerStore.Revoke(); } catch { }
            throw;
        }
        markerStore.Revoke();
        authorizationDenialLatched = false;
        lastError = null;
    }

    private EngineStatus CreateStatus()
    {
        var authorization = InspectAuthorization();
        var decision = PolicyEvaluator.Evaluate(state, clock.UtcNow, clock.LocalNow,
            authorization.State == LockAuthorizationStates.Armed);
        var error = authorization.State == LockAuthorizationStates.Faulted ? authorization.Reason : lastError;
        return new EngineStatus(state.Settings, authorization.State != LockAuthorizationStates.Armed, state.Armed,
            authorization, decision.BedtimeRestricted, decision.Phase, decision.NextTransitionUtc,
            state.Credit.BalanceMinutes, state.PendingSettings?.ApplyAtUtc, state.OverrideUntilUtc,
            state.BedtimeGraceUntilUtc, decision.ActiveSessions, error);
    }

    private void AppendLog(string message, string level = "INFO")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(state.LogPath)!);
            File.AppendAllText(state.LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
