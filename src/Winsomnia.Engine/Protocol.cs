using System.Text.Json;

namespace Winsomnia.Engine;

public sealed record ApiRequest(int Version, string Id, string Command, JsonElement Payload);
public sealed record ApiResponse(int Version, string Id, bool Ok, object? Payload = null, string? Error = null)
{
    public static ApiResponse Success(string id, object? payload = null) => new(2, id, true, payload);
    public static ApiResponse Failure(string id, string error) => new(2, id, false, null, error);
}

public sealed record StartSessionPayload(
    string Kind,
    string Source,
    int DurationSeconds,
    int RelockIntervalSeconds,
    int UnlockGraceSeconds,
    bool Cancelable);

public sealed record CancelSessionPayload(Guid SessionId, string Token);
public sealed record ReportUnlockPayload(Guid SessionId);
public sealed record SpendCreditPayload(int Minutes);
public sealed record ScheduleExceptionPayload(string LocalDate);
public sealed record ActivationPayload(string Confirmation);
