using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LogiPlusSwitcher.App.Dialogs;

public partial class AlertWindow : Window
{
    public AlertWindow()
    {
        InitializeComponent();
    }

    public AlertWindow(string header, string body) : this()
    {
        var h = this.FindControl<TextBlock>("HeaderText");
        var b = this.FindControl<TextBlock>("BodyText");
        if (h is not null) h.Text = header;
        if (b is not null) b.Text = body;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
