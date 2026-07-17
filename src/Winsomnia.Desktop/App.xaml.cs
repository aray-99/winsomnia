using Microsoft.Win32;
using System.Windows;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public partial class App : System.Windows.Application
{
    private TrayController? tray;
    private SessionUnlockMonitor? sessionUnlockMonitor;
    private bool usingSystemEventsFallback;
    private readonly EngineClient client = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        tray = new TrayController(client);
        try
        {
            sessionUnlockMonitor = new SessionUnlockMonitor();
            sessionUnlockMonitor.SessionUnlocked += OnSessionUnlocked;
        }
        catch
        {
            usingSystemEventsFallback = true;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            var window = new MainWindow(client);
            window.Show();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) _ = HandleSessionUnlockAsync();
    }

    private void OnSessionUnlocked(object? sender, EventArgs e) => _ = HandleSessionUnlockAsync();

    private async Task HandleSessionUnlockAsync()
    {
        try
        {
            var status = await client.GetStatusAsync();
            if (status.Phase is "restricted" or "restriction-prompt")
            {
                status = await client.ReportBedtimeUnlockAsync();
                var seconds = RestrictionPromptWindow.SecondsUntil(status.GraceUntilUtc, DateTimeOffset.UtcNow, 15);
                tray?.ShowRestrictionPrompt(seconds);
            }
        }
        catch
        {
            // A missing UI or engine must never remove a safety control.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (usingSystemEventsFallback) SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (sessionUnlockMonitor is not null)
        {
            sessionUnlockMonitor.SessionUnlocked -= OnSessionUnlocked;
            sessionUnlockMonitor.Dispose();
        }
        tray?.Dispose();
        base.OnExit(e);
    }
}
