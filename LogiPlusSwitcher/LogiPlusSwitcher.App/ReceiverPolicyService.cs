using System;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using LogiPlusSwitcher.App.Licensing;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Reconciles license tier + persisted primary-receiver setting + attached
/// receivers and sets <see cref="BoltReceiver.IsParticipating"/> on each.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item><b>Pro</b>: every attached receiver participates.</item>
/// <item><b>Free + 0/1 receivers</b>: the (only) receiver participates.
/// First-run side-effect: if no primary is set, adopt the lone receiver as
/// the primary and persist.</item>
/// <item><b>Free + 2+ receivers, no primary chosen</b>: nothing participates;
/// fires <see cref="MultiReceiverPromptRequired"/> so the UI can ask the
/// user to pick one.</item>
/// <item><b>Free + 2+ receivers, primary chosen</b>: only the primary
/// participates; others enumerate but don't fan out.</item>
/// </list>
/// </remarks>
public sealed class ReceiverPolicyService : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly ILicenseService _license;
    private readonly AppSettings _settings;
    private readonly ILogger<ReceiverPolicyService> _logger;
    private readonly Subject<Unit> _multiReceiverPrompt = new();
    private readonly CompositeDisposable _disposables = new();

    /// <summary>Fires when the UI should prompt the user to pick a primary receiver.</summary>
    public IObservable<Unit> MultiReceiverPromptRequired => _multiReceiverPrompt.AsObservable();

    public ReceiverPolicyService(
        ReceiverManager manager,
        ILicenseService license,
        AppSettings settings,
        ILogger<ReceiverPolicyService> logger)
    {
        _manager = manager;
        _license = license;
        _settings = settings;
        _logger = logger;

        // Recompute participation when receivers attach/detach.
        _disposables.Add(_manager.Receivers.Connect()
            .Subscribe(_ => Reconcile()));

        // Recompute when license tier changes.
        _disposables.Add(_license.IsProChanges.Subscribe(_ => Reconcile()));

        _disposables.Add(_multiReceiverPrompt);
    }

    /// <summary>
    /// Sets a new primary receiver serial, persists to settings, and
    /// re-reconciles. Pass null to clear.
    /// </summary>
    public void SetPrimary(string? serial)
    {
        if (_settings.PrimaryReceiverSerial == serial) return;
        _settings.PrimaryReceiverSerial = serial;
        _settings.Save();
        _logger.LogInformation("Primary receiver set to {Serial}", serial ?? "(cleared)");
        Reconcile();
    }

    /// <summary>Forces a reconcile pass — handy for tests or after settings edits.</summary>
    public void Reconcile()
    {
        var attached = _manager.Receivers.Items.ToList();
        if (attached.Count == 0) return;

        if (_license.IsPro)
        {
            foreach (var receiver in attached)
                receiver.IsParticipating = true;
            return;
        }

        // Free tier branch. Single-receiver case participates unconditionally
        // — don't auto-persist as primary (that would lock in a transient
        // state during enumeration). User explicitly designates primary via
        // SetPrimary when they're prompted (multi-receiver case).
        if (attached.Count == 1)
        {
            attached[0].IsParticipating = true;
            return;
        }

        // Free + multiple receivers.
        var primary = _settings.PrimaryReceiverSerial;
        if (string.IsNullOrEmpty(primary)
            || !attached.Any(r => r.Info.Serial == primary))
        {
            // No primary chosen (or stored primary isn't attached). Park
            // every receiver as non-participating and ask the UI to prompt.
            foreach (var receiver in attached)
                receiver.IsParticipating = false;
            _logger.LogInformation("Free + {Count} receivers with no primary chosen — prompting UI", attached.Count);
            _multiReceiverPrompt.OnNext(Unit.Default);
            return;
        }

        foreach (var receiver in attached)
            receiver.IsParticipating = receiver.Info.Serial == primary;
    }

    public void Dispose() => _disposables.Dispose();
}

/// <summary>Lightweight unit value for void-stream notifications.</summary>
public readonly record struct Unit
{
    public static Unit Default => default;
}
