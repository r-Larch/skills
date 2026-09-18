using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Protocol;
using WorkState.Api.Auth;
using WorkState.Mcp;
using WorkState.Mcp.Probe;

namespace WorkState.Api;

/// <summary>
/// Builds the host. Kept separate from <c>Program.cs</c> so tests can start the real Kestrel
/// pipeline in-process without a test-host shim.
/// </summary>
public static class ApiHost
{
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
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(WorkStatePolicies.Reader, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Reader))
            .AddPolicy(WorkStatePolicies.Writer, policy => policy
                .AddAuthenticationSchemes(StaticTokenHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(WorkStatePolicies.Writer));

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "workstate-probe",
                    Title = "WorkState probe server",
                    Version = "0.0.1-probe",
                };
                options.ServerInstructions = ProbeServerInstructions.Text;
            })
            .WithHttpTransport()
            .AddAuthorizationFilters()
            .WithToolsFromAssembly(typeof(ProbeTools).Assembly)
            .WithPromptsFromAssembly(typeof(ProbePrompts).Assembly)
            .WithResourcesFromAssembly(typeof(ProbeResources).Assembly)
            .WithCompleteHandler(ProbeCompletions.HandleAsync);

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        app.MapMcp("/mcp").RequireAuthorization(WorkStatePolicies.Reader);

        return app;
    }
}
