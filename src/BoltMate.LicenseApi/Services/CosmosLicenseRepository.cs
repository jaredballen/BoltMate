using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace BoltMate.LicenseApi.Services;

internal sealed class CosmosLicenseRepository : ILicenseRepository
{
    private readonly Container _container;

    public CosmosLicenseRepository(CosmosClient cosmos, IOptions<LicenseApiOptions> options)
    {
        var opts = options.Value;
        _container = cosmos.GetContainer(opts.CosmosDatabase, opts.LicensesContainer);
    }

    public async Task<LicenseRecord?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var list = await ListByEmailAsync(email, ct).ConfigureAwait(false);
        return list.Count == 0 ? null : list[0];
    }

    public async Task<IReadOnlyList<LicenseRecord>> ListByEmailAsync(string email, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @pk AND c.status = 'active'")
            .WithParameter("@pk", email.ToLowerInvariant());

        var iterator = _container.GetItemQueryIterator<LicenseRecord>(query);
        var results = new List<LicenseRecord>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            results.AddRange(page);
        }
        return results;
    }

    public Task UpsertAsync(LicenseRecord record, CancellationToken ct = default)
    {
        record.PartitionKey = record.Email.ToLowerInvariant();
        return _container.UpsertItemAsync(record, new PartitionKey(record.PartitionKey), cancellationToken: ct);
    }

    public async Task BindOAuthSubjectAsync(string licenseId, string email, string oauthSubject, CancellationToken ct = default)
    {
        var pk = email.ToLowerInvariant();
        try
        {
            var resp = await _container.ReadItemAsync<LicenseRecord>(licenseId, new PartitionKey(pk), cancellationToken: ct).ConfigureAwait(false);
            var record = resp.Resource;
            if (record.OAuthSubject == oauthSubject) return;
            record.OAuthSubject = oauthSubject;
            await _container.ReplaceItemAsync(record, licenseId, new PartitionKey(pk), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async Task<bool> HasRecentTrialForHardwareAsync(string hardwareIdHash, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        // Cross-partition query — required because trial farming would
        // use different emails (= different partitions). Small index
        // footprint: only Trial records carry hardwareIdHash, and the
        // cutoff prunes anything older than the reuse window.
        var query = new QueryDefinition(
            "SELECT TOP 1 c.id FROM c WHERE c.hardwareIdHash = @h AND c.trialOriginAt >= @cutoff")
            .WithParameter("@h", hardwareIdHash)
            .WithParameter("@cutoff", cutoffUtc);

        var iterator = _container.GetItemQueryIterator<dynamic>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            if (page.Count > 0) return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<LicenseRecord>> ListActiveTrialsExpiringBetweenAsync(
        DateTimeOffset from, DateTimeOffset to, string notifiedFlag, CancellationToken ct = default)
    {
        // Cross-partition; small footprint because the window is narrow
        // (one day's worth of trials, intersected with one of three
        // dedup flags). LicenseTier.Trial is "Trial" in the wire enum.
        var flagProp = notifiedFlag switch
        {
            "t3" => "trialNotifiedT3",
            "t1" => "trialNotifiedT1",
            "expired" => "trialNotifiedExpired",
            _ => throw new ArgumentOutOfRangeException(nameof(notifiedFlag),
                "Expected 't3', 't1', or 'expired'."),
        };
        var query = new QueryDefinition(
            $"SELECT * FROM c WHERE c.tier = 'Trial' AND c.status = 'active' " +
            $"AND c.expiresAt >= @from AND c.expiresAt < @to " +
            $"AND (NOT IS_DEFINED(c.{flagProp}) OR c.{flagProp} = false)")
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        var results = new List<LicenseRecord>();
        var iter = _container.GetItemQueryIterator<LicenseRecord>(query);
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct).ConfigureAwait(false);
            results.AddRange(page);
        }
        return results;
    }

    public async Task<int> DeleteByEmailAsync(string email, CancellationToken ct = default)
    {
        // Single-partition delete loop. Cheap — typical user has 1
        // record under their partition. Idempotent on rerun (404 is
        // swallowed for each id since a concurrent caller may have
        // already removed the row).
        var pk = email.ToLowerInvariant();
        var query = new QueryDefinition("SELECT c.id FROM c WHERE c.partitionKey = @pk")
            .WithParameter("@pk", pk);

        var ids = new List<string>();
        var iter = _container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(pk) });
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct).ConfigureAwait(false);
            foreach (var p in page) ids.Add((string)p.id);
        }

        var removed = 0;
        foreach (var id in ids)
        {
            try
            {
                await _container.DeleteItemAsync<LicenseRecord>(id, new PartitionKey(pk), cancellationToken: ct).ConfigureAwait(false);
                removed++;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
        return removed;
    }

    public async Task<LicenseRecord?> GetByStripePaymentIntentIdAsync(string paymentIntentId, CancellationToken ct = default)
    {
        // Cross-partition lookup keyed on Stripe's PaymentIntent ID.
        // Set on every checkout-completed upsert; refund webhook uses
        // this to find which license to revoke.
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.stripePaymentIntentId = @pi")
            .WithParameter("@pi", paymentIntentId);

        var iterator = _container.GetItemQueryIterator<LicenseRecord>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            foreach (var hit in page) return hit;
        }
        return null;
    }
}
