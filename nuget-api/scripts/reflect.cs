// reflect.cs — load-only reflection + surface rendering (included by surface/find/diff).
// Requires the entry script to declare: #:package System.Reflection.MetadataLoadContext
using System.Reflection;
using System.Text;

static class Sig
{
    // Type name with value-type nullability (Nullable<T> -> T?). No NRT (see Nl for that).
    public static string N(Type t)
    {
        if (t.IsByRef) return N(t.GetElementType()!);
        if (t.IsArray) return N(t.GetElementType()!) + "[]";
        if (IsNullableValue(t, out var u)) return N(u) + "?";
        if (t.IsGenericType) return $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(N))}>";
        return t.Name;
    }

    // MetadataLoadContext-safe Nullable<T> detection (typeof(Nullable<>) can't be compared across type systems).
    public static bool IsNullableValue(Type t, out Type underlying)
    {
        underlying = t;
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        { underlying = t.GetGenericArguments()[0]; return true; }
        return false;
    }
}

// Nullable-reference-type aware type rendering: decodes System.Runtime.CompilerServices.NullableAttribute
// (per-member byte[] flags, or a single byte) + NullableContextAttribute (the enclosing default) so that
// `string?`, `List<string?>?`, `HttpStatusCode?` render as written. Degrades to Sig.N on any surprise.
static class Nl
{
    public static string Of(Type t, MemberInfo member, byte ctx)
    {
        try { var (f, s) = ReadNullable(member.GetCustomAttributesData(), ctx); return new Reader(f, s).Read(t); }
        catch { return Sig.N(t); }
    }

    // For method return types the flags live on the return parameter; for params, on the parameter.
    public static string OfParam(ParameterInfo p, byte ctx)
    {
        try { var (f, s) = ReadNullable(p.GetCustomAttributesData(), ctx); return new Reader(f, s).Read(p.ParameterType); }
        catch { return Sig.N(p.ParameterType); }
    }

    // Walks a type tree consuming the flag stream in the same pre-order the compiler emits it.
    sealed class Reader
    {
        readonly byte[] _f; readonly bool _single; int _i;
        public Reader(byte[] f, bool single) { _f = f; _single = single; }
        byte Take() => _single ? _f[0] : (_i < _f.Length ? _f[_i++] : (byte)0);

        public string Read(Type t)
        {
            if (t.IsByRef) return Read(t.GetElementType()!);
            if (t.IsArray) { var f = Take(); return Read(t.GetElementType()!) + "[]" + (f == 2 ? "?" : ""); }
            if (Sig.IsNullableValue(t, out var u)) { Take(); return Read(u) + "?"; }   // Nullable<> consumes a 0 slot
            if (t.IsValueType)
            {
                Take();                                                // value types consume a 0 slot
                if (!t.IsGenericType) return t.Name;
                return $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(Read))}>";
            }
            var g = Take();                                            // reference type / type parameter
            var q = g == 2 ? "?" : "";
            if (t.IsGenericType)
                return $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(Read))}>{q}";
            return t.Name + q;
        }
    }

    static (byte[] flags, bool single) ReadNullable(IList<CustomAttributeData> attrs, byte ctx)
    {
        var at = attrs.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
        if (at is null) return (new[] { ctx }, true);                  // no member flag -> the enclosing context default
        var arg = at.ConstructorArguments[0];
        if (arg.Value is IReadOnlyCollection<CustomAttributeTypedArgument> arr)
            return (arr.Select(x => (byte)x.Value!).ToArray(), false);
        return (new[] { (byte)arg.Value! }, true);
    }

    // Nearest NullableContextAttribute (member, then declaring type, then module) — the default for
    // positions the member's own NullableAttribute doesn't cover.
    public static byte Context(MemberInfo member)
    {
        foreach (var provider in Providers(member))
        {
            var at = provider.FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute");
            if (at is not null) return (byte)at.ConstructorArguments[0].Value!;
        }
        return 0;

        static IEnumerable<IList<CustomAttributeData>> Providers(MemberInfo m)
        {
            yield return m.GetCustomAttributesData();
            if (m.DeclaringType is { } d) yield return d.GetCustomAttributesData();
        }
    }
}

