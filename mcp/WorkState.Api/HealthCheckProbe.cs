namespace WorkState.Api;

/// <summary>
/// The container HEALTHCHECK runs the app's own binary with <c>--health-check</c>, so the runtime
/// image needs no curl and the build needs no apt.
/// </summary>
public static class HealthCheckProbe
{
    public static async Task<int> RunAsync()
    {
        var url = Environment.GetEnvironmentVariable("WORKSTATE_HEALTHCHECK_URL")
                  ?? "http://127.0.0.1:8080/health";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"health check failed: {ex.Message}");
            return 1;
        }
    }
}
