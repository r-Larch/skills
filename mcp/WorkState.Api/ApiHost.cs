using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using Npgsql;
using WorkState.Api.Auth;
using WorkState.Core.Board;
using WorkState.Core.Data;
using WorkState.Core.Queries;
using WorkState.Mcp;
using WorkState.Mcp.Probe;
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
                options.ReaderToken = builder.Configuration["WorkState:Auth:ReaderToken"];
                options.WriterToken = builder.Configuration["WorkState:Auth:WriterToken"];
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
                .RequireRole(WorkStatePolicies.Controller))
            .AddPolicy(WorkStatePolicies.Reader, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Reader))
            .AddPolicy(WorkStatePolicies.Writer, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Writer));

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
            .WithResourcesFromAssembly(typeof(WorkResources).Assembly)
            .WithPromptsFromAssembly(typeof(ProbePrompts).Assembly)
            .WithCompleteHandler(ProbeCompletions.HandleAsync);

        var app = builder.Build();

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

        builder.Services.AddSingleton(_ => WorkStateDataSource.Create(connectionString));
        builder.Services.AddSingleton(sp => new WorkStateQueries(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new BoardQueries(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new BoardReader(
            sp.GetRequiredService<WorkStateQueries>(),
            sp.GetRequiredService<BoardQueries>()));
    }
}
