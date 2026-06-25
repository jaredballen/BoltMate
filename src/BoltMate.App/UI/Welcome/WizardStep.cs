using ReactiveUI;

namespace BoltMate.App.UI;

/// <summary>
/// One row in the Welcome wizard's left-rail step tracker. Bound via
/// <see cref="WelcomeViewModel.Steps"/>; state updates as the user walks
/// the wizard. The handoff defines three visual styles per state — see
/// the data-template branches in WelcomeWindow.axaml.
/// </summary>
public sealed class WizardStep : ReactiveObject
{
    public string Label { get; }

    /// <summary>Stable identifier for diff'ing (matches a page constant when applicable).</summary>
    public string Key { get; }

    private WizardStepState _state;
    public WizardStepState State
    {
        get => _state;
        set
        {
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsActive));
            this.RaisePropertyChanged(nameof(IsDone));
            this.RaisePropertyChanged(nameof(IsUpcoming));
        }
    }

    public bool IsActive   => _state == WizardStepState.Active;
    public bool IsDone     => _state == WizardStepState.Done;
    public bool IsUpcoming => _state == WizardStepState.Upcoming;

    public WizardStep(string key, string label, WizardStepState state = WizardStepState.Upcoming)
    {
        Key = key;
        Label = label;
        _state = state;
    }
}

public enum WizardStepState
{
    Upcoming,
    Active,
    Done,
}
