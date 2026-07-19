using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Winsomnia.Core;

namespace Winsomnia.Desktop;

public sealed class MainWindow : Window
{
    private readonly EngineClient client;
    private readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock operationText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBox start = new() { Width = 120 };
    private readonly TextBox end = new() { Width = 120 };
    private readonly ComboBox strength = new() { Width = 260 };
    private readonly DatePicker exceptionDate = new() { Width = 180 };
    private EngineStatus? status;

    public MainWindow(EngineClient client)
    {
        this.client = client;
        Title = Localization.Text("AppTitle");
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildContent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private UIElement BuildContent()
    {
        var tabs = new TabControl { Margin = new Thickness(20) };
        tabs.Items.Add(Tab(Localization.Text("Home"), Panel(
            Heading(Localization.Text("Status")), statusText, operationText,
            Button(Localization.Text("Refresh"), async () => await RefreshAsync()))));

        strength.Items.Add(Localization.Text("Strict"));
        strength.Items.Add(Localization.Text("Standard"));
        strength.Items.Add(Localization.Text("Flexible"));
        strength.SelectedIndex = 1;

        tabs.Items.Add(Tab(Localization.Text("Schedule"), Panel(
            Heading(Localization.Text("Schedule")),
            Row(Label(Localization.Text("Start")), start),
            Row(Label(Localization.Text("End")), end),
            Heading(Localization.Text("Strength")), strength,
            Button(Localization.Text("Stage"), async () => await StageAsync()))));

        tabs.Items.Add(Tab(Localization.Text("Exception"), Panel(
            Heading(Localization.Text("Exception")),
            new TextBlock { Text = Localization.Text("Reserve"), Margin = new Thickness(0, 0, 0, 12) },
            exceptionDate,
            Button(Localization.Text("Reserve"), async () => await ReserveExceptionAsync()))));

        tabs.Items.Add(Tab(Localization.Text("Diagnostics"), Panel(
            Heading(Localization.Text("Diagnostics")),
            new TextBlock
            {
                Text = "Enable marker / 有効化マーカー: C:\\temp\\winsomnia-lock-enabled.json\n" +
                       "Emergency guide / 緊急手順: docs/EMERGENCY.md\n" +
                       "IPC: named pipe, current user only",
                TextWrapping = TextWrapping.Wrap
            },
            Button("Run safe test / 安全テスト", async () => await RunSafeTestAsync()),
            Button("Activate / 有効化", async () => await ActivateAsync()),
            Button("Pause / 一時停止", async () => await PauseAsync()))));

        return tabs;
    }

    private async Task RefreshAsync()
    {
        try
        {
            status = await client.GetStatusAsync();
            start.Text = status.Settings.StartTime;
            end.Text = status.Settings.EndTime;
            statusText.Text = StatusPresentation.Render(status);
        }
        catch
        {
            statusText.Text = Localization.Text("Unavailable");
        }
    }

    private async Task StageAsync()
    {
        try
        {
            var policy = strength.SelectedIndex switch
            {
                0 => CreditPolicy.Strict,
                2 => CreditPolicy.Flexible,
                _ => CreditPolicy.Standard
            };
            var current = status?.Settings ?? new UserSettings();
            await client.StageSettingsAsync(current with
            {
                StartTime = start.Text,
                EndTime = end.Text,
                Credit = policy
            });
            await RefreshAsync();
            MessageBox.Show(Localization.Text("Stage"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunSafeTestAsync()
    {
        try
        {
            await client.RunSafeTestAsync();
            MessageBox.Show("Safe test passed without locking. / ロックなしの安全テストに成功しました。",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ActivateAsync()
    {
        var answer = MessageBox.Show(
            "This creates the affirmative lock marker and enables real locking with the current schedule. Continue?\n" +
            "有効化マーカーを作成し、現在の予定で実ロックを有効にします。続行しますか？",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await client.ActivateAsync();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PauseAsync()
    {
        try
        {
            status = await client.PauseAsync();
            var display = StatusPresentation.AfterPause(status);
            statusText.Text = display.StatusText;
            operationText.Text = display.ConfirmationText;
        }
        catch (Exception exception)
        {
            operationText.Text = $"{Localization.Text("PauseFailed")}: {exception.Message}";
            MessageBox.Show(operationText.Text, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private async Task ReserveExceptionAsync()
    {
        if (exceptionDate.SelectedDate is null) return;
        try
        {
            await client.ScheduleExceptionAsync(DateOnly.FromDateTime(exceptionDate.SelectedDate.Value));
            MessageBox.Show(Localization.Text("Reserve"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static TabItem Tab(string title, UIElement content) => new() { Header = title, Content = new ScrollViewer { Content = content } };
    private static StackPanel Panel(params UIElement[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
    private static TextBlock Label(string text) => new() { Text = text, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }
    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => await action();
        return button;
    }
}
