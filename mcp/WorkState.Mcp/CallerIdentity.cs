using Microsoft.AspNetCore.Http;
using WorkState.Core.Domain;
using WorkState.Core.Writing;

namespace WorkState.Mcp;

/// <summary>
/// Who is calling, from the bearer token and nothing else. doc 01 §6 — the actor on every event and
/// the <c>run_by</c> on every verification come from here, never from an argument, which is what
/// makes a worker structurally unable to record an orchestrator's evidence.
/// <para>
/// The session (doc 11) is attached beside the role, never in place of it: <see cref="CallerSession"/>
/// resolves it from the registration headers, and no header reaches the role.
/// </para>
/// </summary>
public sealed class CallerIdentity(IHttpContextAccessor accessor)
{
    public Actor Actor
    {
        get
        {
            var role = Role;
            if (role == Actor.System)
            {
                return role;
            }

            role = role with { UserId = UserId };
            return accessor.HttpContext?.Items[CallerSession.ItemKey] is SessionRef session
                ? role with { SessionId = session.Id, Session = session.Name }
                : role;
        }
    }

    /// <summary>The session helper's key, when a static agent token sent one; never a minted token's.</summary>
    public string? ClientKey => accessor.HttpContext?.User.FindFirst(CallerSession.ClientKeyClaim)?.Value;

    /// <summary>The signed-in user the credential belongs to (<see cref="CallerSession.UserIdClaim"/>); null for a static token.</summary>
    public Guid? UserId
        => Guid.TryParse(accessor.HttpContext?.User.FindFirst(CallerSession.UserIdClaim)?.Value, out var id) ? id : null;

    private Actor Role
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
