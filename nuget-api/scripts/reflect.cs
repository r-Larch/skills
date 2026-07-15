// reflect.cs — load-only reflection + surface rendering (included by surface/find/diff).
// Requires the entry script to declare: #:package System.Reflection.MetadataLoadContext
using System.Reflection;
using System.Text;

static class Sig
{
    public static string N(Type t) => t.IsByRef ? N(t.GetElementType()!)
        : t.IsArray ? N(t.GetElementType()!) + "[]"
        : t.IsGenericType ? $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(N))}>"
        : t.Name;
}

// Loaded assembly with the full closure resolved. Surfaces ReflectionTypeLoadException as Diagnostics
// so the caller (and the AI) knows results are partial and why.
sealed class Loaded : IDisposable
{
    public MetadataLoadContext Mlc = null!;
    public Assembly Asm = null!;
    public Type[] Types = Array.Empty<Type>();
    public string Diagnostics = "";
    public void Dispose() => Mlc.Dispose();

    public static Loaded Open(string binDir, string dllPath)
    {
        var rt = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var paths = Directory.GetFiles(binDir, "*.dll").Concat(Directory.GetFiles(rt, "*.dll")).Distinct().ToArray();
        var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        var asm = mlc.LoadFromAssemblyPath(dllPath);
        Type[] types; string diag = "";
        try { types = asm.GetExportedTypes(); }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t is not null).ToArray()!;
            var missing = (e.LoaderExceptions ?? Array.Empty<Exception>())
                .Where(x => x is not null).Select(x => x!.Message).Distinct().Take(5);
            var failed = e.Types.Count(t => t is null);
            diag = $"// PARTIAL RESULT: {failed} type(s) could not be loaded — some dependencies are missing, so the surface below is incomplete.\n"
                 + string.Concat(missing.Select(m => $"//   - {m}\n"))
                 + "// Fix: the bin folder is missing dependencies. Prefer the by-package form (which builds a complete workbench); if using --bin, rebuild it. See SKILL.md.\n";
        }
        catch (Exception e)   // FileNotFoundException / TypeLoadException / BadImageFormatException: an incomplete closure
        {
            types = Array.Empty<Type>();
            diag = $"// ERROR: could not enumerate types — {e.GetType().Name}: {e.Message}\n"
                 + "// This bin folder is missing a dependency, so nothing can be read from it.\n"
                 + "// Fix: use the by-package form (e.g. `surface.cs <pkgId> <version> …`), which builds a complete workbench with the full closure. See SKILL.md.\n";
        }
        return new Loaded { Mlc = mlc, Asm = asm, Types = types, Diagnostics = diag };
    }
}

static class Render
{
    // Full public surface (signatures + inline xml summaries) for types matching `filter`.
    // Appends an "unresolved types" note when a type's members can't be introspected.
    public static string Surface(Type[] types, string filter, XmlDocs docs)
    {
        var sb = new StringBuilder();
        var unresolved = new List<string>();
        foreach (var type in types
                 .Where(t => filter == "" || (t.FullName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            try
            {
                // NB: kind/baseSuffix touch BaseType/IsValueType, which resolve the base type and can
                // throw when a dependency is missing — keep them inside the try with everything else.
                var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
                var baseSuffix = type.BaseType is { } b && b.Name != "Object" && b.Name != "ValueType" ? $" : {Sig.N(b)}" : "";
                sb.Append($"\n{kind} {type.FullName}{baseSuffix}");
                if (docs.Summary(type.FullName ?? "") is { } td) sb.Append($"   // {td}");
                sb.AppendLine();
                foreach (var c in type.GetConstructors().Where(c => c.IsPublic))
                    sb.AppendLine($"  .ctor({Ps(c)})");
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    sb.AppendLine($"  {Sig.N(p.PropertyType)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.GetSetMethod() is not null ? "set; " : "")}}}{Tail(type, p.Name, docs)}");
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
                    sb.AppendLine($"  {Sig.N(m.ReturnType)} {m.Name}({Ps(m)}){Tail(type, m.Name, docs)}");
                if (type.IsEnum)
                    foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                        sb.AppendLine($"  = {f.Name}");
            }
            catch (Exception ex)
            {
                unresolved.Add($"{Safe(type)} ({ex.GetType().Name})");
                sb.AppendLine($"\n// {Safe(type)}: not introspected — {ex.GetType().Name} (a type it depends on failed to resolve).");
            }
        }
        if (unresolved.Count > 0)
            sb.AppendLine($"\n// NOTE: {unresolved.Count} type(s) had members that could not be resolved: {string.Join(", ", unresolved.Take(8))}"
                        + (unresolved.Count > 8 ? " …" : "") + ". Their listing above is partial.");
        return sb.ToString();

        static string Ps(MethodBase mb) => string.Join(", ", mb.GetParameters().Select(p => $"{Sig.N(p.ParameterType)} {p.Name}"));
        static string Tail(Type t, string member, XmlDocs d) => d.Summary($"{t.FullName}.{member}") is { } s ? $"   // {s}" : "";
        static string Safe(Type t) { try { return t.FullName ?? t.Name; } catch { return t.Name; } }
    }
}
