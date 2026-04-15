using Microsoft.Azure.Cosmos;

namespace Daisi.Broski.Web.Data;

public sealed partial class BroskiWebCosmo
{
    // Container name + partition-key path declared in the main
    // partial class so the GetContainerAsync switch resolves
    // without forward references.

    private static PartitionKey ApiKeyUsagePartitionKey(string keyId) => new(keyId);

    public async Task<ApiKeyUsage> RecordUsageAsync(ApiKeyUsage row)
    {
        if (string.IsNullOrEmpty(row.id)) row.id = GenerateId("usage");
        if (row.TimestampUtc == default) row.TimestampUtc = DateTime.UtcNow;
        var container = await GetContainerAsync(ApiKeyUsageContainerName);
        var response = await container.CreateItemAsync(row,
            ApiKeyUsagePartitionKey(row.KeyId));
        return response.Resource;
    }

    /// <summary>Recent usage rows across every key the given
    /// user owns. Cross-partition query (scoped by UserId) —
    /// fine at usage-page scale (dozens to low-hundreds of
    /// rows per render); if a user ever racks up millions of
    /// calls we'd move to a keyset-paginated query.</summary>
    public async Task<List<ApiKeyUsage>> GetRecentUsageAsync(
        string accountId, string userId, int top = 100)
    {
        var container = await GetContainerAsync(ApiKeyUsageContainerName);
        var query = new QueryDefinition(
            "SELECT TOP @top * FROM c WHERE c.AccountId = @accountId "
            + "AND c.UserId = @userId AND c.Type = 'ApiKeyUsage' "
            + "ORDER BY c.TimestampUtc DESC")
            .WithParameter("@accountId", accountId)
            .WithParameter("@userId", userId)
            .WithParameter("@top", top);
        var results = new List<ApiKeyUsage>();
        using var iterator = container.GetItemQueryIterator<ApiKeyUsage>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    /// <summary>Per-key aggregate (total calls, last call) for
    /// the given user. Populates the rows shown on the dashboard
    /// cards + the usage page summary.</summary>
    public async Task<Dictionary<string, UsageAggregate>> GetUsageAggregatesAsync(
        string accountId, string userId)
    {
        var container = await GetContainerAsync(ApiKeyUsageContainerName);
        var query = new QueryDefinition(
            "SELECT c.KeyId, COUNT(1) AS Calls, MAX(c.TimestampUtc) AS LastCall "
            + "FROM c WHERE c.AccountId = @accountId AND c.UserId = @userId "
            + "AND c.Type = 'ApiKeyUsage' GROUP BY c.KeyId")
            .WithParameter("@accountId", accountId)
            .WithParameter("@userId", userId);
        var results = new Dictionary<string, UsageAggregate>();
        using var iterator = container.GetItemQueryIterator<UsageAggregate>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var row in response)
            {
                if (!string.IsNullOrEmpty(row.KeyId))
                    results[row.KeyId] = row;
            }
        }
        return results;
    }

    public sealed class UsageAggregate
    {
        public string KeyId { get; set; } = "";
        public int Calls { get; set; }
        public DateTime? LastCall { get; set; }
    }
}
