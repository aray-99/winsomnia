using Winsomnia.Desktop;

var deactivation = new WindowCloseGate();
Assert(deactivation.TryQueueClose(), "The first deactivation did not queue a close.");
Assert(!deactivation.TryQueueClose(), "A repeated deactivation queued a second close.");
Assert(deactivation.TryBeginClose(), "The queued close could not begin.");
Assert(!deactivation.TryBeginClose(), "A close began twice.");

var userClose = new WindowCloseGate();
Assert(userClose.TryQueueClose(), "The deactivation close was not queued.");
userClose.MarkClosing();
Assert(!userClose.TryBeginClose(), "A queued callback closed an already-closing window.");
Assert(!userClose.TryQueueClose(), "Closing re-entry queued another close.");

var directClose = new WindowCloseGate();
directClose.MarkClosing();
Assert(!directClose.TryQueueClose(), "A direct close allowed deactivation re-entry.");

Console.WriteLine("PASS status window close requests are one-shot and re-entry safe");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