// Attribute-driven modifiers/markers read from metadata.
static class Meta
{
    public static bool Has(MemberInfo m, string fullName) =>
        m.GetCustomAttributesData().Any(a => a.AttributeType.FullName == fullName);

    public static bool Required(MemberInfo m) => Has(m, "System.Runtime.CompilerServices.RequiredMemberAttribute");
    public static bool SetsRequired(MemberInfo m) => Has(m, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");

    public static string? Obsolete(MemberInfo m)
    {
        var at = m.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == "System.ObsoleteAttribute");
        if (at is null) return null;
        var msg = at.ConstructorArguments.Count > 0 ? at.ConstructorArguments[0].Value as string : null;
        return msg is null ? "[Obsolete]" : $"[Obsolete(\"{msg}\")]";
    }
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
        // Resolver = the package's bin closure first, then every installed shared framework
        // (Microsoft.NETCore.App, Microsoft.AspNetCore.App, …) so web/framework base types resolve.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddDir(string? dir) { if (dir is not null && Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.dll")) byName.TryAdd(Path.GetFileName(f), f); }
        AddDir(binDir);                                  // package assemblies win over framework copies
        foreach (var d in FrameworkDirs()) AddDir(d);
        var mlc = new MetadataLoadContext(new PathAssemblyResolver(byName.Values.ToArray()));
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

    // The newest version dir of every shared framework under dotnet/shared (NETCore.App, AspNetCore.App, …).
    static IEnumerable<string> FrameworkDirs()
    {
        var rt = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        yield return rt;
        var shared = Directory.GetParent(rt.TrimEnd(Path.DirectorySeparatorChar))?.Parent;   // …/shared
        if (shared is null) yield break;
        foreach (var fw in shared.GetDirectories())
        {
            var newest = fw.GetDirectories()
                .OrderByDescending(d => d.Name, Comparer<string>.Create(Cache.CompareVer)).FirstOrDefault();
            if (newest is not null) yield return newest.FullName;
        }
    }
}

