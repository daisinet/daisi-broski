using System.Security.Cryptography;
using Daisi.Broski.Web.Data;

namespace Daisi.Broski.Web.Services;

/// <summary>
/// Create / validate / list / revoke API keys. Mirrors the
/// daisi-git <c>ApiKeyService</c> contract so the code is
/// predictable for anyone who's worked in that repo.
///
/// <para>Token format: <c>db_{28 base64url chars}</c> — 24
/// random bytes rendered as URL-safe base64, stripped of
/// padding. Raw token is returned exactly once at creation;
/// we store only its SHA-256 hash (hex) plus the first 7
/// chars as a prefix for UI identification.</para>
/// </summary>
public sealed class ApiKeyService(BroskiWebCosmo cosmo)
{
    /// <summary>Create a new API key for the logged-in user.
    /// Returns (record, raw token). The raw token is the only
    /// opportunity the caller has to see the secret — no
    /// recovery later.</summary>
    public async Task<(ApiKey Key, string RawToken)> CreateKeyAsync(
        string accountId, string userId, string userName, string name,
        DateTime? expiresUtc = null)
    {
        var rawToken = GenerateToken();
        var hash = HashToken(rawToken);
        var key = await cosmo.CreateApiKeyAsync(new ApiKey
        {
            AccountId = accountId,
            UserId = userId,
            UserName = userName,
            Name = name,
            TokenHash = hash,
            TokenPrefix = rawToken[..7],
            ExpiresUtc = expiresUtc,
        });
        return (key, rawToken);
    }

    /// <summary>Validate a raw token off the wire. Returns the
    /// underlying <see cref="ApiKey"/> when the token is
    /// well-formed, not revoked, and not expired. Also updates
    /// <c>LastUsedUtc</c> so the admin UI can show when each
    /// key was last exercised.</summary>
    public async Task<ApiKey?> ValidateTokenAsync(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || !rawToken.StartsWith("db_",
            StringComparison.Ordinal))
        {
            return null;
        }
        var hash = HashToken(rawToken);
        var key = await cosmo.GetApiKeyByHashAsync(hash);
        if (key is null || key.IsRevoked) return null;
        if (key.ExpiresUtc.HasValue && key.ExpiresUtc < DateTime.UtcNow) return null;
        key.LastUsedUtc = DateTime.UtcNow;
        // Fire-and-forget-ish: the update is cheap but if it
        // fails we don't want a DB blip to block a valid request.
        try { await cosmo.UpdateApiKeyAsync(key); } catch { }
        return key;
    }

    public Task<List<ApiKey>> ListKeysAsync(string accountId, string userId)
        => cosmo.GetApiKeysAsync(accountId, userId);

    public async Task RevokeKeyAsync(string keyId, string accountId, string userId)
    {
        var keys = await cosmo.GetApiKeysAsync(accountId, userId);
        var key = keys.FirstOrDefault(k => k.id == keyId);
        if (key is not null)
        {
            key.IsRevoked = true;
            await cosmo.UpdateApiKeyAsync(key);
        }
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return "db_" + Convert.ToBase64String(bytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "")[..28];
    }

    private static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
