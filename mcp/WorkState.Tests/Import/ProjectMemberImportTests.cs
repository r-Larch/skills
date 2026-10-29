using Dapper;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Import;

/// <summary>doc 02 §12b: the offline importer has no user, so a project it creates gets every member of the workspace.</summary>
[Collection(StoreCollection.Name)]
public sealed class ProjectMemberImportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ProjectMember_a_project_the_importer_creates_gets_every_tenant_member()
    {
        var slug = $"imp-{Guid.CreateVersion7():n}";
        var (workspace, ann, bob) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO workspace (id, slug) VALUES (@workspace, @slug);
                INSERT INTO app_user (id, name, password_hash) VALUES (@ann, @annName, 'x'), (@bob, @bobName, 'x');
                INSERT INTO tenant_membership (user_id, workspace_id, role) VALUES (@ann, @workspace, 'admin'), (@bob, @workspace, 'member');
                """,
                new { workspace, slug, ann, bob, annName = $"ann-{slug}", bobName = $"bob-{slug}" });
        }

        var run = await Corpus.ImportAsync(fixture, "sample-run", slug);

        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            var members = await connection.QueryAsync<Guid>(
                """
                SELECT pm.user_id FROM project_member pm JOIN item i ON i.id = pm.project_item_id
                WHERE i.workspace_id = @workspace AND i.kind = 'project';
                """,
                new { workspace = run.WorkspaceId });

            Assert.Equal(new[] { ann, bob }.Order(), members.Order());
        }
    }
}
