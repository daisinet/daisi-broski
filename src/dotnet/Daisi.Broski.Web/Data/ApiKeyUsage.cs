namespace Daisi.Broski.Web.Data;

/// <summary>
/// One row per authenticated <c>/api/v1/skim</c> call. Written
/// fire-and-forget after the response is sent so logging delay
/// never sits on the hot path. Partitioned by <c>KeyId</c> so
/// per-key aggregates are cheap queries.
/// </summary>
public sealed class ApiKeyUsage
{
    public string id { get; set; } = "";
    public string Type { get; set; } = nameof(ApiKeyUsage);
    public string KeyId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Host part of the skimmed URL — enough signal
    /// for usage reporting without retaining full URLs (many
    /// of which could be sensitive: internal reports, session
    /// tokens in query strings).</summary>
    public string UrlHost { get; set; } = "";

    /// <summary>HTTP status returned to the caller (200, 502,
    /// 400, etc).</summary>
    public int Status { get; set; }

    public int DurationMs { get; set; }
    public long ResponseBytes { get; set; }
}
