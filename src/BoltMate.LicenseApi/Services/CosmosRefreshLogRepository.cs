using System;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace BoltMate.LicenseApi.Services;

internal sealed class CosmosRefreshLogRepository : IRefreshLogRepository
{
    private readonly Container _container;

    public CosmosRefreshLogRepository(CosmosClient cosmos, IOptions<LicenseApiOptions> options)
    {
        var opts = options.Value;
        _container = cosmos.GetContainer(opts.CosmosDatabase, opts.RefreshLogContainer);
    }

    public Task RecordAsync(string licenseId, string oauthSubject, DateTimeOffset at, CancellationToken ct = default)
    {
        var record = new RefreshRecord
        {
            Id = $"{at:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
            PartitionKey = licenseId,
            At = at,
            OAuthSubject = oauthSubject
        };
        return _container.CreateItemAsync(record, new PartitionKey(licenseId), cancellationToken: ct);
    }

    public async Task<int> DeleteByLicenseIdAsync(string licenseId, CancellationToken ct = default)
    {
        // Single-partition wipe. The refresh log can have many entries
        // per license (every entitlement call); we iterate to drain. No
        // rate-limit concern because the GDPR delete path is human-paced.
        var query = new QueryDefinition("SELECT c.id FROM c WHERE c.partitionKey = @pk")
            .WithParameter("@pk", licenseId);

        var ids = new System.Collections.Generic.List<string>();
        var iter = _container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(licenseId) });
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
                await _container.DeleteItemAsync<RefreshRecord>(id, new PartitionKey(licenseId), cancellationToken: ct).ConfigureAwait(false);
                removed++;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
        }
        return removed;
    }

    public async Task<int> CountSinceAsync(string licenseId, DateTimeOffset since, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.partitionKey = @pk AND c.at >= @since")
            .WithParameter("@pk", licenseId)
            .WithParameter("@since", since);

        var iterator = _container.GetItemQueryIterator<int>(query);
        var total = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
            foreach (var n in page) total += n;
        }
        return total;
    }
}
