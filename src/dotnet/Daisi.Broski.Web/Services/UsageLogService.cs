using Daisi.Broski.Web.Data;

namespace Daisi.Broski.Web.Services;

/// <summary>
/// Thin wrapper around <see cref="BroskiWebCosmo.RecordUsageAsync"/>
/// for the skim endpoint's after-response logging pass. Lives as
/// its own service so tests can substitute a no-op impl and so
/// the logging failure mode (swallow + log warning) is isolated
/// from the request path.
/// </summary>
public sealed class UsageLogService(
    BroskiWebCosmo cosmo, ILogger<UsageLogService> logger)
{
    public async Task RecordAsync(ApiKeyUsage row)
    {
        try
        {
            await cosmo.RecordUsageAsync(row);
        }
        catch (Exception ex)
        {
            // Don't propagate — usage tracking is observability,
            // not correctness. A DB blip shouldn't bubble up as
            // a response error to the caller.
            logger.LogWarning(ex,
                "Failed to record API key usage for keyId={KeyId}", row.KeyId);
        }
    }
}
