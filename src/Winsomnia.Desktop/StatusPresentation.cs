using System.Globalization;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public sealed record StatusDisplay(string StatusText, string ConfirmationText, string DiagnosticsText);

public static class StatusPresentation
{
    public static string Render(EngineStatus status, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var lines = new List<string>
        {
            $"{Localization.Text("Status", culture)}: {status.Phase}",
            $"{Localization.Text("Paused", culture)}: {YesNo(status.Paused, culture)}",
            $"{Localization.Text("Armed", culture)}: {YesNo(status.Armed, culture)}",
            $"{Localization.Text("Authorization", culture)}: {status.LockAuthorization.State}",
            $"{Localization.Text("Reason", culture)}: {status.LockAuthorization.Reason}",
            $"{Localization.Text("Credit", culture)}: {status.CreditMinutes} min",
            $"Next / 次回: {status.NextTransitionUtc?.ToLocalTime():g}",
            $"{Localization.Text("Pending", culture)}: {status.PendingSettingsApplyAtUtc?.ToLocalTime():g}"
        };
        if (!string.IsNullOrWhiteSpace(status.Error)) lines.Add($"Error: {status.Error}");
        return string.Join(Environment.NewLine, lines);
    }

    public static StatusDisplay AfterPause(EngineStatus status, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var confirmation = Localization.Text("PauseSucceeded", culture);
        var authorization = $"{Localization.Text("Authorization", culture)}: {status.LockAuthorization.State}";
        var reason = $"{Localization.Text("Reason", culture)}: {status.LockAuthorization.Reason}";
        return new(Render(status, culture), confirmation,
            string.Join(Environment.NewLine, confirmation, authorization, reason));
    }

    private static string YesNo(bool value, CultureInfo culture) =>
        Localization.Text(value ? "Yes" : "No", culture);
}
