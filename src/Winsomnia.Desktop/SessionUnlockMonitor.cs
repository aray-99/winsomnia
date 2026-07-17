using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Winsomnia.Desktop;

public sealed class SessionUnlockMonitor : IDisposable
{
    private const int WmWtsSessionChange = 0x02B1;
    private const int WtsSessionUnlock = 0x8;
    private const int NotifyForThisSession = 0;
    private readonly HwndSource source;
    private bool disposed;

    public event EventHandler? SessionUnlocked;

    public SessionUnlockMonitor()
    {
        var parameters = new HwndSourceParameters("winsomnia-session-events")
        {
            PositionX = -32000,
            PositionY = -32000,
            Width = 1,
            Height = 1,
            WindowStyle = 0
        };
        source = new HwndSource(parameters);
        source.AddHook(WindowProcedure);
        if (!WTSRegisterSessionNotification(source.Handle, NotifyForThisSession))
        {
            var error = Marshal.GetLastWin32Error();
            source.RemoveHook(WindowProcedure);
            source.Dispose();
            throw new Win32Exception(error, "Could not register for Windows session notifications.");
        }
    }

    public static bool IsUnlockMessage(int message, IntPtr eventCode) =>
        message == WmWtsSessionChange && eventCode.ToInt64() == WtsSessionUnlock;

    private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (IsUnlockMessage(message, wParam)) SessionUnlocked?.Invoke(this, EventArgs.Empty);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        WTSUnRegisterSessionNotification(source.Handle);
        source.RemoveHook(WindowProcedure);
        source.Dispose();
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
