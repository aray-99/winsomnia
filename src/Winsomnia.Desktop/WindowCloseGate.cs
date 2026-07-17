using System.Threading;

namespace Winsomnia.Desktop;

public sealed class WindowCloseGate
{
    private const int Idle = 0;
    private const int Queued = 1;
    private const int Closing = 2;
    private int state;

    public bool TryQueueClose() => Interlocked.CompareExchange(ref state, Queued, Idle) == Idle;

    public bool TryBeginClose() => Interlocked.CompareExchange(ref state, Closing, Queued) == Queued;

    public void MarkClosing() => Interlocked.Exchange(ref state, Closing);
}
