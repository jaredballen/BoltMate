using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LogiPlusSwitcher.App.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public bool Confirmed { get; private set; }

    private void OnConfirm(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    public static async Task<bool> AskAsync(
        Window owner,
        string title,
        string header,
        string body,
        string confirmLabel = "OK")
    {
        var dlg = new ConfirmDialog { Title = title };
        var h = dlg.FindControl<TextBlock>("HeaderText");
        var b = dlg.FindControl<TextBlock>("BodyText");
        var btn = dlg.FindControl<Button>("ConfirmButton");
        if (h is not null) h.Text = header;
        if (b is not null) b.Text = body;
        if (btn is not null) btn.Content = confirmLabel;
        await dlg.ShowDialog(owner);
        return dlg.Confirmed;
    }
}
