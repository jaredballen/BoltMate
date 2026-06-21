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
