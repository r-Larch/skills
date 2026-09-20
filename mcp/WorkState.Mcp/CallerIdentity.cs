using Microsoft.AspNetCore.Http;
using WorkState.Core.Domain;
using WorkState.Core.Writing;

namespace WorkState.Mcp;

/// <summary>
/// Who is calling, from the bearer token and nothing else. doc 01 §6 — the actor on every event and
/// the <c>run_by</c> on every verification come from here, never from an argument, which is what
/// makes a worker structurally unable to record an orchestrator's evidence.
/// </summary>
public sealed class CallerIdentity(IHttpContextAccessor accessor)
{
    public Actor Actor
    {
        get
        {
            var user = accessor.HttpContext?.User;

            if (user is null)
            {
                return Actor.System;
            }

            if (user.IsInRole(WorkStatePolicies.Controller))
            {
                return new Actor(WorkStatePolicies.Controller, ActorRole.Controller);
            }

            if (user.IsInRole(WorkStatePolicies.Orchestrator))
            {
                return new Actor(WorkStatePolicies.Orchestrator, ActorRole.Orchestrator);
            }

            return user.IsInRole(WorkStatePolicies.Worker)
                ? new Actor(WorkStatePolicies.Worker, ActorRole.Worker)
                : Actor.System;
        }
    }
}
