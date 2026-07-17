using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Winsomnia.Core;

public sealed class EngineClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant()[..16];
            return $"winsomnia.engine.v1.{digest}";
        }
    }

    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<EngineStatus>("status", new { }, cancellationToken);

    public Task<EngineStatus> StageSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("stageSettings", settings, cancellationToken);

    public Task<EngineStatus> ScheduleExceptionAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("scheduleException", new { localDate = date.ToString("yyyy-MM-dd") }, cancellationToken);

    public Task<EngineStatus> SpendCreditAsync(int minutes, CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("spendCredit", new { minutes }, cancellationToken);

    public Task<EngineStatus> PauseAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("pause", new { }, cancellationToken);

    public Task<EngineStatus> ReportBedtimeUnlockAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("reportBedtimeUnlock", new { }, cancellationToken);

    public Task<EngineStatus> EndBedtimeGraceAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("endBedtimeGrace", new { }, cancellationToken);

    public Task<EngineStatus> RunSafeTestAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("safeTest", new { }, cancellationToken);

    public Task<EngineStatus> ActivateAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EngineStatus>("activate", new { confirmation = "ACTIVATE" }, cancellationToken);

    public async Task<T> SendAsync<T>(string command, object payload, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var id = Guid.NewGuid().ToString("N");
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { version = 1, id, command, payload }, JsonOptions));
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("Engine closed the pipe.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(root.GetProperty("error").GetString() ?? "Engine request failed.");
        return root.GetProperty("payload").Deserialize<T>(JsonOptions)
            ?? throw new InvalidDataException("Engine response payload was empty.");
    }
}
