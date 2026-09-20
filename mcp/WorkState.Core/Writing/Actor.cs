using System.Text;
using WorkState.Core.Domain;

namespace WorkState.Core.Writing;

/// <summary>
/// Who is writing, taken from the token. doc 01 §6 — the actor on every event never comes from a
/// self-declared argument, which is what makes the journal readable when six agents write at once.
/// </summary>
public sealed record Actor(string Name, ActorRole Role)
{
    public static readonly Actor System = new("system", ActorRole.System);

    /// <summary>
    /// doc 07 §3 condition 2, and the entire reason a worker may hold <c>work_verify</c>: a worker's
    /// evidence is recorded as a worker's, so it can never satisfy its own item's closure. The
    /// argument is not consulted for a worker — the token decides.
    /// </summary>
    public VerifyRunner RunnerFor(string? asked) => Role switch
    {
        ActorRole.Worker => VerifyRunner.Worker,
        _ when Enum.TryParse<VerifyRunner>(asked?.Trim(), ignoreCase: true, out var parsed)
               && Enum.IsDefined(parsed) => parsed,
        ActorRole.Human => VerifyRunner.Human,
        _ => VerifyRunner.Orchestrator,
    };
}

/// <summary>
/// Native enum labels. Dapper sends a CLR enum parameter as its underlying <c>int</c>, which a
/// Postgres enum column rejects outright — so every enum this layer writes travels as text and is
/// cast in the statement.
/// </summary>
internal static class Pg
{
    public static string Label(Enum value)
    {
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
