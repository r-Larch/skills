# dotnet-reflect

A **Claude Code plugin** that inspects the API surface of any .NET **NuGet package or assembly** —
read straight from the DLL on disk, so it's exact for the version you actually have, not whatever
version the web docs happen to show.

It answers the questions you hit when using an unfamiliar package: *what's this type called, what's
the real signature, which overload, is this nullable, what changed between versions, what does this
method actually do?*

## What it does

Six one-command scripts. Each takes a **package id + version** and builds everything it needs itself
(no DLL/dependency/XML hunting):

| Command | Answers |
|---|---|
| `find` | "what is it called / where does it live" — locate types & members by name |
| `surface` | exact signatures + XML-doc summaries (the default digest) |
| `decompile` | real C# **with method bodies** — the behavior signatures can't show |
| `diff` | API changelog between two versions (added/removed types & members) |
| `cache` | where a package lives in the local NuGet cache, its versions & files |
| `bindir` | build a reusable workbench bin folder and print its path |

Signatures are **metadata-accurate**: nullable value **and** reference types (`HttpStatusCode?`,
`string?`, `Func<HttpContext,String?>?`), `required` members, `[SetsRequiredMembers]`, `[Obsolete]`,
`static`/`virtual`/`override`, and a C#-style header with directly-declared interfaces
(`class X : Base, IFoo`). Metapackages (e.g. `OpenIddict.AspNetCore`) expand to the real assemblies
they expose.

## Install

```
/plugin marketplace add <owner>/<repo>
/plugin install dotnet-reflect@dotnet-tools
```

Requires the **.NET 10 SDK** (`dotnet --version` ≥ 10) on the machine — the scripts are file-based
C# apps. Works on Windows, macOS, and Linux.

Once installed, Claude invokes it automatically when you're working with an unfamiliar .NET package.
You can also drive the scripts directly:

```bash
# from the plugin's scripts directory ($CLAUDE_PLUGIN_ROOT/skills/dotnet-reflect/scripts)
dotnet run find.cs      OpenAI 2.12.0 Streaming
dotnet run surface.cs   OpenAI 2.12.0 Chat.ChatClient
dotnet run decompile.cs OpenAI 2.12.0 OpenAI.Chat.ChatClient
dotnet run diff.cs      OpenAI 2.11.0 2.12.0
```

`version` may be the literal `latest`. Use `surface.cs --inherited` to also list base-type members.

## How it works

- **Load-only reflection** (`System.Reflection.MetadataLoadContext`) reads metadata without executing
  the package; the shared-framework directories are added to the resolver so web/framework base types
  resolve.
- **In-process decompilation** (`ICSharpCode.Decompiler`) — no global tools required.
- The **workbench** is a throwaway project built once per (package, version) under your temp dir and
  reused, so repeated queries are fast.

## Private / authenticated feeds

Packages on a private feed (e.g. GitHub Packages) work via your `nuget.config`. Point the scripts at
it with the `NUGET_API_CONFIG` env var (or run from inside a repo that has one); credentials
referenced as `%ENV_VAR%` expand from the environment at restore time. See the skill's `SKILL.md` for
details.

## Layout

```
.claude-plugin/{plugin.json, marketplace.json}   # plugin + self-hosted marketplace
skills/dotnet-reflect/
  SKILL.md                                        # instructions Claude follows
  scripts/{common,reflect}.cs                     # shared helpers (#:include'd)
  scripts/{find,surface,decompile,diff,cache,bindir}.cs
```

## License

MIT (adjust to taste).
