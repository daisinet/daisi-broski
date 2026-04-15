namespace Daisi.Broski.Web.Data;

/// <summary>
/// Personal access token for the Broski skim API. The raw
/// token is shown exactly once at creation; we persist only
/// its SHA-256 hash + a short prefix so the admin UI can
/// identify a key without reconstructing the secret.
///
/// <para>Shape mirrors the daisi-git ApiKey model so the
/// Cosmos persistence + validation code stays drop-in
/// familiar. Token string format: <c>db_{28 base64url chars}</c>.</para>
/// </summary>
public sealed class ApiKey
{
    public string id { get; set; } = "";
    public string Type { get; set; } = nameof(ApiKey);
    public string AccountId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string TokenPrefix { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public bool IsRevoked { get; set; }
}
