using Forms = System.Windows.Forms;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public interface IWarningNotification
{
    void Show();
}

public sealed class WarningClaimCoordinator(
    Func<CancellationToken, Task<WarningClaim>> claimWarning,
    IWarningNotification notification)
{
    private int polling;

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref polling, 1) != 0) return;
        try
        {
            var claim = await claimWarning(cancellationToken);
            if (claim.ShouldDisplay) notification.Show();
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }
}

internal sealed class TrayWarningNotification(Forms.NotifyIcon icon) : IWarningNotification
{
    public void Show() => icon.ShowBalloonTip(
        5000,
        "winsomnia",
        "Restriction starts in five minutes / 5分後に制限を開始します",
        Forms.ToolTipIcon.Info);
}