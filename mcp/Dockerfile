# syntax=docker/dockerfile:1
# Build context is mcp/. No SPA yet, so no Node stage — the dashboard unit adds one.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY WorkState.Abstractions/WorkState.Abstractions.csproj WorkState.Abstractions/
COPY WorkState.Core/WorkState.Core.csproj WorkState.Core/
COPY WorkState.Mcp/WorkState.Mcp.csproj WorkState.Mcp/
COPY WorkState.Api/WorkState.Api.csproj WorkState.Api/
RUN dotnet restore WorkState.Api/WorkState.Api.csproj

COPY . .
RUN dotnet publish WorkState.Api/WorkState.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    WORKSTATE_HEALTHCHECK_URL=http://127.0.0.1:8080/health
EXPOSE 8080

# The app probes itself, so the runtime image needs no curl and the build needs no apt.
HEALTHCHECK --interval=15s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/WorkState.Api.dll", "--health-check"]

USER $APP_UID
ENTRYPOINT ["dotnet", "WorkState.Api.dll"]
