using Forms = System.Windows.Forms;
using System.Drawing;
using System.Windows.Threading;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public sealed class TrayController : IDisposable
{
    private readonly EngineClient client;
    private readonly Forms.NotifyIcon icon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly PromptDisplayGate promptGate = new();
    private readonly WarningClaimCoordinator warningClaims;

    public TrayController(EngineClient client)
    {
        this.client = client;
        icon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "winsomnia",
            Visible = true
        };
        warningClaims = new WarningClaimCoordinator(client.ClaimWarningAsync, new TrayWarningNotification(icon));
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) new StatusWindow(client).Show();
        };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var status = await client.GetStatusAsync();
            icon.Text = $"winsomnia: {status.Phase}, {status.CreditMinutes} min";
            await warningClaims.PollAsync();
            if (status.Phase == "restriction-prompt")
            {
                var seconds = RestrictionPromptWindow.SecondsUntil(status.GraceUntilUtc, DateTimeOffset.UtcNow, 30);
                ShowRestrictionPrompt(seconds);
            }
        }
        catch
        {
            icon.Text = "winsomnia: safely paused / 安全停止";
        }
    }

    public void ShowRestrictionPrompt(int seconds)
    {
        if (!promptGate.TryOpen()) return;
        var prompt = new RestrictionPromptWindow(client, seconds);
        prompt.Closed += (_, _) => promptGate.MarkClosed();
        prompt.Show();
    }

    public void Dispose()
    {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
    }
}
