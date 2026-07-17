using System.Threading;

namespace Winsomnia.Desktop;

public sealed class PromptDisplayGate
{
    private int visible;

    public bool TryOpen() => Interlocked.CompareExchange(ref visible, 1, 0) == 0;

    public void MarkClosed() => Interlocked.Exchange(ref visible, 0);
}
