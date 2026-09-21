using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using Npgsql;
using WorkState.Api.Auth;
using WorkState.Core.Board;
using WorkState.Core.Data;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Mcp;
using WorkState.Mcp.Resources;
using WorkState.Mcp.Tools;

namespace WorkState.Api;

/// <summary>
/// Builds the host. Kept separate from <c>Program.cs</c> so tests can start the real Kestrel
/// pipeline in-process without a test-host shim.
/// </summary>
public static class ApiHost
{
    public const string ConnectionStringName = "WorkState";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.Services
            .AddAuthentication(StaticTokenHandler.SchemeName)
            .AddScheme<StaticTokenOptions, StaticTokenHandler>(StaticTokenHandler.SchemeName, options =>
            {
                options.WorkerToken = builder.Configuration["WorkState:Auth:WorkerToken"];
                options.OrchestratorToken = builder.Configuration["WorkState:Auth:OrchestratorToken"];
                options.ControllerToken = builder.Configuration["WorkState:Auth:ControllerToken"];
            });

        // doc 01 §6. AddAuthorizationFilters() honours [Authorize] on a tool, and a tool the token
        // cannot satisfy is hidden from tools/list rather than refused on call (PROBE Q6) — so
        // these three policies are the entire role mechanism.
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(WorkStatePolicies.Any, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser())
            .AddPolicy(WorkStatePolicies.Worker, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Worker))
            .AddPolicy(WorkStatePolicies.Orchestrator, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Orchestrator))
            .AddPolicy(WorkStatePolicies.Controller, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Controller));

        AddWorkState(builder);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "workstate",
                    Title = "WorkState",
                    Version = "0.3.0",
                };
                options.ServerInstructions = WorkStateServerInstructions.Text;
            })
            .WithHttpTransport()
            .AddAuthorizationFilters()
            .WithToolsFromAssembly(typeof(WorkReadTools).Assembly)
            .WithResourcesFromAssembly(typeof(WorkResources).Assembly);

        var app = builder.Build();

        app.Services.GetService<SchemaOwner>()?.Apply();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        app.MapMcp("/mcp").RequireAuthorization(WorkStatePolicies.Any);

        return app;
    }

    /// <summary>
    /// The store, through U1's shared registration. Hand-rolling a <c>DbContextOptionsBuilder</c>
    /// here would skip the native enum mapping and fail at the first status column read.
    /// </summary>
    private static void AddWorkState(WebApplicationBuilder builder)
    {
        var connectionString =
            builder.Configuration.GetConnectionString(ConnectionStringName)
            ?? builder.Configuration["WorkState:ConnectionString"];

        var slug = builder.Configuration["WorkState:Workspace"];
        builder.Services.AddSingleton(new WorkspaceOptions
        {
            Slug = string.IsNullOrWhiteSpace(slug) ? null : slug,
        });

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        // FINDINGS-live F-L6: until now the only thing that created the schema was the corpus
        // importer, so a first run of WorkState meant pointing an ingest tool at markdown you may
        // not have. The service owns this database outright, so it brings it up itself.
        if (builder.Configuration.GetValue("WorkState:Migrate", true))
        {
            builder.Services.AddSingleton(new SchemaOwner(connectionString));
        }

        builder.Services.AddSingleton(_ => WorkStateDataSource.Create(connectionString));
        builder.Services.AddSingleton(sp => new WorkStateQueries(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new BoardQueries(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new BoardReader(
            sp.GetRequiredService<WorkStateQueries>(),
            sp.GetRequiredService<BoardQueries>()));

        // The write side. CallerIdentity reads the bearer token's roles off the request, which is
        // where the actor and work_verify's run_by come from (doc 01 §6) — never from an argument.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp => new Housekeeping(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new WorkWriter(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<BoardQueries>(),
            sp.GetRequiredService<Housekeeping>()));
        builder.Services.AddSingleton<CallerIdentity>();
    }
}
