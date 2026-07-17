using Winsomnia.Desktop;

Assert(SessionUnlockMonitor.IsUnlockMessage(0x02B1, new IntPtr(8)),
    "A Windows session-unlock message was not recognized.");
Assert(!SessionUnlockMonitor.IsUnlockMessage(0x02B1, new IntPtr(7)),
    "A session-lock message was treated as unlock.");
var now = DateTimeOffset.UtcNow;
Assert(RestrictionPromptWindow.SecondsUntil(now.AddSeconds(14.2), now, 30) == 15,
    "The prompt countdown did not match the engine grace deadline.");
Assert(RestrictionPromptWindow.SecondsUntil(null, now, 15) == 15,
    "The prompt countdown fallback changed.");

var promptGate = new PromptDisplayGate();
Assert(promptGate.TryOpen(), "The first prompt request was rejected.");
Assert(!promptGate.TryOpen(), "A concurrent prompt request opened a duplicate.");
promptGate.MarkClosed();
Assert(promptGate.TryOpen(), "A prompt could not reopen after the previous one closed.");
promptGate.MarkClosed();

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

Console.WriteLine("PASS single prompt, native unlock, grace countdown, and close re-entry safety");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
