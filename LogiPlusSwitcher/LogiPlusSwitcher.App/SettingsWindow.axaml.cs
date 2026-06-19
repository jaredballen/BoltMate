using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LogiPlusSwitcher.Core.Bolt;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Read-only summary of attached receivers + their paired devices. Phase 2
/// will turn each row into an interactive form (rename, unpair, identify);
/// for now this is the visible surface for "expand to launcher" UX so the
/// menubar app earns its keep when the user explicitly opens settings.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ReceiverManager? _manager;
    private readonly ObservableCollection<ReceiverRow> _rows = new();

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(ReceiverManager manager) : this()
    {
        _manager = manager;
        var list = this.FindControl<ItemsControl>("ReceiverList");
        if (list is not null) list.ItemsSource = _rows;
        Populate();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Populate()
    {
        _rows.Clear();
        var status = this.FindControl<TextBlock>("StatusLine");

        if (_manager is null)
        {
            if (status is not null) status.Text = "No manager wired.";
            return;
        }

        var receivers = _manager.Receivers.Items.ToList();
        if (status is not null)
            status.Text = $"{receivers.Count} receiver{(receivers.Count == 1 ? "" : "s")} attached.";

        foreach (var receiver in receivers)
        {
            var slots = receiver.Devices.Items
                .OrderBy(d => (int)d.DeviceIndex)
                .Select(d => new SlotRow
                {
                    Line = $"slot {d.DeviceIndex}  {d.DisplayName}  ({(d.LinkUp ? "online" : "offline")})"
                         + (d.LastKnownBattery is { } b && b.Percent.HasValue ? $"  · {b.Percent}%" : "")
                         + (d.Serial is not null ? $"  · {d.Serial}" : "")
                })
                .ToList();

            _rows.Add(new ReceiverRow
            {
                Header = receiver.Info.ProductString,
                SubLine = $"{(string.IsNullOrEmpty(receiver.Info.Serial) ? "no serial" : receiver.Info.Serial)} · path: {Shorten(receiver.Info.Path)}",
                Slots = slots,
            });
        }
    }

    private static string Shorten(string path) =>
        path.Length > 60 ? string.Concat("…", path.AsSpan(path.Length - 60)) : path;

    private void OnRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Populate();

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    public sealed class ReceiverRow
    {
        public string Header { get; set; } = "";
        public string SubLine { get; set; } = "";
        public System.Collections.Generic.List<SlotRow> Slots { get; set; } = new();
    }

    public sealed class SlotRow
    {
        public string Line { get; set; } = "";
    }
}
