using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LogiPlusSwitcher.App.Dialogs;

public partial class HostNamesDialog : Window
{
    public HostNamesDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string[]? Result { get; private set; }

    private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string T(string n) => this.FindControl<TextBox>(n)?.Text?.Trim() ?? "";
        Result = [T("Host1Box"), T("Host2Box"), T("Host3Box")];
        Close();
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    public static async Task<string[]?> AskAsync(
        Window owner,
        string title,
        string header,
        string hint,
        string[] initial)
    {
        var dlg = new HostNamesDialog { Title = title };
        var h = dlg.FindControl<TextBlock>("HeaderText");
        var note = dlg.FindControl<TextBlock>("HintText");
        if (h is not null) h.Text = header;
        if (note is not null) note.Text = hint;
        var fields = new[] { "Host1Box", "Host2Box", "Host3Box" };
        for (var i = 0; i < fields.Length; i++)
        {
            var tb = dlg.FindControl<TextBox>(fields[i]);
            if (tb is not null) tb.Text = i < initial.Length ? initial[i] : $"Host {i + 1}";
        }
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }
}
