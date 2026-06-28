using System;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Functions;
using BoltMate.LicenseApi.Models;
using BoltMate.Licensing.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BoltMate.Licensing.Tests;

public sealed class TrialReminderFunctionTests
{
    private static readonly DateTimeOffset Today = new(2026, 6, 28, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sends_T3_T1_and_Expired_to_matching_trials_and_dedups()
    {
        var fakes = new Fakes();
        // Trial expiring today → expired email.
        Seed(fakes, "today@example.com", "lic_today", Today.AddHours(2));
        // Trial expiring tomorrow → T-1 email.
        Seed(fakes, "tmrw@example.com", "lic_tmrw", Today.AddDays(1).AddHours(6));
        // Trial expiring in 3 days → T-3 email.
        Seed(fakes, "in3@example.com", "lic_in3", Today.AddDays(3).AddHours(8));
        // Trial expiring in 5 days → nothing.
        Seed(fakes, "in5@example.com", "lic_in5", Today.AddDays(5));

        var fn = Build(fakes);
        await fn.RunOnceAsync(default);

        Assert.Single(fakes.Emails.Expired);
        Assert.Equal("today@example.com", fakes.Emails.Expired[0]);

        Assert.Equal(2, fakes.Emails.TrialEnding.Count);
        var t1 = Assert.Single(fakes.Emails.TrialEnding, e => e.DaysLeft == 1);
        Assert.Equal("tmrw@example.com", t1.To);
        var t3 = Assert.Single(fakes.Emails.TrialEnding, e => e.DaysLeft == 3);
        Assert.Equal("in3@example.com", t3.To);

        // Dedup flags set.
        Assert.True(fakes.Licenses.ByEmail["today@example.com"].TrialNotifiedExpired);
        Assert.True(fakes.Licenses.ByEmail["tmrw@example.com"].TrialNotifiedT1);
        Assert.True(fakes.Licenses.ByEmail["in3@example.com"].TrialNotifiedT3);

        // Second run — no new sends.
        await fn.RunOnceAsync(default);
        Assert.Single(fakes.Emails.Expired);
        Assert.Equal(2, fakes.Emails.TrialEnding.Count);
    }

    [Fact]
    public async Task Skips_non_trial_records()
    {
        var fakes = new Fakes();
        var rec = new LicenseRecord
        {
            Id = "lic_paid",
            Email = "paid@example.com",
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Boltmate, // paid lifetime — no reminder
            Status = "active",
            ExpiresAt = Today.AddHours(3),
        };
        fakes.Licenses.ByEmail["paid@example.com"] = rec;

        var fn = Build(fakes);
        await fn.RunOnceAsync(default);

        Assert.Empty(fakes.Emails.Expired);
        Assert.Empty(fakes.Emails.TrialEnding);
    }

    private static void Seed(Fakes fakes, string email, string id, DateTimeOffset expiresAt)
    {
        fakes.Licenses.ByEmail[email] = new LicenseRecord
        {
            Id = id,
            Email = email,
            Sku = LicenseSkus.Boltmate,
            Tier = LicenseTier.Trial,
            Status = "active",
            IssuedAt = Today.AddDays(-11),
            ExpiresAt = expiresAt,
        };
    }

    private static TrialReminderFunction Build(Fakes fakes)
    {
        var clock = new FakeTimeProvider(Today);
        return new TrialReminderFunction(fakes.Licenses, fakes.Emails, clock, NullLogger<TrialReminderFunction>.Instance);
    }

    private sealed class Fakes
    {
        public FakeLicenseRepository Licenses { get; } = new();
        public FakeEmailNotifier Emails { get; } = new();
    }
}
