using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;

namespace WorkState.Core.Data;

/// <summary>
/// Why a caller opens a connection as the owner, which row-level security does not confine (doc 02 §15). Each is
/// a path that has no tenant yet, or acts across tenants by design; the list is closed on purpose.
/// </summary>
public enum Bypass
{
    /// <summary>A <c>wss_</c> token is being turned into its session and workspace: there is no tenant yet.</summary>
    Authentication,

    /// <summary>The static tokens' configured workspace, looked up or created on first use (C62).</summary>
    WorkspaceBinding,

    /// <summary>Users and their memberships span workspaces: sign-in, the cookie's stamp, OAuth, the switcher.</summary>
    Users,

    /// <summary>The admin pages act on the workspace their path names, after reading the caller's rights (C62).</summary>
    Administration,

    /// <summary>Invite and reset links are opened by token before anyone signs in; a reset link has no workspace.</summary>
    Links,

    /// <summary>DataProtection and OAuth signing keys belong to the server, not to a workspace.</summary>
    Keys,

    /// <summary>The one LISTEN connection fans every workspace's journal out to that workspace's subscribers.</summary>
    Journal,
}

/// <summary>
/// Why a tenant transaction sees every project of its tenant, which <c>project_access</c> otherwise keeps to the
/// caller's (doc 02 §15b). Still the tenant role in the tenant's workspace; the list is closed on purpose.
/// </summary>
public enum ProjectBypass
{
    /// <summary>
    /// A project's member list: a workspace admin manages it without being a member. The writer reads the project's
    /// ref and its members, never its work (<c>WorkWriter.ProjectMembersAsync</c>).
    /// </summary>
    Membership,
}

/// <summary>
/// The only way the server opens a connection (<c>RlsSourceGuardTests</c>). <see cref="TenantAsync"/> is the
/// request path's: a transaction that acts as <see cref="AppRole"/> in <see cref="TenantScope"/>'s workspace, so
/// row-level security returns that workspace's rows and no other's even to a query that forgets its filter.
/// <see cref="OwnerBypassAsync"/> is the owner's, and every caller says why (<see cref="Bypass"/>).
/// </summary>
public sealed class WorkStateConnections(NpgsqlDataSource dataSource)
{
    /// <summary>The NOLOGIN role the TenantRls migration creates; its policies read <see cref="WorkspaceSetting"/>.</summary>
    public const string AppRole = "workstate_app";

    public const string WorkspaceSetting = "app.workspace_id";

    /// <summary>The scope's user, which the ProjectAccess policies read; <c>''</c> for none: every project.</summary>
    public const string UserSetting = "app.user_id";

    /// <summary>The roots the scope's user could see when the transaction began, so a policy need not ask per statement.</summary>
    public const string ProjectsSetting = "app.projects";

    private static readonly ConditionalWeakTable<NpgsqlDataSource, object> OwnerWhenUnscoped = [];

    /// <summary>
    /// A transaction in the current tenant. Disposing it rolls back what was not committed, and ends the role and
    /// the workspace with it: both are transaction-local, so the pooled connection goes back as the owner.
    /// </summary>
    public Task<OpenTransaction> TenantAsync(CancellationToken cancellationToken = default)
        => OpenAsync(BeginTenantAsync, cancellationToken);

    /// <summary>A transaction in the current tenant that sees every project of it, for the reason given.</summary>
    public Task<OpenTransaction> TenantEveryProjectAsync(ProjectBypass why, CancellationToken cancellationToken = default)
        => OpenAsync((connection, token) => BeginAsync(connection, everyProject: true, token), cancellationToken);

    /// <summary>
    /// The next tenant transaction on a connection <see cref="TenantAsync"/> opened, once its first has ended — a
    /// plain <c>BeginTransaction</c> there would run as the owner.
    /// </summary>
    public Task<NpgsqlTransaction> BeginTenantAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
        => BeginAsync(connection, everyProject: false, cancellationToken);

    private async Task<NpgsqlTransaction> BeginAsync(
        NpgsqlConnection connection,
        bool everyProject,
        CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!TenantScope.TryGet(out var workspaceId, out var userId) && OwnerWhenUnscoped.TryGetValue(dataSource, out _))
        {
            return transaction;
        }

        // With a user, the roots they may see are read once here, in the same round trip, by the owner's function.
        var user = everyProject ? null : userId;
        var projects = user is null ? "''" : $"{Migrations.ProjectAccess.VisibleFunction}(@workspaceId, @userId)";
        await using var command = new NpgsqlCommand(
            $"SELECT set_config('role', '{AppRole}', true), set_config('{WorkspaceSetting}', @workspace, true), "
            + $"set_config('{UserSetting}', @user, true), set_config('{ProjectsSetting}', {projects}, true);",
            connection,
            transaction);
        command.Parameters.AddWithValue("workspace", workspaceId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("user", user?.ToString() ?? string.Empty);
        if (user is not null)
        {
            command.Parameters.Add(new NpgsqlParameter("workspaceId", NpgsqlDbType.Uuid) { Value = (object?)workspaceId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("userId", NpgsqlDbType.Uuid) { Value = user.Value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);

        return transaction;
    }

    /// <summary>A connection as the owner, which row-level security does not confine.</summary>
    public ValueTask<NpgsqlConnection> OwnerBypassAsync(Bypass why, CancellationToken cancellationToken = default)
        => dataSource.OpenConnectionAsync(cancellationToken);

    /// <inheritdoc cref="OwnerBypassAsync"/>
    public NpgsqlConnection OwnerBypass(Bypass why) => dataSource.OpenConnection();

    /// <summary>A transaction as the owner, for a bypass that writes more than one statement.</summary>
    public Task<OpenTransaction> OwnerBypassTransactionAsync(Bypass why, CancellationToken cancellationToken = default)
        => OpenAsync(async (connection, token) => await connection.BeginTransactionAsync(token), cancellationToken);

    private async Task<OpenTransaction> OpenAsync(
        Func<NpgsqlConnection, CancellationToken, Task<NpgsqlTransaction>> begin,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            return new OpenTransaction(connection, await begin(connection, cancellationToken));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Callers on <paramref name="dataSource"/> that never enter a <see cref="TenantScope"/> act as the owner. For
    /// the test suite's direct store calls only; nothing in the server calls it (<c>RlsSourceGuardTests</c>), so
    /// there a tenant connection outside a scope sees no tenant's rows.
    /// </summary>
    public static void RunUnscopedAsOwner(NpgsqlDataSource dataSource) => OwnerWhenUnscoped.AddOrUpdate(dataSource, new object());
}

/// <summary>A transaction and the pooled connection it holds, released together.</summary>
public sealed class OpenTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
{
    public NpgsqlConnection Connection => connection;

    public NpgsqlTransaction Transaction => transaction;

    public void Deconstruct(out NpgsqlConnection open, out NpgsqlTransaction begun) => (open, begun) = (connection, transaction);

    public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
