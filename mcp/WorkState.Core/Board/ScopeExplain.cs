using WorkState.Core.Rendering;

namespace WorkState.Core.Board;

/// <summary>
/// What a caller is told when a <c>scope</c> or a <c>{ref}</c> did not resolve. One renderer for
/// every surface that resolves a ref — tools and resources alike — because the two disagreeing is
/// exactly how ACCEPTANCE A-2 happened: the item resource answered an ambiguous ref with the
/// empty-server onboarding text, telling an agent that a store holding 267 tasks was empty.
/// <para>
/// A confident wrong answer is the worst outcome in this design, worse than an error: an agent
/// believes it and stops. Only <see cref="ScopeResult.Empty"/> — no workspace, or one with nothing
/// in it — is a cold start; everything else is a refusal in the shape of doc 06 §6.
/// </para>
/// </summary>
public static class ScopeExplain
{
    /// <summary>How many candidates a refusal lists before it stops being readable.</summary>
    private const int CandidateCap = 5;

    /// <param name="arguments">
    /// The caller's other arguments, as they are written after <c>scope:</c> — <c>q:"…"</c> for
    /// <c>work_search</c>, which refuses a call without one — so every retry printed is a whole call.
    /// </param>
    public static string Text(ScopeResult result, string call, string? asked, string? arguments = null) => result switch
    {
        ScopeResult.Empty => BriefRenderer.Onboarding(),
        ScopeResult.Unbound unbound => BriefRenderer.Unbound(unbound.Keys, unbound.Projects),
        ScopeResult.Unchosen unchosen => BriefRenderer.Unchosen(unchosen.Projects),
        ScopeResult.Ambiguous ambiguous => Ambiguous(ambiguous, call, Rest(arguments)),
        ScopeResult.Missing missing => Missing(missing, call, Rest(arguments)),
        _ => Refusal.Blocked(
            $"{call} — scope \"{asked}\" could not be resolved.",
            "nothing in this workspace answers to that scope",
            "work_tree() to see every ref that exists",
            "work_brief() to orient"),
    };

    /// <summary>
    /// Every candidate, best first. ACCEPTANCE A-1: naming one candidate confidently and wrongly
    /// sends a careful agent to another run's item, so the caller chooses from the list rather than
    /// trusting a guess the server had no grounds for.
    /// </summary>
    private static string Rest(string? arguments)
        => string.IsNullOrWhiteSpace(arguments) ? string.Empty : $", {arguments.Trim()}";

    private static string Ambiguous(ScopeResult.Ambiguous ambiguous, string call, string rest)
    {
        var shown = ambiguous.Candidates.Take(CandidateCap).ToList();
        var routes = shown.Select(c => $"{call}(scope:\"{c}\"{rest})").ToList();

        if (ambiguous.Candidates.Count > shown.Count)
        {
            routes.Add($"work_search(q:\"{ambiguous.Asked}\") for the rest");
        }

        if (ambiguous.Matches > ambiguous.Candidates.Count)
        {
            routes.Add("work_tree() to see where the others sit — no ref names only them");
        }

        return Refusal.Blocked(
            $"{call} — \"{ambiguous.Asked}\" exists in {ambiguous.Matches} runs.",
            [new Unmet($"\"{ambiguous.Asked}\" is ambiguous — qualify it with the run it belongs to", routes)]);
    }

    private static string Missing(ScopeResult.Missing missing, string call, string rest)
    {
        var routes = new List<string>();
        routes.AddRange(missing.Nearby.Select(n => $"{call}(scope:\"{n}\"{rest})"));

        if (routes.Count == 0)
        {
            routes.Add($"work_search(q:\"{missing.Asked}\")");
        }

        routes.Add("work_tree() to see every ref that exists");

        return Refusal.Blocked(
            $"{call} — no item has ref \"{missing.Asked}\".",
            [new Unmet($"\"{missing.Asked}\" is not a ref in this workspace", routes)]);
    }
}
