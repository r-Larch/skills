using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WorkState.Core.Rendering;

namespace WorkState.Mcp.Tools;

/// <summary>
/// doc 01 §3.6 — a call whose arguments cannot bind is refused with the call that would. The SDK
/// binds arguments before a tool body runs, so a missing or wrong-shaped one never reaches
/// <c>Guarded</c> and used to surface as the SDK's generic "An error occurred invoking". This judges
/// the arguments against the tool's own published <c>inputSchema</c> first, and turns what binding
/// still throws into the same refusal shape.
/// </summary>
public static class ToolArguments
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is not McpServerTool tool || context.Params is not { } call)
            {
                return await next(context, cancellationToken);
            }

            var schema = new Schema(tool.ProtocolTool);
            var arguments = new Dictionary<string, JsonElement>(
                call.Arguments ?? new Dictionary<string, JsonElement>(),
                StringComparer.Ordinal);

            var unmet = Judge(schema, arguments, out var coerced);
            if (unmet.Count > 0)
            {
                return Refused(Refusal.Blocked(
                    $"{schema.Name} — {Plural(unmet.Count, "argument")} cannot bind. Nothing ran.",
                    unmet));
            }

            if (coerced)
            {
                call.Arguments = arguments;
            }

            try
            {
                return await next(context, cancellationToken);
            }
            catch (JsonException failure)
            {
                return Refused(Unbound(schema, failure));
            }
            catch (ArgumentException failure) when (failure.ParamName == "arguments")
            {
                return Refused(Unbound(schema, failure));
            }
        };

    private static List<Unmet> Judge(
        Schema schema,
        Dictionary<string, JsonElement> arguments,
        out bool coerced)
    {
        coerced = false;
        var unmet = new List<Unmet>();
        var suggested = new HashSet<string>(StringComparer.Ordinal);

        // An undeclared name is dropped by the binder without a word, so work_update(check_all:true)
        // answered "OK updated" having written nothing.
        foreach (var unknown in arguments.Keys.Where(k => schema.Find(k) is null))
        {
            var closest = schema.Closest(unknown);
            if (closest is not null)
            {
                suggested.Add(closest.Name);
            }

            unmet.Add(new Unmet(
                closest is null
                    ? $"{unknown} is not a parameter of {schema.Name}, which takes "
                      + string.Join(", ", schema.Parameters.Select(p => p.Name))
                    : $"{unknown} is not a parameter of {schema.Name} — the closest it declares is {closest.Name}",
                schema.Example(closest)));
        }

        foreach (var missing in schema.Parameters.Where(p =>
                     p.Required && !arguments.ContainsKey(p.Name) && !suggested.Contains(p.Name)))
        {
            unmet.Add(new Unmet(
                $"{missing.Name} is required and was not sent — {Glyph.Clip(missing.Description, 90)}",
                schema.Example(missing)));
        }

        foreach (var (name, value) in arguments.ToList())
        {
            if (schema.Find(name) is not { } parameter || parameter.Accepts(value.ValueKind))
            {
                continue;
            }

            // A lone string where a list of strings is expected has one reading — refs:"2.3" is
            // refs:["2.3"] — so it binds as that rather than costing a round trip.
            if (value.ValueKind == JsonValueKind.String && parameter.ListOfStrings)
            {
                arguments[name] = JsonSerializer.SerializeToElement(new[] { value.GetString() });
                coerced = true;
                continue;
            }

            unmet.Add(new Unmet(
                $"{name} takes {parameter.Expected}, and {Sent(value.ValueKind)} was sent",
                schema.Example(parameter)));
        }

        return unmet;
    }

    private static string Unbound(Schema schema, Exception failure)
        => Refusal.Blocked(
            $"{schema.Name} — an argument cannot bind. Nothing ran.",
            $"binding refused it: {Glyph.Clip(failure.Message, 140)}",
            $"{schema.Example(null)} with each argument in the shape its tools/list entry declares");

    private static CallToolResult Refused(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = true,
    };

    private static string Sent(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static string Plural(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The parameters as <c>tools/list</c> publishes them — the only contract a caller sees.</summary>
    private sealed class Schema
    {
        public Schema(Tool tool)
        {
            Name = tool.Name;
            var root = tool.InputSchema;

            var required = root.ValueKind == JsonValueKind.Object
                           && root.TryGetProperty("required", out var names)
                           && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                Parameters = [.. properties.EnumerateObject()
                    .Select(p => new Parameter(p.Name, p.Value, required.Contains(p.Name)))];
            }
        }

        public string Name { get; }

        public IReadOnlyList<Parameter> Parameters { get; } = [];

        public Parameter? Find(string name) => Parameters.FirstOrDefault(p => p.Name == name);

        /// <summary>
        /// The declared parameter an undeclared name most likely meant: the same name spelled
        /// differently (<c>include_counts</c>), one that extends or shortens it (<c>ref</c> → <c>refs</c>),
        /// or one a small edit away. Nothing, rather than a guess, when none is close.
        /// </summary>
        public Parameter? Closest(string asked)
        {
            var wanted = Fold(asked);
            if (wanted.Length == 0)
            {
                return null;
            }

            return Parameters
                .Select(p => (Parameter: p, Folded: Fold(p.Name)))
                .Select(c => (c.Parameter, Distance: c.Folded == wanted ? 0
                    : c.Folded.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(c.Folded, StringComparison.Ordinal) ? 1
                    : Levenshtein(c.Folded, wanted)))
                .Where(c => c.Distance <= Math.Max(1, wanted.Length / 3))
                .OrderBy(c => c.Distance)
                .Select(c => c.Parameter)
                .FirstOrDefault();
        }

        private static string Fold(string name)
            => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

        private static int Levenshtein(string a, string b)
        {
            var row = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 1; i <= a.Length; i++)
            {
                var diagonal = row[0];
                row[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }

            return row[b.Length];
        }

        /// <summary>A call that binds: every required parameter, plus the one being corrected.</summary>
        public string Example(Parameter? focus)
        {
            var shown = Parameters.Where(p => p.Required || p == focus).Select(p => $"{p.Name}:{p.Placeholder}");
            return $"{Name}({string.Join(", ", shown)})";
        }
    }

    private sealed class Parameter
    {
        private readonly HashSet<string> _types;
        private readonly string? _items;

        public Parameter(string name, JsonElement schema, bool required)
        {
            Name = name;
            Required = required;
            _types = Types(schema);
            _items = schema.TryGetProperty("items", out var items) ? Types(items).FirstOrDefault() : null;
            Description = schema.TryGetProperty("description", out var description)
                          && description.ValueKind == JsonValueKind.String
                ? description.GetString()
                : null;
        }

        public string Name { get; }

        public bool Required { get; }

        public string? Description { get; }

        public bool ListOfStrings => _types.Contains("array") && _items == "string";

        public string Expected => _types.Contains("array")
            ? _items is { } item ? $"an array of {item}s" : "an array"
            : _types.Contains("object") ? "an object" : $"a {_types.FirstOrDefault() ?? "value"}";

        public string Placeholder => _types.Contains("array")
            ? _items switch { "string" => "[\"…\"]", "object" => "[{…}]", _ => "[…]" }
            : _types.Contains("object") ? "{…}"
            : _types.Contains("string") ? "\"…\""
            : _types.Contains("boolean") ? "true"
            : _types.Contains("integer") || _types.Contains("number") ? "1"
            : "…";

        /// <summary>
        /// Only a container mismatch is judged here. A scalar of the wrong kind is left to binding,
        /// whose serializer is lenient in ways a schema check would contradict.
        /// </summary>
        public bool Accepts(JsonValueKind kind)
        {
            if (_types.Count == 0 || kind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return true;
            }

            var scalar = _types.Overlaps(["string", "integer", "number", "boolean"]);
            return kind switch
            {
                JsonValueKind.Array => _types.Contains("array"),
                JsonValueKind.Object => _types.Contains("object"),
                _ => scalar,
            };
        }

        private static HashSet<string> Types(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
            {
                return [];
            }

            var names = type.ValueKind switch
            {
                JsonValueKind.String => [type.GetString()],
                JsonValueKind.Array => type.EnumerateArray().Select(t => t.GetString()),
                _ => [],
            };

            return [.. names.OfType<string>().Where(n => n != "null")];
        }
    }
}
