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
    private bool promptVisible;

    public TrayController(EngineClient client)
    {
        this.client = client;
        icon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "winsomnia",
            Visible = true
        };
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
            if (status.Phase == "warning")
                icon.ShowBalloonTip(5000, "winsomnia", "Restriction starts in five minutes / 5分後に制限を開始します", Forms.ToolTipIcon.Info);
            if (status.Phase == "restriction-prompt" && !promptVisible)
            {
                promptVisible = true;
                var seconds = RestrictionPromptWindow.SecondsUntil(status.GraceUntilUtc, DateTimeOffset.UtcNow, 30);
                var prompt = new RestrictionPromptWindow(client, seconds);
                prompt.Closed += (_, _) => promptVisible = false;
                prompt.Show();
            }
        }
        catch
        {
            icon.Text = "winsomnia: safely paused / 安全停止";
        }
    }

    public void Dispose()
    {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
    }
}
