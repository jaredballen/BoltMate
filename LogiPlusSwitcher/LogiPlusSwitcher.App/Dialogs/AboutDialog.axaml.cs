using System.Reflection;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LogiPlusSwitcher.App.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var line = this.FindControl<TextBlock>("VersionLine");
        if (line is not null) line.Text = $"Version {GetVersion()}";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0-unknown";

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
