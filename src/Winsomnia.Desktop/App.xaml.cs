using Microsoft.Win32;
using System.Windows;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public partial class App : System.Windows.Application
{
    private TrayController? tray;
    private readonly EngineClient client = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        tray = new TrayController(client);
        SystemEvents.SessionSwitch += OnSessionSwitch;

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            var window = new MainWindow(client);
            window.Show();
        }
    }

    private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;
        try
        {
            var status = await client.GetStatusAsync();
            if (status.Phase is "restricted" or "restriction-prompt")
            {
                await client.ReportBedtimeUnlockAsync();
                new RestrictionPromptWindow(client).Show();
            }
        }
        catch
        {
            // A missing UI or engine must never remove a safety control.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        tray?.Dispose();
        base.OnExit(e);
    }
}
