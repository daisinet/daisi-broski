using Microsoft.Azure.Cosmos;

namespace Daisi.Broski.Web.Data;

public sealed partial class BroskiWebCosmo
{
    public const string ApiKeysContainerName = "ApiKeys";
    public const string ApiKeysPartitionKeyName = nameof(ApiKey.AccountId);

    // Usage-log container shape is defined here so the
    // container-name → partition-key switch in the base class
    // finds it. Real queries / insert methods live in the
    // partial class that ships with P4.
    public const string ApiKeyUsageContainerName = "ApiKeyUsage";
    public const string ApiKeyUsagePartitionKeyName = "KeyId";

    private static PartitionKey ApiKeyPartitionKey(string accountId) => new(accountId);

    public async Task<ApiKey> CreateApiKeyAsync(ApiKey key)
    {
        if (string.IsNullOrEmpty(key.id)) key.id = GenerateId("key");
        key.CreatedUtc = DateTime.UtcNow;
        var container = await GetContainerAsync(ApiKeysContainerName);
        var response = await container.CreateItemAsync(key,
            ApiKeyPartitionKey(key.AccountId));
        return response.Resource;
    }

    /// <summary>Cross-partition lookup by token hash — used by
    /// the auth middleware on every API request. The caller
    /// doesn't know the account yet (it only has the raw
    /// token from the Authorization header), so we can't scope
    /// to a partition.</summary>
    public async Task<ApiKey?> GetApiKeyByHashAsync(string tokenHash)
    {
        var container = await GetContainerAsync(ApiKeysContainerName);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.TokenHash = @hash AND c.Type = 'ApiKey' AND c.IsRevoked = false")
            .WithParameter("@hash", tokenHash);
        using var iterator = container.GetItemQueryIterator<ApiKey>(query);
        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }
        return null;
    }

    public async Task<List<ApiKey>> GetApiKeysAsync(string accountId, string userId)
    {
        var container = await GetContainerAsync(ApiKeysContainerName);
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.AccountId = @accountId AND c.UserId = @userId "
            + "AND c.Type = 'ApiKey' AND c.IsRevoked = false ORDER BY c.CreatedUtc DESC")
            .WithParameter("@accountId", accountId)
            .WithParameter("@userId", userId);
        var results = new List<ApiKey>();
        using var iterator = container.GetItemQueryIterator<ApiKey>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = ApiKeyPartitionKey(accountId),
            });
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    public async Task<ApiKey> UpdateApiKeyAsync(ApiKey key)
    {
        var container = await GetContainerAsync(ApiKeysContainerName);
        var response = await container.UpsertItemAsync(key,
            ApiKeyPartitionKey(key.AccountId));
        return response.Resource;
    }
}
