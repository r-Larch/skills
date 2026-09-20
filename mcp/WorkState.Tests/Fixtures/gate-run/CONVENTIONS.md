# Conventions — gate-run

## 1. Red lines — violating any of these fails the task outright

**Invariant code — never modify.** These serve 23 live chatbots:
- `App.Web/Core/Rag/**`
- `App.Web/Features/DataQueries/**`
- `ChatbotResponder`

You may **read** them freely. New tables and new files only.

**Database.**
- **Never connect to `app.example.com`.** Production is read-only for this run; only the orchestrator
  touches it.
- **Always pass `--connection "<local>"` to any `dotnet ef database` command.** A bare one resolves
  through the design-time factory and targets production.

## 2. Stack facts

- Backend: .NET 10, solution `App.slnx`. EF Core 10 + Npgsql with `UseSnakeCaseNamingConvention()`.
