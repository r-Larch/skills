using OpenIddict.Abstractions;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// Deletes token and authorization rows that expired or were revoked more than
/// <see cref="Retention"/> ago. OpenIddict's own pruning is a Quartz job; one statement pair every few
/// hours does not need a scheduler.
/// </summary>
public sealed class OAuthPruning(IServiceScopeFactory scopes, ILogger<OAuthPruning> logger) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(FirstRun, stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await PruneAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var threshold = DateTimeOffset.UtcNow - Retention;

            var tokens = await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>()
                .PruneAsync(threshold, cancellationToken);
            var authorizations = await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
                .PruneAsync(threshold, cancellationToken);

            if (tokens + authorizations > 0)
            {
                logger.LogInformation("Pruned {Tokens} OAuth tokens and {Authorizations} authorizations.", tokens, authorizations);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "OAuth pruning failed; the next run retries.");
        }
    }
}
