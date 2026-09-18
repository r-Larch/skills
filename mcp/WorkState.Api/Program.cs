using WorkState.Api;

if (args is ["--health-check", ..])
{
    return await HealthCheckProbe.RunAsync();
}

var app = ApiHost.Build(args);
await app.RunAsync();
return 0;
