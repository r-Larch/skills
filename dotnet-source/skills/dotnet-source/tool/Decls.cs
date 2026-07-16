// Decls.cs — syntax-only declaration extraction. No Compilation, no reference resolution,
// no semantics: this is what lets Tier 1 work on a solution that doesn't build, and see
// private/internal members that a metadata reader (dotnet-reflect) structurally cannot.
//
// Signatures are rendered from SYNTAX, so a type name reads exactly as written in the source
// (`var`, aliases and usings are not resolved). That's the right trade for navigation.

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DotnetSource;

enum DeclKind
{
    Class, Struct, Record, Interface, Enum, Delegate,
    Method, Ctor, Property, Field, Event, Indexer, Operator, EnumMember,
}

sealed record Decl(
    DeclKind Kind,
    string Name,
    string Fqn,
    string Container,      // owning type FQN (members) or namespace (types)
    string Signature,
    string Modifiers,
    string File,
    int Line,
    int EndLine,
    string Project)
{
    // Computed — derived from the stored fields, so keep them out of the index file.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsType => Kind is DeclKind.Class or DeclKind.Struct or DeclKind.Record
                              or DeclKind.Interface or DeclKind.Enum or DeclKind.Delegate;

    [System.Text.Json.Serialization.JsonIgnore]
    public int Loc => Math.Max(1, EndLine - Line + 1);

    [System.Text.Json.Serialization.JsonIgnore]
    public string KindWord => Kind switch
    {
        DeclKind.Class => "class",
        DeclKind.Struct => "struct",
        DeclKind.Record => "record",
        DeclKind.Interface => "interface",
        DeclKind.Enum => "enum",
        DeclKind.Delegate => "delegate",
        DeclKind.Method => "method",
        DeclKind.Ctor => "ctor",
        DeclKind.Property => "prop",
        DeclKind.Field => "field",
        DeclKind.Event => "event",
        DeclKind.Indexer => "indexer",
        DeclKind.Operator => "operator",
        DeclKind.EnumMember => "enum-member",
        _ => "?",
    };
}

static class Parser
{
    static readonly CSharpParseOptions Options =
        new CSharpParseOptions(LanguageVersion.Preview).WithDocumentationMode(DocumentationMode.None);

    /// <summary>Parse the whole file set in parallel. Broken files still yield their good parts.</summary>
    public static List<Decl> ParseAll(SolutionSet set, Action<string, List<Decl>>? onFile = null)
    {
        var bag = new ConcurrentBag<List<Decl>>();
        var work = set.Projects.SelectMany(p => p.Files.Select(f => (proj: p, file: f))).ToList();

        Parallel.ForEach(work, w =>
        {
            var decls = ParseFile(w.file, w.proj.Name);
            onFile?.Invoke(w.file, decls);
            if (decls.Count > 0) bag.Add(decls);
        });

        return bag.SelectMany(x => x).ToList();
    }

    public static List<Decl> ParseFile(string path, string project)
    {
        string text;
        try { text = File.ReadAllText(path); } catch { return []; }
        return ParseText(text, path, project);
    }

    public static List<Decl> ParseText(string text, string path, string project)
    {
        // Roslyn parses broken code into a best-effort tree — that's the point of Tier 1.
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(text), Options, path);
        var walker = new DeclWalker(tree, path, project);
        walker.Visit(tree.GetRoot());
        return walker.Decls;
    }
}

sealed class DeclWalker(SyntaxTree tree, string file, string project) : CSharpSyntaxWalker
{
    public List<Decl> Decls { get; } = [];

    readonly Stack<string> _scope = new();      // namespaces + containing types
    string Current => string.Join('.', _scope.Reverse());

    string Qualify(string name) => _scope.Count == 0 ? name : $"{Current}.{name}";

    void Add(DeclKind kind, string name, string signature, SyntaxTokenList mods, SyntaxNode node)
    {
        var span = tree.GetLineSpan(node.Span);
        Decls.Add(new Decl(
            Kind: kind,
            Name: name,
            Fqn: Qualify(name),
            Container: Current,
            Signature: signature,
            Modifiers: string.Join(' ', mods.Select(m => m.ValueText)),
            File: file,
            Line: span.StartLinePosition.Line + 1,
            EndLine: span.EndLinePosition.Line + 1,
            Project: project));
    }

