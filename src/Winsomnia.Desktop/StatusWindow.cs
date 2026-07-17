using System.Windows;
using System.Windows.Controls;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public sealed class StatusWindow : Window
{
    private readonly TextBlock content = new() { Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap };

    public StatusWindow(EngineClient client)
    {
        Title = "winsomnia";
        Width = 340;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Content = content;
        Loaded += async (_, _) =>
        {
            try
            {
                var status = await client.GetStatusAsync();
                content.Text = $"{status.Phase}\n{Localization.Text("Credit")}: {status.CreditMinutes} min\n" +
                    $"Next / 次回: {status.NextTransitionUtc?.ToLocalTime():g}\n\n{Localization.Text("ReadOnly")}";
            }
            catch
            {
                content.Text = Localization.Text("Unavailable");
            }
        };
        Deactivated += (_, _) => Close();
    }
}
