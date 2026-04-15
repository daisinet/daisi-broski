using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Daisi.Broski.Web.Data;

/// <summary>
/// Thin Cosmos DB client wrapper shared by every persistence
/// feature in the Broski web app. Matches the pattern from
/// <c>daisi-git</c>'s <c>DaisiGitCosmo</c> so the operational
/// story (same account, user-secrets connection string, lazy
/// container creation) is identical.
///
/// <para>Database name is <c>daisi-broski-web</c> — distinct
/// from daisi-git's database inside the same Cosmos account so
/// the two apps don't collide on partition keys or container
/// layouts. Containers are created on-demand the first time
/// they're asked for.</para>
/// </summary>
public sealed partial class BroskiWebCosmo
{
    private readonly Lazy<CosmosClient> _client;
    private readonly ConcurrentDictionary<string, Container> _containerCache = new();
    private Database? _database;

    private const string DatabaseName = "daisi-broski-web";

    public BroskiWebCosmo(
        IConfiguration configuration,
        string connectionStringConfigurationName = "Cosmo:ConnectionString")
    {
        _client = new Lazy<CosmosClient>(() =>
        {
            var connectionString = configuration[connectionStringConfigurationName];
            var options = new CosmosClientOptions
            {
                // Preserve raw property names so the model can
                // live alongside the daisi-git ApiKey rows
                // without camel-case mangling.
                UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null,
                },
            };
            return new CosmosClient(connectionString, options);
        });
    }

    public static string GenerateId(string prefix)
        => $"{prefix}-{DateTime.UtcNow:yyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

    public CosmosClient Client => _client.Value;

    public async Task<Database> GetDatabaseAsync()
    {
        if (_database != null) return _database;
        var response = await Client.CreateDatabaseIfNotExistsAsync(DatabaseName);
        _database = response.Database;
        return _database;
    }

    /// <summary>Resolve (or create-then-cache) a container by
    /// name. Partition-key path comes from the container-name
    /// switch below — extend the switch when a new container
    /// lands in a partial class.</summary>
    public async Task<Container> GetContainerAsync(string containerName)
    {
        if (_containerCache.TryGetValue(containerName, out var cached)) return cached;
        string partitionKeyPath = "/" + containerName switch
        {
            ApiKeysContainerName => ApiKeysPartitionKeyName,
            ApiKeyUsageContainerName => ApiKeyUsagePartitionKeyName,
            _ => "id",
        };
        var db = await GetDatabaseAsync();
        var container = await db.CreateContainerIfNotExistsAsync(
            containerName, partitionKeyPath);
        _containerCache.TryAdd(containerName, container);
        return container;
    }
}
