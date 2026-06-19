using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LogiPlusSwitcher.Core.Bolt;

namespace LogiPlusSwitcher.App;

/// <summary>
/// First-run-style dialog that appears when the Free tier sees multiple
/// receivers attached and no primary has been chosen yet. User picks one
/// to designate as the primary; the rest enumerate visibly but don't
/// participate in fan-out until the user upgrades.
/// </summary>
public partial class MultiReceiverPrompt : Window
{
    private readonly ReceiverManager? _manager;
    private readonly ReceiverPolicyService? _policy;
    private readonly ObservableCollection<ReceiverChoice> _choices = new();

    public MultiReceiverPrompt()
    {
        InitializeComponent();
    }

    public MultiReceiverPrompt(ReceiverManager manager, ReceiverPolicyService policy) : this()
    {
        _manager = manager;
        _policy = policy;
        var list = this.FindControl<ListBox>("ReceiverList");
        if (list is not null) list.ItemsSource = _choices;
        Populate();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Populate()
    {
        _choices.Clear();
        if (_manager is null) return;
        foreach (var r in _manager.Receivers.Items.OrderBy(r => r.Info.Path))
        {
            var label = string.IsNullOrEmpty(r.Info.Serial)
                ? $"{r.Info.ProductString} (no serial)"
                : $"{r.Info.ProductString} — {r.Info.Serial}";
            _choices.Add(new ReceiverChoice
            {
                Serial = r.Info.Serial,
                Label = label,
            });
        }
    }

    private void OnSetPrimary(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("ReceiverList");
        if (list?.SelectedItem is ReceiverChoice choice && _policy is not null)
        {
            _policy.SetPrimary(choice.Serial);
            Close();
        }
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    public sealed class ReceiverChoice
    {
        public string Serial { get; set; } = "";
        public string Label { get; set; } = "";

        public override string ToString() => Label;
    }
}
