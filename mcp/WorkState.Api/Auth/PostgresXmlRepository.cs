using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;
using WorkState.Core.Data;

namespace WorkState.Api.Auth;

/// <summary>
/// The dashboard cookie's key ring, in the one durable thing this service owns.
/// <para>
/// DataProtection's default repository is a directory inside the container. A plain restart keeps
/// it; <c>docker compose up -d --build</c> recreates the container and does not — so every upgrade
/// of a console whose whole claim is that it is always on (doc 08 §4) silently signed the operator
/// out. A named volume is not the fix either: Docker creates one root-owned and the image runs as
/// <c>$APP_UID</c>, which fails the key write outright.
/// </para>
/// <para>
/// <b>The elements are stored as the provider hands them over — plaintext XML — deliberately.</b>
/// There is no DPAPI on Linux, and a certificate would be one more secret to manage and to lose. It
/// would also protect nothing that is not already here: this database holds every project's plan,
/// ledger and evidence, so anyone who can read this table can already read everything the cookie
/// guards, and the API binds to localhost. The encryptor would be a lock on the inside of the vault.
/// </para>
/// </summary>
public sealed class PostgresXmlRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    private readonly WorkStateConnections _connections = new(dataSource);

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        const string sql = """SELECT "xml" FROM data_protection_key ORDER BY id;""";

        using var connection = _connections.OwnerBypass(Bypass.Keys);
        return connection.Query<string>(sql).Select(XElement.Parse).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        const string sql =
            """
            INSERT INTO data_protection_key (friendly_name, "xml")
            VALUES (@friendlyName, @xml);
            """;

        using var connection = _connections.OwnerBypass(Bypass.Keys);
        connection.Execute(sql, new
        {
            friendlyName,
            xml = element.ToString(SaveOptions.DisableFormatting),
        });
    }
}
