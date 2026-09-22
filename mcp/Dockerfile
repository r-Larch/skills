# syntax=docker/dockerfile:1
# Build context is mcp/.

# The SPA is built here rather than by the API project's `-p:BuildSpa=true` target, because that
# target would put Node inside the SDK image and make every C# layer depend on the frontend's
# lockfile. Vite's outDir is ../WorkState.Api/wwwroot, so the assets land beside this stage's
# frontend/ directory and the build stage copies them in.
FROM node:24-slim AS spa
WORKDIR /src/frontend

RUN npm install --global pnpm@9.15.4

COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile

COPY frontend/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY WorkState.Core/WorkState.Core.csproj WorkState.Core/
COPY WorkState.Mcp/WorkState.Mcp.csproj WorkState.Mcp/
COPY WorkState.Api/WorkState.Api.csproj WorkState.Api/
RUN dotnet restore WorkState.Api/WorkState.Api.csproj

COPY . .
COPY --from=spa /src/WorkState.Api/wwwroot WorkState.Api/wwwroot
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
