var engine = @"C:\Program Files\winsomnia\Winsomnia.Engine.exe";
var plan = SetupTaskPlan.Define("winsomnia", engine, @"COMPUTER\USER");

Assert(plan.TaskName == "winsomnia", "Task name changed.");
Assert(plan.EnginePath == engine, "Engine path changed.");
Assert(plan.UserId == @"COMPUTER\USER", "Task principal changed.");
Assert(plan.AllowStartOnBattery && !plan.StopOnBattery,
    "The task must continue on battery power.");
Assert(plan.RestartCount == 3, "Task restart safety policy changed.");

var end = SetupTaskPlan.End("winsomnia");
Assert(!end.Required, "Stopping a missing task must not fail setup.");
Assert(end.Arguments.SequenceEqual(new[] { "/End", "/TN", "winsomnia" }),
    "Task stop arguments are invalid.");

var delete = SetupTaskPlan.Delete("winsomnia");
Assert(delete.Arguments.SequenceEqual(new[] { "/Delete", "/TN", "winsomnia", "/F" }),
    "Uninstall task deletion arguments are invalid.");

AssertThrows(() => SetupTaskPlan.Define("winsomnia", "Winsomnia.Engine.exe", @"COMPUTER\USER"),
    "A relative engine path was accepted.");
AssertThrows(() => SetupTaskPlan.Define("winsomnia", engine, ""),
    "An empty task principal was accepted.");

Console.WriteLine("PASS setup task definition is per-user, battery-safe, and recoverable");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows(Action action, string message)
{
    try { action(); }
    catch { return; }
    throw new InvalidOperationException(message);
}
