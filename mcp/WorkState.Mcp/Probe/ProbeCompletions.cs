using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WorkState.Abstractions.Probe;

namespace WorkState.Mcp.Probe;

/// <summary>
/// PROBE Q5, second half: <c>completions/complete</c> for <c>{id}</c> of <see cref="ProbeUris.Template"/>.
/// Registered with <c>WithCompleteHandler</c>; doc 01 §11 names that as the mechanism.
/// </summary>
public static class ProbeCompletions
{
    public static ValueTask<CompleteResult> HandleAsync(
        RequestContext<CompleteRequestParams> request,
        CancellationToken cancellationToken)
    {
        var argumentName = request.Params?.Argument?.Name;
        var typed = request.Params?.Argument?.Value ?? string.Empty;

        var isTemplateId =
            request.Params?.Ref is ResourceTemplateReference { Uri: ProbeUris.Template }
            && argumentName == "id";

        if (!isTemplateId)
        {
            return ValueTask.FromResult(new CompleteResult());
        }

        string[] candidates =
        [
            ..ProbeUris.TemplateIdCandidates,
            ProbeTokens.Completion,
        ];

        var matches = candidates
            .Where(c => c.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return ValueTask.FromResult(new CompleteResult
        {
            Completion = new Completion
            {
                Values = matches,
                Total = matches.Count,
                HasMore = false,
            },
        });
    }
}
