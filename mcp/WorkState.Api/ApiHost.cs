using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using ModelContextProtocol.Protocol;
using Npgsql;
using WorkState.Api.App;
using WorkState.Api.App.V1;
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

    /// <summary>
    /// The DataProtection application discriminator, fixed rather than derived. Left to itself the
    /// provider takes it from the content root, so the same key ring read from the container
    /// (<c>/app</c>) and from a published build on the host would yield two different purpose
    /// chains — a ring in the database that neither party could use.
    /// </summary>
    public const string DataProtectionApplicationName = "workstate";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        // doc 08 §4 — one process, two audiences. The dashboard's credentials are read once here so
        // the cookie handler and POST /api/session judge the same values.
        var dashboard = new DashboardOptions();
        builder.Configuration.GetSection(DashboardOptions.Section).Bind(dashboard);
        builder.Services.AddSingleton(dashboard);

        builder.Services
            .AddAuthentication(StaticTokenHandler.SchemeName)
            .AddScheme<StaticTokenOptions, StaticTokenHandler>(StaticTokenHandler.SchemeName, options =>
            {
                options.WorkerToken = builder.Configuration["WorkState:Auth:WorkerToken"];
                options.OrchestratorToken = builder.Configuration["WorkState:Auth:OrchestratorToken"];
                options.ControllerToken = builder.Configuration["WorkState:Auth:ControllerToken"];
                options.ReaderToken = builder.Configuration["WorkState:Auth:ReaderToken"];
            })
            .AddCookie(DashboardAuth.SchemeName, options => DashboardAuth.Configure(options, dashboard));

        // doc 01 §6. AddAuthorizationFilters() honours [Authorize] on a tool, and a tool the token
        // cannot satisfy is hidden from tools/list rather than refused on call (PROBE Q6) — so
        // these three policies are the entire role mechanism.
        builder.Services.AddAuthorizationBuilder()
            // Every agent token carries the worker role; a reader token carries none of the three.
            .AddPolicy(WorkStatePolicies.Any, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Worker))
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
            .AddPolicy(WorkStatePolicies.Importer, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Orchestrator)
                .RequireClaim(CallerSession.GrantClaim, Core.Domain.SessionGrants.Import))
            .AddPolicy(WorkStatePolicies.SelfNamer, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Worker)
                .RequireClaim(CallerSession.ClientKeyClaim))

            // The fifth policy names the cookie scheme and nothing else, exactly as the four above
            // name the bearer scheme and nothing else. That is what "two schemes that never cross"
            // is made of: neither handler is ever asked to judge the other's credential.
            .AddPolicy(DashboardAuth.Policy, policy => policy
                .AddAuthenticationSchemes(DashboardAuth.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(DashboardAuth.Role))

            // doc 10 rule 5: the read API is the one surface both audiences share, so it is the one
            // policy that names both schemes. It still grants nothing either scheme lacks on its own.
            .AddPolicy(ApiV1.Policy, policy => policy
                .AddAuthenticationSchemes(DashboardAuth.SchemeName, StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(DashboardAuth.Role, WorkStatePolicies.Worker, WorkStatePolicies.Reader));

        AddWorkState(builder);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "workstate",
                    Title = "WorkState",
                    Version = "0.6.0",
                };
                options.ServerInstructions = WorkStateServerInstructions.Text;
            })
            .WithHttpTransport()
            .AddAuthorizationFilters()

            // After the authorization filters, so a call a token may not make is still refused as
            // forbidden and never answered with the shape of that tool's arguments (doc 01 §3.6).
            // The session after the arguments, so a call refused for its shape renews nothing.
            .WithRequestFilters(filters => filters
                .AddCallToolFilter(ToolArguments.Filter)
                .AddCallToolFilter(CallerSession.Filter)
                .AddReadResourceFilter(CallerSession.ResourceFilter))
            .WithToolsFromAssembly(typeof(WorkReadTools).Assembly)
            .WithResourcesFromAssembly(typeof(WorkResources).Assembly);

        var app = builder.Build();

        app.Services.GetService<SchemaOwner>()?.Apply();

        app.UseApiVersionHeader();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        app.MapMcp("/mcp").RequireAuthorization(WorkStatePolicies.Any);

        app.MapAppApi();

        app.MapSpaFallback();

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

        AddDataProtection(builder);

        builder.Services.AddSingleton(sp => new WorkStateQueries(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new BoardQueries(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new ApiQueries(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new BoardReader(
            sp.GetRequiredService<WorkStateQueries>(),
            sp.GetRequiredService<BoardQueries>()));

        var stream = new StreamOptions();
        builder.Configuration.GetSection(StreamOptions.Section).Bind(stream);
        builder.Services.AddSingleton(stream);

        // One LISTEN per process, owning a connection of its own for the lifetime of the host.
        builder.Services.AddSingleton<EventStream>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EventStream>());

        // The write side. CallerIdentity reads the bearer token's roles off the request, which is
        // where the actor and work_verify's run_by come from (doc 01 §6) — never from an argument.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp => new Housekeeping(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton(sp => new WorkWriter(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<BoardQueries>(),
            sp.GetRequiredService<Housekeeping>()));
        builder.Services.AddSingleton<CallerIdentity>();
        builder.Services.AddSingleton(sp => new SessionStore(sp.GetRequiredService<NpgsqlDataSource>()));
    }

    /// <summary>
    /// The dashboard cookie's signing keys, into Postgres rather than the container filesystem — see
    /// <see cref="PostgresXmlRepository"/> for why. Registered here, inside the block that a missing
    /// connection string skips, because keys in the store are only an option when there is a store.
    /// </summary>
    private static void AddDataProtection(WebApplicationBuilder builder)
    {
        builder.Services.AddDataProtection().SetApplicationName(DataProtectionApplicationName);

        builder.Services.AddSingleton<IXmlRepository>(sp =>
            new PostgresXmlRepository(sp.GetRequiredService<NpgsqlDataSource>()));

        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IXmlRepository>((options, repository) => options.XmlRepository = repository);
    }
}
