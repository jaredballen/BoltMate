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
