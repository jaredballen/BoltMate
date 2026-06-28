using System;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BoltMate.LicenseApi.Functions;

/// <summary>
/// Daily timer-triggered fan-out for trial conversion emails.
/// Runs once per day (14:00 UTC = ~morning across NA + EU), surveys
/// every active Trial license whose <c>ExpiresAt</c> falls in the T-3,
/// T-1, or just-expired window, and emails the user. Per-flag dedup
/// columns on <see cref="BoltMate.LicenseApi.Models.LicenseRecord"/>
/// guarantee one send per stage even if the timer misfires.
/// </summary>
/// <remarks>
/// The send order is deliberate: <c>expired</c> first so users who blew
/// past the trial without acting get the prompt before any future T-x
/// reminder would fire, then T-1, then T-3. A user landing on the last
/// 24h of their trial gets T-1 + expired in close succession by design
/// — the second is the conversion nudge that converts best.
///
/// Failures inside one window do NOT halt the others; we want a Resend
/// outage on one stage to still let the others through. Each record
/// upsert preserves all other fields because the flag mutation is a
/// targeted property write.
/// </remarks>
public sealed class TrialReminderFunction
{
    private readonly ILicenseRepository _licenses;
    private readonly IEmailNotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<TrialReminderFunction> _log;

    public TrialReminderFunction(
        ILicenseRepository licenses,
        IEmailNotifier notifier,
        TimeProvider clock,
        ILogger<TrialReminderFunction> log)
    {
        _licenses = licenses;
        _notifier = notifier;
        _clock = clock;
        _log = log;
    }

    // 0 0 14 * * *  — fires daily at 14:00 UTC.
    [Function("TrialReminderTimer")]
    public Task RunTimer(
        [TimerTrigger("0 0 14 * * *")] TimerInfo timer,
        CancellationToken ct) => RunOnceAsync(ct);

    // Public seam — lets tests drive the work without a real timer.
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        // Half-open day windows. ExpiresAt typically lands at trial-issue
        // wall-clock + 14d so we sweep on calendar day, not exact second.
        await ProcessStageAsync(
            from: today,
            to: today.AddDays(1),
            stage: "expired",
            send: rec => _notifier.TrialExpiredAsync(rec.Email, ct),
            setFlag: rec => rec.TrialNotifiedExpired = true,
            ct).ConfigureAwait(false);

        await ProcessStageAsync(
            from: today.AddDays(1),
            to: today.AddDays(2),
            stage: "t1",
            send: rec => _notifier.TrialEndingAsync(rec.Email, 1, rec.ExpiresAt!.Value, ct),
            setFlag: rec => rec.TrialNotifiedT1 = true,
            ct).ConfigureAwait(false);

        await ProcessStageAsync(
            from: today.AddDays(3),
            to: today.AddDays(4),
            stage: "t3",
            send: rec => _notifier.TrialEndingAsync(rec.Email, 3, rec.ExpiresAt!.Value, ct),
            setFlag: rec => rec.TrialNotifiedT3 = true,
            ct).ConfigureAwait(false);
    }

    private async Task ProcessStageAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string stage,
        Func<BoltMate.LicenseApi.Models.LicenseRecord, Task> send,
        Action<BoltMate.LicenseApi.Models.LicenseRecord> setFlag,
        CancellationToken ct)
    {
        try
        {
            var candidates = await _licenses
                .ListActiveTrialsExpiringBetweenAsync(from, to, stage, ct)
                .ConfigureAwait(false);
            if (candidates.Count == 0) return;

            foreach (var rec in candidates)
            {
                try
                {
                    await send(rec).ConfigureAwait(false);
                    setFlag(rec);
                    await _licenses.UpsertAsync(rec, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One bad row mustn't kill the rest of the batch —
                    // the daily timer will pick it up tomorrow.
                    _log.LogWarning(ex, "Trial reminder {Stage} failed for {LicenseId}.", stage, rec.Id);
                }
            }
            _log.LogInformation("Trial reminder {Stage}: sent {Count}.", stage, candidates.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Trial reminder stage {Stage} blew up.", stage);
        }
    }
}