static class Render
{
    const BindingFlags Own = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // Full public surface for types matching `filter`. Members are DECLARED-ONLY (own); pass
    // showInherited to additionally group base-type members under `  <BaseType>:` sections.
    // Signatures are nullable-aware (T?, string?) and carry required / [Obsolete] / static / override markers.
    public static string Surface(Type[] types, string filter, XmlDocs docs, bool showInherited = false)
    {
        var sb = new StringBuilder();
        var unresolved = new List<string>();
        foreach (var type in types
                 .Where(t => filter == "" || (t.FullName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            try
            {
                var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
                var baseName = type.BaseType is { } bt && bt.Name != "Object" && bt.Name != "ValueType" ? Sig.N(bt) : null;
                var bases = (baseName is null ? Enumerable.Empty<string>() : new[] { baseName })
                    .Concat(type.IsEnum ? Enumerable.Empty<string>() : DirectInterfaces(type).Select(Sig.N));
                var clause = string.Join(", ", bases);
                var obs = Meta.Obsolete(type) is { } to ? to + " " : "";
                sb.Append($"\n{obs}{kind} {type.FullName}{(clause.Length > 0 ? " : " + clause : "")}");
                if (docs.Summary(type.FullName ?? "") is { } td) sb.Append($"   // {td}");
                sb.AppendLine();

                var emitted = new HashSet<string>(StringComparer.Ordinal);   // method key -> suppress overridden base copies
                EmitMembers(sb, type, type, docs, emitted, indent: "  ");

                if (showInherited)
                    for (var b = type.BaseType; b is not null && b.Name != "Object" && b.Name != "ValueType"; b = b.BaseType)
                    {
                        var before = sb.Length;
                        var section = new StringBuilder();
                        EmitMembers(section, b, type, docs, emitted, indent: "    ");
                        if (section.Length > 0) sb.Append($"  {Sig.N(b)}:\n").Append(section);
                    }
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

        static string Safe(Type t) { try { return t.FullName ?? t.Name; } catch { return t.Name; } }
    }

    // Interfaces DECLARED on this type: all implemented interfaces minus those coming from the base type
    // or already implied by another interface in the set (so `class X : Base, IA, IB<T>` reads like C#).
    static IEnumerable<Type> DirectInterfaces(Type type)
    {
        Type[] all;
        try { all = type.GetInterfaces(); } catch { return Array.Empty<Type>(); }
        if (all.Length == 0) return all;
        string Key(Type t) => t.FullName ?? t.Name;
        var fromBase = new HashSet<string>(SafeIfaces(() => type.BaseType?.GetInterfaces()).Select(Key));
        var implied = new HashSet<string>(all.SelectMany(i => SafeIfaces(i.GetInterfaces)).Select(Key));
        return all.Where(i => !fromBase.Contains(Key(i)) && !implied.Contains(Key(i)))
                  .OrderBy(i => i.Name, StringComparer.Ordinal);

        static Type[] SafeIfaces(Func<Type[]?> f) { try { return f() ?? Array.Empty<Type>(); } catch { return Array.Empty<Type>(); } }
    }

    // Emit declared members of `declaring`. `docOwner` is the type used for xml-doc keys (the leaf type,
    // so inherited members still look up docs on the base). `emitted` dedupes overridden methods.
    static void EmitMembers(StringBuilder sb, Type declaring, Type docOwner, XmlDocs docs, HashSet<string> emitted, string indent)
    {
        bool classLike = !declaring.IsInterface && !declaring.IsEnum;
        if (declaring == docOwner)
            foreach (var c in declaring.GetConstructors().Where(c => c.IsPublic))
                sb.AppendLine($"{indent}{Mark(c)}.ctor({Ps(c)})");

        foreach (var p in declaring.GetProperties(Own))
        {
            var ctx = Nl.Context(p);
            var acc = p.GetMethod ?? p.SetMethod;
            var mods = (acc?.IsStatic == true ? "static " : "") + (Meta.Required(p) ? "required " : "");
            sb.AppendLine($"{indent}{Mark(p)}{mods}{Nl.Of(p.PropertyType, p, ctx)} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.GetSetMethod() is not null ? "set; " : "")}}}{Doc(p.Name)}");
        }

        foreach (var m in declaring.GetMethods(Own).Where(m => !m.IsSpecialName))
        {
            var key = m.Name + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")";
            if (!emitted.Add(key)) continue;                     // already shown (overridden in a derived type)
            var ctx = Nl.Context(m);
            sb.AppendLine($"{indent}{Mark(m)}{MethodMods(m, classLike)}{Nl.OfParam(m.ReturnParameter, ctx)} {m.Name}({Ps(m)}){Doc(m.Name)}");
        }

        if (declaring.IsEnum && declaring == docOwner)
            foreach (var f in declaring.GetFields(BindingFlags.Public | BindingFlags.Static))
                sb.AppendLine($"{indent}= {f.Name}");

        string Doc(string member) => docs.Summary($"{docOwner.FullName}.{member}") is { } s ? $"   // {s}" : docs.Summary($"{declaring.FullName}.{member}") is { } s2 ? $"   // {s2}" : "";
    }

    static string Mark(MemberInfo m)
    {
        var parts = new List<string>();
        if (Meta.Obsolete(m) is { } o) parts.Add(o);
        if (m is ConstructorInfo c && Meta.SetsRequired(c)) parts.Add("[SetsRequiredMembers]");
        return parts.Count > 0 ? string.Join(" ", parts) + " " : "";
    }

    static string MethodMods(MethodInfo m, bool classLike)
    {
        var parts = new List<string>();
        if (m.IsStatic) parts.Add("static");
        else if (classLike)
        {
            if (m.IsAbstract) parts.Add("abstract");
            else if (m.IsVirtual && !m.IsFinal) parts.Add(IsOverride(m) ? "override" : "virtual");
        }
        return parts.Count > 0 ? string.Join(" ", parts) + " " : "";

        static bool IsOverride(MethodInfo m) { try { return m.GetBaseDefinition().DeclaringType != m.DeclaringType; } catch { return false; } }
    }

    static string Ps(MethodBase mb)
    {
        byte ctx = mb is MethodInfo mi ? Nl.Context(mi) : Nl.Context(mb);
        return string.Join(", ", mb.GetParameters().Select(p => $"{Nl.OfParam(p, ctx)} {p.Name}"));
    }
}
