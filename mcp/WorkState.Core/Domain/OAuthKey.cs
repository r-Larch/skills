namespace WorkState.Core.Domain;

/// <summary>
/// One of the OAuth server's two RSA keys, generated on first start and loaded on every start after,
/// so a token issued before a restart still validates after it and no image needs a certificate.
/// <para>
/// <see cref="ProtectedKey"/> is the PKCS#8 private key passed through the host's DataProtection ring
/// (<c>data_protection_key</c>): this table alone does not yield a key. Read and written with Dapper by
/// <c>OAuthKeyRing</c>; the entity exists so the migration is generated from the model.
/// </para>
/// </summary>
public class OAuthKey
{
    public const string Signing = "signing";
    public const string Encryption = "encryption";

    /// <summary><see cref="Signing"/> or <see cref="Encryption"/>; one row each.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>The <c>kid</c> tokens carry, stable across restarts.</summary>
    public string KeyId { get; set; } = "";

    public string ProtectedKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
