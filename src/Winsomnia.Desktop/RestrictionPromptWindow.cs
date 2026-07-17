using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using System.Windows.Threading;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public sealed class RestrictionPromptWindow : Window
{
    private readonly EngineClient client;
    private readonly TextBlock countdown = new() { FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ComboBox minutes = new() { Width = 140 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int remaining;

    public static int SecondsUntil(DateTimeOffset? deadlineUtc, DateTimeOffset nowUtc, int fallbackSeconds) =>
        deadlineUtc is null
            ? Math.Clamp(fallbackSeconds, 1, 300)
            : Math.Clamp((int)Math.Ceiling((deadlineUtc.Value - nowUtc).TotalSeconds), 1, 300);
    public RestrictionPromptWindow(EngineClient client, int initialSeconds = 30)
    {
        remaining = Math.Clamp(initialSeconds, 1, 300);
        this.client = client;
        Title = Localization.Text("RestrictionPrompt");
        Width = 520;
        Height = 340;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        for (var value = 5; value <= 60; value += 5) minutes.Items.Add(value);
        minutes.SelectedIndex = 0;
        var lockButton = new Button { Content = Localization.Text("LockNow"), Margin = new Thickness(6), Padding = new Thickness(14, 8, 14, 8) };
        lockButton.Click += async (_, _) =>
        {
            try { await client.EndBedtimeGraceAsync(); }
            finally { Close(); }
        };
        var spendButton = new Button { Content = Localization.Text("Spend"), Margin = new Thickness(6), Padding = new Thickness(14, 8, 14, 8) };
        spendButton.Click += async (_, _) =>
        {
            try
            {
                await client.SpendCreditAsync((int)minutes.SelectedItem);
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(lockButton);
        buttons.Children.Add(minutes);
        buttons.Children.Add(spendButton);
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = Localization.Text("RelockMessage"), TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(0, 0, 0, 24) });
        panel.Children.Add(countdown);
        panel.Children.Add(buttons);
        Content = panel;

        timer.Tick += (_, _) =>
        {
            remaining--;
            countdown.Text = remaining.ToString();
            if (remaining <= 0) Close();
        };
        Loaded += (_, _) => { countdown.Text = remaining.ToString(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }
}