    // ---- namespaces ----

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        _scope.Push(node.Name.ToString());
        base.VisitNamespaceDeclaration(node);
        _scope.Pop();
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        _scope.Push(node.Name.ToString());
        base.VisitFileScopedNamespaceDeclaration(node);
        // Deliberately not popped: a file-scoped namespace covers the rest of the file.
    }

    // ---- types ----

    public override void VisitClassDeclaration(ClassDeclarationSyntax node) => Type(DeclKind.Class, node, base.VisitClassDeclaration);
    public override void VisitStructDeclaration(StructDeclarationSyntax node) => Type(DeclKind.Struct, node, base.VisitStructDeclaration);
    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Type(DeclKind.Interface, node, base.VisitInterfaceDeclaration);
    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => Type(DeclKind.Record, node, base.VisitRecordDeclaration);

    void Type<T>(DeclKind kind, T node, Action<T> descend) where T : TypeDeclarationSyntax
    {
        var name = node.Identifier.ValueText + Arity(node.TypeParameterList);
        var bases = node.BaseList is null ? "" : " : " + string.Join(", ", node.BaseList.Types.Select(t => t.ToString()));
        Add(kind, name, $"{Word(kind)} {name}{bases}", node.Modifiers, node);

        _scope.Push(name);
        descend(node);
        _scope.Pop();
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var name = node.Identifier.ValueText;
        Add(DeclKind.Enum, name, $"enum {name}", node.Modifiers, node);
        _scope.Push(name);
        base.VisitEnumDeclaration(node);
        _scope.Pop();
    }

    public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) =>
        Add(DeclKind.EnumMember, node.Identifier.ValueText,
            node.Identifier.ValueText + (node.EqualsValue is null ? "" : " " + node.EqualsValue), node.Modifiers, node);

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var name = node.Identifier.ValueText + Arity(node.TypeParameterList);
        Add(DeclKind.Delegate, name, $"delegate {node.ReturnType} {name}{Params(node.ParameterList)}", node.Modifiers, node);
    }

    // ---- members ----

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var name = node.Identifier.ValueText + Arity(node.TypeParameterList);
        Add(DeclKind.Method, name, $"{node.ReturnType} {name}{Params(node.ParameterList)}", node.Modifiers, node);
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
        Add(DeclKind.Ctor, node.Identifier.ValueText,
            $"{node.Identifier.ValueText}{Params(node.ParameterList)}", node.Modifiers, node);

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
        Add(DeclKind.Property, node.Identifier.ValueText,
            $"{node.Type} {node.Identifier.ValueText} {Accessors(node.AccessorList)}", node.Modifiers, node);

    public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node) =>
        Add(DeclKind.Indexer, "this[]", $"{node.Type} this{Brackets(node.ParameterList)} {Accessors(node.AccessorList)}",
            node.Modifiers, node);

    public override void VisitEventDeclaration(EventDeclarationSyntax node) =>
        Add(DeclKind.Event, node.Identifier.ValueText, $"event {node.Type} {node.Identifier.ValueText}", node.Modifiers, node);

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        foreach (var v in node.Declaration.Variables)
            Add(DeclKind.Event, v.Identifier.ValueText, $"event {node.Declaration.Type} {v.Identifier.ValueText}",
                node.Modifiers, node);
    }

    // One Decl per declarator: `int a, b;` is two fields.
    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (var v in node.Declaration.Variables)
            Add(DeclKind.Field, v.Identifier.ValueText, $"{node.Declaration.Type} {v.Identifier.ValueText}",
                node.Modifiers, node);
    }

    public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node) =>
        Add(DeclKind.Operator, $"operator {node.OperatorToken.ValueText}",
            $"{node.ReturnType} operator {node.OperatorToken.ValueText}{Params(node.ParameterList)}", node.Modifiers, node);

    public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) =>
        Add(DeclKind.Operator, $"operator {node.Type}",
            $"{node.ImplicitOrExplicitKeyword} operator {node.Type}{Params(node.ParameterList)}", node.Modifiers, node);

    // ---- rendering helpers ----

    static string Word(DeclKind k) => k switch
    {
        DeclKind.Class => "class",
        DeclKind.Struct => "struct",
        DeclKind.Record => "record",
        DeclKind.Interface => "interface",
        _ => "type",
    };

    static string Arity(TypeParameterListSyntax? tp) =>
        tp is null || tp.Parameters.Count == 0 ? "" : $"<{string.Join(",", tp.Parameters.Select(p => p.Identifier.ValueText))}>";

    static string Params(BaseParameterListSyntax? pl) =>
        pl is null ? "()" : "(" + string.Join(", ", pl.Parameters.Select(RenderParam)) + ")";

    static string Brackets(BaseParameterListSyntax? pl) =>
        pl is null ? "[]" : "[" + string.Join(", ", pl.Parameters.Select(RenderParam)) + "]";

    static string RenderParam(ParameterSyntax p)
    {
        var mods = p.Modifiers.Count > 0 ? string.Join(' ', p.Modifiers.Select(m => m.ValueText)) + " " : "";
        var def = p.Default is null ? "" : " " + p.Default;
        return $"{mods}{p.Type} {p.Identifier.ValueText}{def}".Trim();
    }

    static string Accessors(AccessorListSyntax? al) =>
        al is null ? "" : "{ " + string.Join(" ", al.Accessors.Select(a =>
            (a.Modifiers.Count > 0 ? string.Join(' ', a.Modifiers.Select(m => m.ValueText)) + " " : "")
            + a.Keyword.ValueText + ";")) + " }";
}
