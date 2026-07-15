#:property NoWarn=CA2266
#:include common.cs
// find-usages.cs — who USES a symbol, scanned from compiled IL (not source). ReSharper "Find Usages",
// but read from the DLLs: every method whose body references the symbol, across a set of assemblies.
// Semantic — catches extension-method call syntax (env.IsDevelopment()), overloads, generics, and
// inheritance that a text grep misses. When a portable PDB is present it resolves file:line and prints
// the source line; otherwise it reports the containing method and says a Debug build is needed for lines.
//
//   dotnet run find-usages.cs <pkgId> <version> <symbol>                 # scan the package's own assemblies
//   dotnet run find-usages.cs --bin <dir> <symbol> [--only a,b]         # scan every DLL in a bin dir (name-filtered)
//   dotnet run find-usages.cs --bin <dir> <Assembly.dll> <symbol>       # scan a single assembly
//
// <symbol> is a name, matched at dotted-segment boundaries (case-insensitive):
//   IsDevelopment                         any member named IsDevelopment, on any type
//   HostEnvironmentEnvExtensions.IsDevelopment   that member on that type (type given by suffix)
//   Microsoft.Extensions.Hosting.IHostEnvironment   a TYPE — casts, generic args, new, typeof, field/param types
// Reflection is metadata-only; nothing in the target executes. Uses in-box System.Reflection.Metadata.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// ---- args ------------------------------------------------------------------
List<string> dllsToScan = new();
string binDir, symbol; string[]? onlyFilters = null; string header;
if (args.Length >= 1 && args[0] == "--bin")
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: find-usages.cs --bin <dir> <symbol> [--only a,b]   |   --bin <dir> <Assembly.dll> <symbol>"); return 1; }
    binDir = args[1];
    if (!Directory.Exists(binDir)) { Console.Error.WriteLine($"dotnet-reflect: binDir not found: {binDir}"); return 2; }
    var rest = args.Skip(2).ToList();
    var onlyIdx = rest.IndexOf("--only");
    if (onlyIdx >= 0) { onlyFilters = (onlyIdx + 1 < rest.Count ? rest[onlyIdx + 1] : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); rest.RemoveRange(onlyIdx, Math.Min(2, rest.Count - onlyIdx)); }
    if (rest.Count >= 2 && rest[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
    {
        var dll = Path.IsPathRooted(rest[0]) ? rest[0] : Path.Combine(binDir, rest[0]);
        if (Workbench.CheckAssembly(binDir, dll) is { Length: > 0 } err) { Console.Error.WriteLine("dotnet-reflect: " + err); return 2; }
        dllsToScan.Add(dll); symbol = rest[1];
    }
    else
    {
        if (rest.Count < 1) { Console.Error.WriteLine("dotnet-reflect: missing <symbol>."); return 1; }
        symbol = rest[0];
        dllsToScan = Directory.GetFiles(binDir, "*.dll")
            .Where(f => onlyFilters is null || onlyFilters.Any(o => Path.GetFileName(f).Contains(o, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }
    header = "";
}
else
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: find-usages.cs <pkgId> <version> <symbol>   |   --bin <dir> <symbol> [--only a,b]"); return 1; }
    var wb = Workbench.Ensure(args[0], args[1]);
    if (!wb.Ok) { Console.Error.WriteLine("dotnet-reflect: " + wb.Error); return 2; }
    binDir = wb.BinDir; symbol = args[2];
    dllsToScan = wb.Targets.Select(t => t.dll).ToList();
    header = $"// {args[0]} {wb.Version} — usages of \"{symbol}\" within the package's own assemblies:";
}

// Normalize the symbol: drop a leading M:/T:/P:/F: doc prefix and any parameter list.
symbol = symbol.Trim();
if (symbol.Length > 2 && symbol[1] == ':') symbol = symbol[2..];
var paren = symbol.IndexOf('('); if (paren >= 0) symbol = symbol[..paren];

if (dllsToScan.Count == 0) { Console.Error.WriteLine("dotnet-reflect: no assemblies to scan (check --only filter)."); return 2; }
if (header.Length > 0) Console.WriteLine(header);

// ---- scan ------------------------------------------------------------------
var all = new List<Usages.Hit>();
int scanned = 0, pdbless = 0;
var noPdbAsms = new List<string>();
foreach (var dll in dllsToScan)
{
    Usages.Result r;
    try { r = Usages.Scan(binDir, dll, symbol); }
    catch (Exception ex) { Console.WriteLine($"// skipped {Path.GetFileName(dll)} — {ex.GetType().Name}: {ex.Message}"); continue; }
    scanned++;
    all.AddRange(r.Hits);
    if (r.Hits.Count > 0 && !r.HadPdb) { pdbless++; noPdbAsms.Add(Path.GetFileNameWithoutExtension(dll)); }
}

// ---- report ----------------------------------------------------------------
if (all.Count == 0)
{
    Console.WriteLine($"// no usages of \"{symbol}\" found (scanned {scanned} assembly(ies)).");
    Console.WriteLine("// If you expected hits: build the solution first, point --bin at its output, and confirm the name/spelling with find.cs.");
    return 0;
}

var byAsm = all.GroupBy(h => h.Assembly).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
int methodCount = all.Select(h => h.Assembly + "!" + h.Method).Distinct().Count();
Console.WriteLine($"// {all.Count} usage(s) in {methodCount} method(s) across {byAsm.Count()} assembly(ies)   [scanned {scanned}]");
foreach (var g in byAsm)
    foreach (var mg in g.GroupBy(h => h.Method).OrderBy(m => m.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"\n{g.Key}!{mg.Key}");
        foreach (var h in mg.OrderBy(h => h.Line).ThenBy(h => h.Display, StringComparer.Ordinal)
                            .DistinctBy(h => (h.File, h.Line, h.Display)))
        {
            if (h.Line > 0) Console.WriteLine($"  {h.File}:{h.Line}   {h.Source}".TrimEnd());
            else Console.WriteLine($"  {(h.AsmHadPdb ? "(method only)" : "(no PDB)")}   -> {h.Display}");
        }
    }
if (pdbless > 0)
    Console.WriteLine($"\n// NOTE: no portable PDB for {string.Join(", ", noPdbAsms.Take(6))}{(noPdbAsms.Count > 6 ? " …" : "")} — "
                    + "usages are shown at method granularity only. Build those projects in Debug (or with DebugType=portable) for file:line and the source line.");
return 0;


// ===========================================================================
static class Usages
{
    public record Hit(string Assembly, string Method, string Display, string File, int Line, string Source, bool AsmHadPdb);
    public record Result(List<Hit> Hits, bool HadPdb);

    // Opcode -> operand type, built once from System.Reflection.Emit so we can walk raw IL and pick out
    // the metadata-token operands (call/newobj/ldfld/castclass/ldtoken/…) without a full IL library.
    static readonly Dictionary<ushort, OperandType> Ops = BuildOps();
    static Dictionary<ushort, OperandType> BuildOps()
    {
        var d = new Dictionary<ushort, OperandType>();
        foreach (var fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (fi.GetValue(null) is OpCode oc) d[(ushort)oc.Value] = oc.OperandType;
        return d;
    }

    public static Result Scan(string dir, string dll, string symbol)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
        if (!pe.HasMetadata) return new(new(), false);
        var reader = pe.GetMetadataReader();
        var asmName = Path.GetFileNameWithoutExtension(dll);
        var prov = new NameProvider(reader);

        string last = symbol; var dot = symbol.LastIndexOf('.'); if (dot >= 0) last = symbol[(dot + 1)..];

        // Build the set of metadata tokens that, as an IL operand, mean "uses <symbol>".
        var wanted = new Dictionary<int, string>();                 // token -> display
        void Member(EntityHandle h, string decl, string name)
        {
            if (name is ".ctor" or ".cctor") { if (SegMatch(decl, symbol)) wanted[Tok(h)] = "new " + Short(decl); }
            else { var canon = decl + "." + name; if (NameEq(name, last) && SegMatch(canon, symbol)) wanted[Tok(h)] = canon; }
        }
        foreach (var h in reader.MethodDefinitions)
        { var md = reader.GetMethodDefinition(h); Member(h, TypeDefName(reader, md.GetDeclaringType()), reader.GetString(md.Name)); }
        foreach (var h in reader.FieldDefinitions)
        { var fd = reader.GetFieldDefinition(h); var n = reader.GetString(fd.Name); if (NameEq(n, last)) { var c = TypeDefName(reader, fd.GetDeclaringType()) + "." + n; if (SegMatch(c, symbol)) wanted[Tok(h)] = c; } }
        foreach (var h in reader.TypeDefinitions)
        { var n = reader.GetString(reader.GetTypeDefinition(h).Name); if (NameEq(n, last)) { var c = TypeDefName(reader, h); if (SegMatch(c, symbol)) wanted[Tok(h)] = c; } }
        foreach (var h in reader.TypeReferences)
        { var n = reader.GetString(reader.GetTypeReference(h).Name); if (NameEq(n, last)) { var c = TypeRefName(reader, h); if (SegMatch(c, symbol)) wanted[Tok(h)] = c; } }
        foreach (var h in reader.MemberReferences)
        {
            var mr = reader.GetMemberReference(h); var n = reader.GetString(mr.Name);
            if (n is ".ctor" or ".cctor") { var decl = ParentName(reader, prov, mr.Parent); if (SegMatch(decl, symbol)) wanted[Tok(h)] = "new " + Short(decl); }
            else if (NameEq(n, last)) { var c = ParentName(reader, prov, mr.Parent) + "." + n; if (SegMatch(c, symbol)) wanted[Tok(h)] = c; }
        }
        for (int i = 1, c = reader.GetTableRowCount(TableIndex.MethodSpec); i <= c; i++)
        {
            var h = MetadataTokens.MethodSpecificationHandle(i);
            try
            {
                var m = reader.GetMethodSpecification(h).Method; string decl, name;
                if (m.Kind == HandleKind.MethodDefinition) { var md = reader.GetMethodDefinition((MethodDefinitionHandle)m); decl = TypeDefName(reader, md.GetDeclaringType()); name = reader.GetString(md.Name); }
                else { var mr = reader.GetMemberReference((MemberReferenceHandle)m); decl = ParentName(reader, prov, mr.Parent); name = reader.GetString(mr.Name); }
                if (NameEq(name, last)) { var canon = decl + "." + name; if (SegMatch(canon, symbol)) wanted[Tok(h)] = canon; }
            }
            catch { }
        }
        for (int i = 1, c = reader.GetTableRowCount(TableIndex.TypeSpec); i <= c; i++)
        {
            var h = MetadataTokens.TypeSpecificationHandle(i);
            try { var rendered = reader.GetTypeSpecification(h).DecodeSignature(prov, (object?)null); if (SegMatch(rendered, symbol)) wanted[Tok(h)] = rendered; }
            catch { }
        }

        if (wanted.Count == 0) return new(new(), false);

        // Walk each method body's IL; a token operand present in `wanted` is a usage at that offset.
        var raw = new List<(MethodDefinitionHandle m, int offset, string disp)>();
        foreach (var h in reader.MethodDefinitions)
        {
            var md = reader.GetMethodDefinition(h);
            if (md.RelativeVirtualAddress == 0) continue;
            byte[]? il;
            try { il = pe.GetMethodBody(md.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
            if (il is null) continue;
            for (int pos = 0; pos < il.Length;)
            {
                int start = pos; byte b0 = il[pos++]; ushort key = b0;
                if (b0 == 0xFE) { if (pos >= il.Length) break; key = (ushort)(0xFE00 | il[pos++]); }
                if (!Ops.TryGetValue(key, out var ot)) break;       // unknown opcode: stop this body
                switch (ot)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: pos += 1; break;
                    case OperandType.InlineVar: pos += 2; break;
                    case OperandType.ShortInlineR: case OperandType.InlineBrTarget: case OperandType.InlineI:
                    case OperandType.InlineString: case OperandType.InlineSig: pos += 4; break;
                    case OperandType.InlineI8: case OperandType.InlineR: pos += 8; break;
                    case OperandType.InlineSwitch: { if (pos + 4 > il.Length) { pos = il.Length; break; } int n = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(pos)); pos += 4 + 4 * n; break; }
                    case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineType: case OperandType.InlineTok:
                        { if (pos + 4 > il.Length) { pos = il.Length; break; } int tok = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(pos)); pos += 4; if (wanted.TryGetValue(tok, out var disp)) raw.Add((h, start, disp)); break; }
                    default: pos = il.Length; break;                // be safe on anything unexpected
                }
            }
        }
        if (raw.Count == 0) return new(new(), false);

        // Resolve file:line via a portable PDB (embedded, or <asm>.pdb beside the dll) + the source line.
        MetadataReaderProvider? pdbProv = null; MetadataReader? pdb = null;
        try
        {
            foreach (var de in pe.ReadDebugDirectory())
            {
                if (de.Type == DebugDirectoryEntryType.EmbeddedPortablePdb) { pdbProv = pe.ReadEmbeddedPortablePdbDebugDirectoryData(de); pdb = pdbProv.GetMetadataReader(); break; }
                if (de.Type == DebugDirectoryEntryType.CodeView)
                {
                    var beside = Path.Combine(dir, asmName + ".pdb");
                    var cvPath = pe.ReadCodeViewDebugDirectoryData(de).Path;
                    var use = File.Exists(beside) ? beside : (File.Exists(cvPath) ? cvPath : null);
                    if (use != null) { try { pdbProv = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(use)); pdb = pdbProv.GetMetadataReader(); } catch { } }
                }
            }
        }
        catch { }

        var fileCache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<Hit>();
        foreach (var (m, offset, disp) in raw)
        {
            var method = TypeDefName(reader, reader.GetMethodDefinition(m).GetDeclaringType()) + "." + reader.GetString(reader.GetMethodDefinition(m).Name) + "(...)";
            string file = ""; int line = 0; string src = "";
            if (pdb != null)
            {
                try
                {
                    var info = pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(m)));
                    if (!info.Document.IsNil || !info.SequencePointsBlob.IsNil)
                    {
                        SequencePoint? best = null;
                        foreach (var sp in info.GetSequencePoints())
                        { if (sp.IsHidden) continue; if (sp.Offset <= offset && (best is null || sp.Offset >= best.Value.Offset)) best = sp; }
                        if (best is { } bp && !bp.Document.IsNil)
                        {
                            var path = pdb.GetString(pdb.GetDocument(bp.Document).Name);
                            file = Path.GetFileName(path); line = bp.StartLine;
                            src = ReadLine(fileCache, path, bp.StartLine);
                        }
                    }
                }
                catch { }
            }
            hits.Add(new(asmName, method, disp, file, line, src, pdb != null));
        }
        pdbProv?.Dispose();
        return new(hits, pdb != null);
    }

    static string ReadLine(Dictionary<string, string[]?> cache, string path, int line)
    {
        if (!cache.TryGetValue(path, out var lines))
            cache[path] = lines = File.Exists(path) ? File.ReadAllLines(path) : null;
        if (lines is null || line < 1 || line > lines.Length) return "";
        return lines[line - 1].Trim();
    }

    static int Tok(EntityHandle h) => MetadataTokens.GetToken(h);
    static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static string Short(string full) { var i = full.LastIndexOf('.'); return i >= 0 ? full[(i + 1)..] : full; }

    // `needle` matches `hay` at dotted-segment boundaries: the char before must be a non-identifier
    // (so '.', '<', ',', start all qualify — a dotted suffix counts), and the char after must be a
    // non-identifier that isn't '.' (so `Hosting` does NOT match the type `Hosting.IHostEnvironment`).
    static bool SegMatch(string hay, string needle)
    {
        if (string.IsNullOrEmpty(needle) || hay.Length < needle.Length) return false;
        for (int idx = 0; (idx = hay.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0; idx++)
        {
            bool beforeOk = idx == 0 || !IsIdent(hay[idx - 1]);
            int after = idx + needle.Length;
            bool afterOk = after >= hay.Length || (!IsIdent(hay[after]) && hay[after] != '.');
            if (beforeOk && afterOk) return true;
        }
        return false;
    }
    static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    static string TypeDefName(MetadataReader r, TypeDefinitionHandle h)
    {
        var td = r.GetTypeDefinition(h); var name = r.GetString(td.Name);
        if (td.IsNested) return TypeDefName(r, td.GetDeclaringType()) + "." + name;
        var ns = r.GetString(td.Namespace); return ns.Length == 0 ? name : ns + "." + name;
    }
    static string TypeRefName(MetadataReader r, TypeReferenceHandle h)
    {
        var tr = r.GetTypeReference(h); var name = r.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return TypeRefName(r, (TypeReferenceHandle)tr.ResolutionScope) + "." + name;
        var ns = r.GetString(tr.Namespace); return ns.Length == 0 ? name : ns + "." + name;
    }
    static string ParentName(MetadataReader r, NameProvider prov, EntityHandle p) => p.Kind switch
    {
        HandleKind.TypeReference => TypeRefName(r, (TypeReferenceHandle)p),
        HandleKind.TypeDefinition => TypeDefName(r, (TypeDefinitionHandle)p),
        HandleKind.TypeSpecification => Try(() => r.GetTypeSpecification((TypeSpecificationHandle)p).DecodeSignature(prov, (object?)null)),
        HandleKind.MethodDefinition => TypeDefName(r, r.GetMethodDefinition((MethodDefinitionHandle)p).GetDeclaringType()),
        _ => "",
    };
    static string Try(Func<string> f) { try { return f(); } catch { return ""; } }
}

