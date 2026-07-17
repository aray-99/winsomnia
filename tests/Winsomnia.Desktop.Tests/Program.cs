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
