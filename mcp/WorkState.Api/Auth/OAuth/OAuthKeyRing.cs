using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// The OAuth server's signing and encryption keys, in the database instead of in certificate files or
/// configuration — which is what keeps the image the same on a laptop and on a server (C32). The first
/// host to start generates each key; every host after loads it, so a token outlives the host that
/// issued it.
/// <para>
/// The private key is stored through the DataProtection ring the dashboard cookie already uses, rather
/// than in a table of its own design: one ring to back up and to rotate, and a dump of
/// <c>oauth_key</c> alone yields nothing.
/// </para>
/// </summary>
public sealed class OAuthKeyRing(NpgsqlDataSource dataSource, IDataProtectionProvider protection)
{
    public const string ProtectorPurpose = "WorkState.OAuth.Keys";

    private const int KeySize = 2048;

    private sealed record Row(string KeyId, string ProtectedKey);

    public RsaSecurityKey Load(string purpose)
    {
        var protector = protection.CreateProtector(ProtectorPurpose, purpose);

        using var connection = dataSource.OpenConnection();
        var row = Read(connection, purpose);

        if (row is null)
        {
            using var generated = RSA.Create(KeySize);

            // Two hosts starting on an empty database both get here; the primary key keeps the first
            // insert and both read back that one.
            connection.Execute(
                """
                INSERT INTO oauth_key (purpose, key_id, protected_key)
                VALUES (@purpose, @keyId, @protectedKey)
                ON CONFLICT (purpose) DO NOTHING;
                """,
                new
                {
                    purpose,
                    keyId = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)),
                    protectedKey = Convert.ToBase64String(protector.Protect(generated.ExportPkcs8PrivateKey())),
                });

            row = Read(connection, purpose)
                  ?? throw new InvalidOperationException($"The {purpose} key was written but cannot be read back.");
        }

        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(protector.Unprotect(Convert.FromBase64String(row.ProtectedKey)), out _);
        return new RsaSecurityKey(rsa) { KeyId = row.KeyId };
    }

    private static Row? Read(NpgsqlConnection connection, string purpose)
        => connection.QuerySingleOrDefault<Row>(
            """SELECT key_id AS "KeyId", protected_key AS "ProtectedKey" FROM oauth_key WHERE purpose = @purpose;""",
            new { purpose });
}