// Renders a signature type to a full-name string (for TypeSpec operands and generic member-ref parents),
// so `List<Ns.IFoo>` / `Ns.IFoo[]` are matchable at segment boundaries by SegMatch.
sealed class NameProvider : ISignatureTypeProvider<string, object?>
{
    readonly MetadataReader _r;
    public NameProvider(MetadataReader r) => _r = r;

    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Byte => "System.Byte", PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Char => "System.Char", PrimitiveTypeCode.Int16 => "System.Int16", PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32", PrimitiveTypeCode.UInt32 => "System.UInt32", PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64", PrimitiveTypeCode.Single => "System.Single", PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.IntPtr => "System.IntPtr", PrimitiveTypeCode.UIntPtr => "System.UIntPtr", PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.String => "System.String", PrimitiveTypeCode.Void => "System.Void", PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => c.ToString(),
    };
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawKind)
    { var td = r.GetTypeDefinition(h); var n = r.GetString(td.Name); if (td.IsNested) return GetTypeFromDefinition(r, td.GetDeclaringType(), 0) + "." + n; var ns = r.GetString(td.Namespace); return ns.Length == 0 ? n : ns + "." + n; }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawKind)
    { var tr = r.GetTypeReference(h); var n = r.GetString(tr.Name); if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return GetTypeFromReference(r, (TypeReferenceHandle)tr.ResolutionScope, 0) + "." + n; var ns = r.GetString(tr.Namespace); return ns.Length == 0 ? n : ns + "." + n; }
    public string GetTypeFromSpecification(MetadataReader r, object? ctx, TypeSpecificationHandle h, byte rawKind) => r.GetTypeSpecification(h).DecodeSignature(this, ctx);
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', Math.Max(0, s.Rank - 1)) + "]";
    public string GetByReferenceType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPinnedType(string e) => e;
    public string GetGenericInstantiation(string generic, ImmutableArray<string> args) => generic + "<" + string.Join(",", args) + ">";
    public string GetGenericMethodParameter(object? ctx, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? ctx, int i) => "!" + i;
    public string GetModifiedType(string modifier, string unmodified, bool isRequired) => unmodified;
    public string GetFunctionPointerType(MethodSignature<string> s) => "method*";
}
