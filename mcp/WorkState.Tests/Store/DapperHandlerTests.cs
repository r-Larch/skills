using System.Reflection;
using System.Runtime.Loader;
using WorkState.Core.Writing;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// Dapper's type handlers are process-global, and this process registered them long before any test
/// here runs — which is how a mint that read <c>timestamptz</c> before any query class existed passed
/// or failed by test order. A collectible load context gets its own WorkState.Core, Dapper and Npgsql,
/// so the mint below is the first thing that copy of the code ever does: a restarted server whose first
/// request is <c>POST /api/agent-sessions</c>.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class DapperHandlerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_mint_as_the_first_query_of_a_fresh_load_reads_its_expiry()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var context = new Isolated(AppContext.BaseDirectory);

        try
        {
            var core = context.LoadFromAssemblyName(new AssemblyName("WorkState.Core"));
            Assert.NotSame(typeof(SessionStore).Assembly, core);

            var dataSource = Type(core, "WorkState.Core.Data.WorkStateDataSource")
                .GetMethod("Create")!
                .Invoke(null, [fixture.ConnectionString])!;

            try
            {
                var storeType = Type(core, "WorkState.Core.Writing.SessionStore");
                var store = Activator.CreateInstance(storeType, dataSource)!;

                var requestCtor = Type(core, "WorkState.Core.Writing.MintRequest")
                    .GetConstructors()
                    .MaxBy(c => c.GetParameters().Length)!;
                var args = requestCtor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                args[0] = "aw-first-mint";
                args[1] = Enum.Parse(Type(core, "WorkState.Core.Domain.ActorRole"), "Controller");
                args[2] = 1;
                args[3] = false;

                // As the server's request would: the copy's own TenantScope, entered for the request's tenant.
                Task task;
                using (Type(core, "WorkState.Core.Data.TenantScope").GetMethod("Enter")!.Invoke(null, [world.WorkspaceId]) as IDisposable)
                {
                    task = (Task)storeType.GetMethod("MintAsync")!
                        .Invoke(store, [world.WorkspaceId, requestCtor.Invoke(args), Ct])!;
                    await task;
                }

                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                var minted = result.GetType().GetProperty("Minted")!.GetValue(result);
                Assert.NotNull(minted);

                var expires = (DateTimeOffset)minted.GetType().GetProperty("ExpiresAt")!.GetValue(minted)!;
                Assert.InRange(expires, DateTimeOffset.UtcNow.AddMinutes(50), DateTimeOffset.UtcNow.AddMinutes(70));
            }
            finally
            {
                await ((IAsyncDisposable)dataSource).DisposeAsync();
            }
        }
        finally
        {
            context.Unload();
        }
    }

    private static Type Type(Assembly assembly, string name)
        => assembly.GetType(name, throwOnError: true)!;

    /// <summary>Everything the test's own output folder holds loads afresh; the framework is shared.</summary>
    private sealed class Isolated(string directory) : AssemblyLoadContext("dapper-first-mint", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(directory, $"{name.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
