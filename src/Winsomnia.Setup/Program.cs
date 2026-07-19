using Winsomnia.Setup;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "install";
var paths = SetupPaths.ForCurrentUser(AppContext.BaseDirectory, Environment.ProcessPath);
var platform = new WindowsSetupPlatform();
var coordinator = new SetupCoordinator(platform, paths);

try
{
    if (command == "uninstall")
    {
        coordinator.Uninstall();
        Console.WriteLine("winsomnia was removed. The legacy kill switch and user data were retained.");
        return 0;
    }
    if (command != "install")
        throw new ArgumentException("Supported commands are 'install' and 'uninstall'.");

    coordinator.Install();
    Console.WriteLine("winsomnia was installed for the current user and remains paused.");
    Console.WriteLine("Open winsomnia from the Start menu to complete setup.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"winsomnia setup failed safely: {exception.Message}");
    return 1;
}
