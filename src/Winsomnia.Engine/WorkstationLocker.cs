using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Winsomnia.Engine;

public interface IWorkstationLocker
{
    void Lock();
}

public sealed class WindowsWorkstationLocker : IWorkstationLocker
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public void Lock()
    {
        if (!LockWorkStation()) throw new Win32Exception(Marshal.GetLastWin32Error(), "LockWorkStation failed.");
    }
}

public sealed class NoOpWorkstationLocker : IWorkstationLocker
{
    public int Requests { get; private set; }
    public void Lock() => Requests++;
}
