using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LogiPlusSwitcher.App.Dialogs;

public partial class TextPromptDialog : Window
{
    public TextPromptDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string? Result { get; private set; }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var box = this.FindControl<TextBox>("InputBox");
        Result = box?.Text;
        Close();
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    public static async Task<string?> AskAsync(
        Window owner,
        string title,
        string prompt,
        string hint = "",
        string initial = "")
    {
        var dlg = new TextPromptDialog
        {
            Title = title,
        };
        var p = dlg.FindControl<TextBlock>("PromptText");
        var h = dlg.FindControl<TextBlock>("HintText");
        var b = dlg.FindControl<TextBox>("InputBox");
        if (p is not null) p.Text = prompt;
        if (h is not null) { h.Text = hint; h.IsVisible = !string.IsNullOrEmpty(hint); }
        if (b is not null) b.Text = initial;
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }
}
