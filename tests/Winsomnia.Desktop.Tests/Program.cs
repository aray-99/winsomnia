using System.Globalization;
using System.IO;
using DesktopLocalization = Winsomnia.Desktop.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Winsomnia.Core;
using Winsomnia.Desktop;

foreach (var key in DesktopLocalization.Keys)
{
    var english = DesktopLocalization.Text(key, CultureInfo.GetCultureInfo("en-US"));
    var japanese = DesktopLocalization.Text(key, CultureInfo.GetCultureInfo("ja-JP"));
    Assert(!string.IsNullOrWhiteSpace(english), $"English localization is empty: {key}");
    Assert(!string.IsNullOrWhiteSpace(japanese), $"Japanese localization is empty: {key}");
}

var repository = FindRepositoryRoot();
var desktopProject = File.ReadAllText(Path.Combine(repository, "src", "Winsomnia.Desktop", "Winsomnia.Desktop.csproj"));
Assert(desktopProject.Contains("<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>", StringComparison.Ordinal),
    "Per-monitor V2 DPI awareness is not declared.");

Exception? uiFailure = null;
var uiThread = new Thread(() =>
{
    try
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        var window = new MainWindow(new EngineClient("winsomnia.engine.accessibility.test"));
        Assert(window.MinWidth <= 620 && window.MinHeight <= 460,
            "The settings window minimum size grew unexpectedly.");
        var controls = Descendants((DependencyObject)window.Content).ToList();
        var interactive = controls.OfType<Control>().Where(control =>
            control is Button or TextBox or ComboBox or DatePicker or TabItem).ToList();
        Assert(interactive.Count >= 10, "Expected keyboard controls were not present.");
        foreach (var control in interactive)
        {
            Assert(control.Focusable, $"Control is not keyboard focusable: {control.GetType().Name}");
            Assert(KeyboardNavigation.GetIsTabStop(control) ||
                control is DatePicker && control.ReadLocalValue(KeyboardNavigation.IsTabStopProperty) == DependencyProperty.UnsetValue,
                $"Control is not in tab order: {control.GetType().Name}");
            Assert(control.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue,
                $"Control overrides the system foreground: {control.GetType().Name}");
            Assert(control.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue,
                $"Control overrides the system background: {control.GetType().Name}");
        }
        var tabs = controls.OfType<TabItem>().ToList();
        Assert(tabs.Count == 4 && tabs.All(tab => tab.Content is ScrollViewer),
            "Every settings tab must remain scrollable at large text or scaling.");
    }
    catch (Exception exception) { uiFailure = exception; }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();
if (uiFailure is not null) throw uiFailure;

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

var notification = new CountingWarningNotification();
var claimStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var claimCalls = 0;
var warningClaims = new WarningClaimCoordinator(async cancellationToken =>
{
    Interlocked.Increment(ref claimCalls);
    claimStarted.SetResult();
    await releaseClaim.Task.WaitAsync(cancellationToken);
    return new WarningClaim(true, DateTimeOffset.UtcNow);
}, notification);
var firstPoll = warningClaims.PollAsync();
await claimStarted.Task;
await warningClaims.PollAsync();
releaseClaim.SetResult();
await firstPoll;
Assert(claimCalls == 1 && notification.Displays == 1,
    "Async timer re-entry duplicated a warning claim or notification.");

var rejectedNotification = new CountingWarningNotification();
var rejectedClaim = new WarningClaimCoordinator(
    _ => Task.FromResult(new WarningClaim(false, DateTimeOffset.UtcNow)), rejectedNotification);
await rejectedClaim.PollAsync();
Assert(rejectedNotification.Displays == 0,
    "A rejected Engine claim displayed a notification.");
var pausedStatus = new EngineStatus(
    new UserSettings(), Paused: true, Armed: false,
    new LockAuthorization(LockAuthorizationStates.Disarmed, "Paused by the user."),
    Restricted: false, Phase: "Outside", NextTransitionUtc: null, CreditMinutes: 30,
    PendingSettingsApplyAtUtc: null, OverrideUntilUtc: null, GraceUntilUtc: null,
    ActiveSessions: Array.Empty<LockSession>());
var pauseDisplay = StatusPresentation.AfterPause(pausedStatus, CultureInfo.GetCultureInfo("en-US"));
Assert(pauseDisplay.StatusText.Contains("Paused: Yes", StringComparison.Ordinal),
    "Pause status did not render Paused=true.");
Assert(pauseDisplay.StatusText.Contains("Armed: No", StringComparison.Ordinal),
    "Pause status did not render Armed=false.");
Assert(pauseDisplay.StatusText.Contains("Lock authorization: Disarmed", StringComparison.Ordinal),
    "Pause status did not render the lock authorization state.");
Assert(pauseDisplay.StatusText.Contains("Authorization reason: Paused by the user.", StringComparison.Ordinal),
    "Pause status did not render the lock authorization reason.");
Assert(pauseDisplay.ConfirmationText.Contains("paused", StringComparison.OrdinalIgnoreCase) &&
    pauseDisplay.ConfirmationText.Contains("disarmed", StringComparison.OrdinalIgnoreCase),
    "Pause success confirmation is not explicit.");
var mainWindowSource = File.ReadAllText(Path.Combine(repository, "src", "Winsomnia.Desktop", "MainWindow.cs"));
Assert(mainWindowSource.Contains("StatusPresentation.AfterPause(status)", StringComparison.Ordinal) &&
    mainWindowSource.Contains("operationText.Text = display.ConfirmationText", StringComparison.Ordinal),
    "MainWindow does not render the pause result and explicit confirmation.");
Console.WriteLine("PASS localization, DPI, keyboard, system colors, scrolling, prompt, unlock, and close safety");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static IEnumerable<DependencyObject> Descendants(DependencyObject root)
{
    yield return root;
    foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        foreach (var descendant in Descendants(child)) yield return descendant;
}

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Winsomnia.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
}

sealed class CountingWarningNotification : IWarningNotification
{
    public int Displays { get; private set; }
    public void Show() => Displays++;
}
