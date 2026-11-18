using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalIdeBridge.Tools;

/// <summary>
/// Provides fast, cancellable text/regex search, project &amp; dependency discovery, and genuine
/// Roslyn C# semantic code intelligence (symbol search, go-to-definition, find-references,
/// find-implementations, type/method inspection, and compiler diagnostics) with bounded LRU caching
/// and explicitly labeled text fallbacks for non-C# languages.
/// </summary>
public sealed class SearchAndIntelTools
{
    private const long MaxSearchFileBytes = 4 * 1024 * 1024;
    private const int MaxRoslynFiles = 400;

    private sealed record CachedSyntaxTree(DateTime LastWriteUtc, long Length, SyntaxTree Tree);

    private readonly AppConfig _config;
    private readonly WorkspaceGuard _guard;
    private readonly ProcessRunner _runner;
    private readonly BoundedLruCache<string, CachedSyntaxTree> _syntaxCache;
    private readonly Lazy<IReadOnlyList<MetadataReference>> _coreReferences;

    public SearchAndIntelTools(AppConfig config, WorkspaceGuard guard, ProcessRunner runner)
    {
        _config = config;
        _guard = guard;
        _runner = runner;
        _syntaxCache = new BoundedLruCache<string, CachedSyntaxTree>(
            config.Resources.MemoryCacheMaxEntries,
            config.Resources.MemoryCacheMaxBytes,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _coreReferences = new Lazy<IReadOnlyList<MetadataReference>>(LoadCoreMetadataReferences, isThreadSafe: true);
    }

    public void InvalidateFile(string fullPath) => _syntaxCache.Invalidate(fullPath);

    public IEnumerable<ToolDefinition> CreateTools()
    {
        yield return new ToolDefinition(
            Name: "text_search",
            Category: "code_intel",
            Description: "Performs fast, streaming literal text search across workspace files. Explicitly reports engine='text-literal'.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("query", "string", "Literal text substring to search for.", Required: true),
                new("filePattern", "string", "Optional wildcard filename filter such as '*.cs' or '*.json'."),
                new("caseSensitive", "boolean", "Match case strictly.", Default: false),
                new("maxResults", "integer", "Maximum matches returned (1-300).", Default: 100)
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var query = args.RequireString("query");
                var filePattern = args.OptionalString("filePattern");
                var caseSensitive = args.OptionalBool("caseSensitive", false);
                var maxResults = Math.Clamp(args.OptionalInt("maxResults", 100), 1, 300);
                var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                var (matches, filesScanned, truncated) = ScanLines(ws, filePattern, maxResults, line =>
                {
                    var idx = line.IndexOf(query, cmp);
                    return idx >= 0 ? idx + 1 : -1;
                }, ct);

                return Task.FromResult(ToolResult.Ok(
                    $"Found {matches.Count} literal match(es) across {filesScanned} file(s){(truncated ? " (truncated)" : "")}.",
                    new { engine = "text-literal", isSemantic = false, query, filesScanned, count = matches.Count, truncated, matches }));
            });

        yield return new ToolDefinition(
            Name: "regex_search",
            Category: "code_intel",
            Description: "Performs timeout-protected streaming regular-expression search across workspace files. Reports engine='text-regex'.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("pattern", "string", ".NET regular expression pattern.", Required: true),
                new("filePattern", "string", "Optional wildcard filename filter such as '*.cs'."),
                new("caseSensitive", "boolean", "Case-sensitive regex evaluation.", Default: false),
                new("maxResults", "integer", "Maximum matches returned (1-300).", Default: 100)
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var pattern = args.RequireString("pattern");
                var filePattern = args.OptionalString("filePattern");
                var caseSensitive = args.OptionalBool("caseSensitive", false);
                var maxResults = Math.Clamp(args.OptionalInt("maxResults", 100), 1, 300);

                var opts = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                Regex rx;
                try
                {
                    rx = new Regex(pattern, opts, TimeSpan.FromMilliseconds(500));
                }
                catch (RegexParseException ex)
                {
                    return Task.FromResult(ToolResult.Fail("invalid_regex", $"Invalid regular expression: {ex.Message}"));
                }

                var (matches, filesScanned, truncated) = ScanLines(ws, filePattern, maxResults, line =>
                {
                    try
                    {
                        var m = rx.Match(line);
                        return m.Success ? m.Index + 1 : -1;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return -1;
                    }
                }, ct);

                return Task.FromResult(ToolResult.Ok(
                    $"Found {matches.Count} regex match(es) across {filesScanned} file(s){(truncated ? " (truncated)" : "")}.",
                    new { engine = "text-regex", isSemantic = false, pattern, filesScanned, count = matches.Count, truncated, matches }));
            });

        yield return new ToolDefinition(
            Name: "project_discover",
            Category: "code_intel",
            Description: "Discovers .NET solutions (.sln/.slnx), MSBuild projects (.csproj/.fsproj/.vbproj), Directory.Build.props, and package manifests in the workspace.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters: [],
            Handler: (_, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var solutions = new List<string>();
                var projects = new List<object>();
                var manifests = new List<string>();

                foreach (var file in WorkspaceTools.EnumerateWorkspaceFiles(ws, _guard, includeIgnored: false, ct))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    var name = Path.GetFileName(file);
                    var rel = _guard.GetRelativePathUnix(ws, file);

                    if (ext is ".sln" or ".slnx")
                    {
                        solutions.Add(rel);
                    }
                    else if (ext is ".csproj" or ".fsproj" or ".vbproj")
                    {
                        var info = ParseMsBuildProjectSummary(file, rel);
                        projects.Add(info);
                    }
                    else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
                    {
                        manifests.Add(rel);
                    }
                }

                return Task.FromResult(ToolResult.Ok(
                    $"Discovered {solutions.Count} solution(s) and {projects.Count} project(s).",
                    new { solutions, projects, manifests }));
            });

        yield return new ToolDefinition(
            Name: "dependency_inspect",
            Category: "code_intel",
            Description: "Inspects NuGet PackageReferences, ProjectReferences, target frameworks, and package.json dependencies for a specific project or the entire workspace.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("projectPath", "string", "Optional relative path to a .csproj/.fsproj or package.json; inspects all discovered projects when omitted.")
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var specific = args.OptionalString("projectPath");
                var targets = new List<string>();

                if (!string.IsNullOrWhiteSpace(specific))
                {
                    var resolved = _guard.ResolvePath(specific);
                    if (!File.Exists(resolved))
                        throw new FileNotFoundException($"Project file not found: {specific}");
                    targets.Add(resolved);
                }
                else
                {
                    foreach (var f in WorkspaceTools.EnumerateWorkspaceFiles(ws, _guard, false, ct))
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext is ".csproj" or ".fsproj" or ".vbproj" ||
                            Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase))
                            targets.Add(f);
                    }
                }

                var reports = new List<object>();
                foreach (var t in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = _guard.GetRelativePathUnix(ws, t);
                    if (Path.GetFileName(t).Equals("package.json", StringComparison.OrdinalIgnoreCase))
                        reports.Add(InspectPackageJson(t, rel));
                    else
                        reports.Add(InspectCsprojDependencies(t, rel));
                }

                return Task.FromResult(ToolResult.Ok(
                    $"Inspected dependencies across {reports.Count} manifest(s).",
                    new { count = reports.Count, projects = reports }));
            });

        yield return new ToolDefinition(
            Name: "symbol_search",
            Category: "code_intel",
            Description: "Searches declared symbols (classes, interfaces, records, structs, enums, methods, properties) using Roslyn for C# files and labeled text fallback for other languages.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("query", "string", "Symbol name or substring to match (empty string lists top symbols).", Required: true),
                new("kind", "string", "Optional filter: any | type | method | property | enum | interface.", EnumValues: ["any", "type", "method", "property", "enum", "interface"], Default: "any"),
                new("maxResults", "integer", "Maximum symbols returned (1-200).", Default: 80)
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var query = args.RequireString("query");
                var kind = args.OptionalString("kind", "any")!.ToLowerInvariant();
                var maxResults = Math.Clamp(args.OptionalInt("maxResults", 80), 1, 200);

                var results = new List<object>();
                var usedRoslyn = false;
                var usedFallback = false;

                if (_config.Integrations.EnableRoslynSemantic)
                {
                    foreach (var (rel, tree) in LoadWorkspaceSyntaxTrees(ws, ct))
                    {
                        usedRoslyn = true;
                        var root = tree.GetRoot(ct);
                        foreach (var decl in root.DescendantNodes())
                        {
                            ct.ThrowIfCancellationRequested();
                            var info = ExtractSyntaxSymbol(decl);
                            if (info is null) continue;
                            if (!MatchesKindFilter(info.Value.Kind, kind)) continue;
                            if (query != "*" && !info.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                            var span = info.Value.IdentifierToken.GetLocation().GetLineSpan();
                            results.Add(new
                            {
                                name = info.Value.Name,
                                kind = info.Value.Kind,
                                container = info.Value.Container,
                                signature = info.Value.Signature,
                                file = rel,
                                line = span.StartLinePosition.Line + 1,
                                column = span.StartLinePosition.Character + 1,
                                engine = "roslyn-syntax",
                                isSemantic = false
                            });
                            if (results.Count >= maxResults) break;
                        }
                        if (results.Count >= maxResults) break;
                    }
                }

                // Fallback declaration scan for non-C# source files if room remains.
                if (results.Count < maxResults)
                {
                    var declRx = new Regex(
                        @"^\s*(?:export\s+)?(?:public\s+|private\s+|async\s+)?(?<kind>class|interface|function|def|struct|enum|type)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
                        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

                    foreach (var file in WorkspaceTools.EnumerateWorkspaceFiles(ws, _guard, false, ct))
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext is not (".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go" or ".rs" or ".java" or ".cpp" or ".h"))
                            continue;
                        if (new FileInfo(file).Length > MaxSearchFileBytes) continue;

                        var rel = _guard.GetRelativePathUnix(ws, file);
                        var lineNo = 0;
                        foreach (var line in File.ReadLines(file))
                        {
                            ct.ThrowIfCancellationRequested();
                            lineNo++;
                            var m = declRx.Match(line);
                            if (!m.Success) continue;
                            var symName = m.Groups["name"].Value;
                            if (query != "*" && !symName.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                            usedFallback = true;
                            results.Add(new
                            {
                                name = symName,
                                kind = m.Groups["kind"].Value,
                                container = (string?)null,
                                signature = line.Trim(),
                                file = rel,
                                line = lineNo,
                                column = m.Groups["name"].Index + 1,
                                engine = "text-fallback",
                                isSemantic = false
                            });
                            if (results.Count >= maxResults) break;
                        }
                        if (results.Count >= maxResults) break;
                    }
                }

                var primaryEngine = usedRoslyn && usedFallback ? "roslyn-syntax+text-fallback" : (usedRoslyn ? "roslyn-syntax" : "text-fallback");
                return Task.FromResult(ToolResult.Ok(
                    $"Found {results.Count} symbol(s) matching '{query}' (engine={primaryEngine}).",
                    new { engine = primaryEngine, query, count = results.Count, symbols = results }));
            });

        yield return new ToolDefinition(
            Name: "go_to_definition",
            Category: "code_intel",
            Description: "Resolves the definition location(s) of a C# symbol using the Roslyn semantic model (either by file+line+column position or by symbolName), with explicit text fallback for non-C#.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("symbolName", "string", "Symbol name to locate when file/line/column are omitted."),
                new("file", "string", "Optional relative C# file path containing a symbol usage."),
                new("line", "integer", "1-based line number of symbol usage (when file is specified)."),
                new("column", "integer", "1-based column number of symbol usage (when file is specified).")
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var file = args.OptionalString("file");
                var symbolName = args.OptionalString("symbolName");
                if (string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(symbolName))
                    throw new ArgumentException("Provide either 'symbolName' or ('file', 'line', 'column').");

                if (!_config.Integrations.EnableRoslynSemantic)
                    return Task.FromResult(ToolResult.Fail("semantic_disabled",
                        "Roslyn semantic analysis is disabled by integrations.enableRoslynSemantic; no semantic compilation was constructed."));
                var (compilation, treeMap) = BuildWorkspaceCompilation(ws, ct);
                if (treeMap.Count > 0)
                {
                    var symbols = ResolveTargetSymbols(compilation, treeMap, ws, file, args.OptionalInt("line", 1), args.OptionalInt("column", 1), symbolName, ct);
                    var defs = new List<object>();

                    foreach (var sym in symbols)
                    {
                        var original = sym.OriginalDefinition;
                        foreach (var loc in original.Locations)
                        {
                            if (!loc.IsInSource || loc.SourceTree is null) continue;
                            var span = loc.GetLineSpan();
                            var rel = _guard.GetRelativePathUnix(ws, loc.SourceTree.FilePath);
                            defs.Add(new
                            {
                                symbol = original.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                                kind = original.Kind.ToString(),
                                containingType = original.ContainingType?.ToDisplayString(),
                                containingNamespace = original.ContainingNamespace?.IsGlobalNamespace == true ? null : original.ContainingNamespace?.ToDisplayString(),
                                file = rel,
                                line = span.StartLinePosition.Line + 1,
                                column = span.StartLinePosition.Character + 1,
                                preview = GetLineText(loc.SourceTree, span.StartLinePosition.Line)
                            });
                        }
                        if (defs.Count == 0 && original.Locations.Any(l => l.IsInMetadata))
                        {
                            defs.Add(new
                            {
                                symbol = original.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                                kind = original.Kind.ToString(),
                                containingType = original.ContainingType?.ToDisplayString(),
                                containingNamespace = original.ContainingNamespace?.ToDisplayString(),
                                file = (string?)null,
                                line = 0,
                                column = 0,
                                preview = $"[external metadata assembly: {original.ContainingAssembly?.Name}]"
                            });
                        }
                    }

                    if (defs.Count > 0)
                    {
                        return Task.FromResult(ToolResult.Ok(
                            $"Resolved {defs.Count} semantic definition location(s).",
                            new { engine = "roslyn-semantic", isSemantic = true, count = defs.Count, definitions = defs }));
                    }
                }

                // Explicit text fallback when target isn't C# or no C# symbol matched.
                if (!string.IsNullOrWhiteSpace(symbolName))
                {
                    var escaped = Regex.Escape(symbolName);
                    var rx = new Regex($@"\b(?:class|interface|struct|record|enum|function|def|type)\s+{escaped}\b", RegexOptions.None, TimeSpan.FromMilliseconds(300));
                    var (fallback, _, _) = ScanLines(ws, null, 20, l => { var m = rx.Match(l); return m.Success ? m.Index + 1 : -1; }, ct);
                    return Task.FromResult(ToolResult.Ok(
                        $"Found {fallback.Count} candidate definition(s) via text fallback.",
                        new { engine = "text-fallback", isSemantic = false, count = fallback.Count, definitions = fallback }));
                }

                return Task.FromResult(ToolResult.Ok("No definition found at specified location.",
                    new { engine = "roslyn-semantic", isSemantic = true, count = 0, definitions = Array.Empty<object>() }));
            });

        yield return new ToolDefinition(
            Name: "find_references",
            Category: "code_intel",
            Description: "Finds semantic references to a C# symbol across the workspace using Roslyn SemanticModel, clearly distinguished from text fallback.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("symbolName", "string", "Symbol name to find references for (when file/line/column are omitted)."),
                new("file", "string", "Optional relative C# file path at symbol position."),
                new("line", "integer", "1-based line number (with file)."),
                new("column", "integer", "1-based column number (with file)."),
                new("maxResults", "integer", "Maximum references returned (1-200).", Default: 100)
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var file = args.OptionalString("file");
                var symbolName = args.OptionalString("symbolName");
                var maxResults = Math.Clamp(args.OptionalInt("maxResults", 100), 1, 200);
                if (string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(symbolName))
                    throw new ArgumentException("Provide either 'symbolName' or ('file', 'line', 'column').");

                if (!_config.Integrations.EnableRoslynSemantic)
                    return Task.FromResult(ToolResult.Fail("semantic_disabled",
                        "Roslyn semantic analysis is disabled by integrations.enableRoslynSemantic; no semantic compilation was constructed."));
                var (compilation, treeMap) = BuildWorkspaceCompilation(ws, ct);
                if (treeMap.Count > 0)
                {
                    var targets = ResolveTargetSymbols(compilation, treeMap, ws, file, args.OptionalInt("line", 1), args.OptionalInt("column", 1), symbolName, ct)
                        .Select(s => s.OriginalDefinition)
                        .Distinct(SymbolEqualityComparer.Default)
                        .ToHashSet(SymbolEqualityComparer.Default);

                    if (targets.Count > 0)
                    {
                        var targetNames = targets.Select(t => t!.Name).ToHashSet(StringComparer.Ordinal);
                        var refs = new List<object>();

                        foreach (var (rel, tree) in treeMap)
                        {
                            ct.ThrowIfCancellationRequested();
                            var text = tree.GetText(ct).ToString();
                            if (!targetNames.Any(n => text.Contains(n, StringComparison.Ordinal)))
                                continue;

                            var model = compilation.GetSemanticModel(tree);
                            var root = tree.GetRoot(ct);

                            foreach (var idNode in root.DescendantNodes().OfType<SimpleNameSyntax>())
                            {
                                ct.ThrowIfCancellationRequested();
                                if (!targetNames.Contains(idNode.Identifier.ValueText))
                                    continue;

                                var info = model.GetSymbolInfo(idNode, ct);
                                var candidate = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault())?.OriginalDefinition;
                                // Match constructors back to containing NamedType as well.
                                var matched = candidate is not null && (
                                    targets.Contains(candidate) ||
                                    (candidate is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor &&
                                     ctor.ContainingType is not null &&
                                     targets.Contains(ctor.ContainingType.OriginalDefinition)));

                                if (!matched) continue;

                                var span = idNode.GetLocation().GetLineSpan();
                                refs.Add(new
                                {
                                    file = rel,
                                    line = span.StartLinePosition.Line + 1,
                                    column = span.StartLinePosition.Character + 1,
                                    symbol = candidate!.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                                    context = GetLineText(tree, span.StartLinePosition.Line)
                                });
                                if (refs.Count >= maxResults) break;
                            }
                            if (refs.Count >= maxResults) break;
                        }

                        return Task.FromResult(ToolResult.Ok(
                            $"Found {refs.Count} semantic reference(s) in C# workspace.",
                            new
                            {
                                engine = "roslyn-semantic",
                                isSemantic = true,
                                targetSymbols = targets.Select(t => t!.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)).ToArray(),
                                count = refs.Count,
                                references = refs
                            }));
                    }
                }

                // Labeled whole-word text fallback for non-C# symbols.
                var fallbackName = symbolName ?? "";
                if (!string.IsNullOrWhiteSpace(fallbackName))
                {
                    var rx = new Regex($@"\b{Regex.Escape(fallbackName)}\b", RegexOptions.None, TimeSpan.FromMilliseconds(300));
                    var (matches, _, truncated) = ScanLines(ws, null, maxResults, l => { var m = rx.Match(l); return m.Success ? m.Index + 1 : -1; }, ct);
                    return Task.FromResult(ToolResult.Ok(
                        $"Found {matches.Count} whole-word text match(es) (non-semantic fallback).",
                        new { engine = "text-fallback", isSemantic = false, count = matches.Count, truncated, references = matches }));
                }

                return Task.FromResult(ToolResult.Ok("No references found.",
                    new { engine = "roslyn-semantic", isSemantic = true, count = 0, references = Array.Empty<object>() }));
            });

        yield return new ToolDefinition(
            Name: "find_implementations",
            Category: "code_intel",
            Description: "Finds concrete types implementing an interface, derived subclasses of a base type, or overrides/implementations of a method/property using Roslyn semantics.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("symbolName", "string", "Interface, base class, or abstract/virtual member name.", Required: true),
                new("maxResults", "integer", "Maximum implementations returned (1-100).", Default: 50)
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var symbolName = args.RequireString("symbolName");
                var maxResults = Math.Clamp(args.OptionalInt("maxResults", 50), 1, 100);

                if (!_config.Integrations.EnableRoslynSemantic)
                    return Task.FromResult(ToolResult.Fail("semantic_disabled",
                        "Roslyn semantic analysis is disabled by integrations.enableRoslynSemantic; no semantic compilation was constructed."));
                var (compilation, treeMap) = BuildWorkspaceCompilation(ws, ct);
                if (treeMap.Count == 0)
                    return Task.FromResult(ToolResult.Ok("No C# source files in workspace for semantic implementation analysis.",
                        new { engine = "none", isSemantic = false, count = 0, implementations = Array.Empty<object>() }));

                var allTypes = EnumerateAllSourceTypes(compilation.GlobalNamespace, ct).ToList();
                var targetTypes = allTypes.Where(t =>
                    string.Equals(t.Name, symbolName, StringComparison.Ordinal) ||
                    string.Equals(t.ToDisplayString(), symbolName, StringComparison.Ordinal)).ToList();

                var impls = new List<object>();

                foreach (var targetType in targetTypes)
                {
                    foreach (var candidate in allTypes)
                    {
                        if (SymbolEqualityComparer.Default.Equals(candidate, targetType)) continue;
                        var implementsIface = targetType.TypeKind == TypeKind.Interface &&
                            candidate.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, targetType.OriginalDefinition));
                        var inheritsClass = targetType.TypeKind == TypeKind.Class && InheritsFrom(candidate, targetType);

                        if (!implementsIface && !inheritsClass) continue;
                        var loc = candidate.Locations.FirstOrDefault(l => l.IsInSource);
                        var span = loc?.GetLineSpan();
                        impls.Add(new
                        {
                            implementation = candidate.ToDisplayString(),
                            kind = candidate.TypeKind.ToString(),
                            isAbstract = candidate.IsAbstract,
                            target = targetType.ToDisplayString(),
                            relationship = implementsIface ? "implements_interface" : "inherits_class",
                            file = loc?.SourceTree is not null ? _guard.GetRelativePathUnix(ws, loc.SourceTree.FilePath) : null,
                            line = span.HasValue ? span.Value.StartLinePosition.Line + 1 : 0,
                            column = span.HasValue ? span.Value.StartLinePosition.Character + 1 : 0
                        });
                        if (impls.Count >= maxResults) break;
                    }
                }

                // Also inspect interface/virtual member implementations if a member name was requested.
                if (impls.Count < maxResults)
                {
                    var targetMembers = allTypes
                        .SelectMany(t => t.GetMembers())
                        .Where(m => m.Kind is SymbolKind.Method or SymbolKind.Property &&
                                    string.Equals(m.Name, symbolName, StringComparison.Ordinal) &&
                                    (m.ContainingType.TypeKind == TypeKind.Interface || m.IsAbstract || m.IsVirtual))
                        .ToList();

                    foreach (var tm in targetMembers)
                    {
                        foreach (var candidateType in allTypes)
                        {
                            if (SymbolEqualityComparer.Default.Equals(candidateType, tm.ContainingType)) continue;
                            ISymbol? implMember = tm.ContainingType.TypeKind == TypeKind.Interface
                                ? candidateType.FindImplementationForInterfaceMember(tm)
                                : candidateType.GetMembers(tm.Name).FirstOrDefault(m => m.IsOverride);

                            if (implMember is null || !SymbolEqualityComparer.Default.Equals(implMember.ContainingType, candidateType))
                                continue;

                            var loc = implMember.Locations.FirstOrDefault(l => l.IsInSource);
                            var span = loc?.GetLineSpan();
                            impls.Add(new
                            {
                                implementation = implMember.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                                kind = implMember.Kind.ToString(),
                                isAbstract = implMember.IsAbstract,
                                target = tm.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                                relationship = tm.ContainingType.TypeKind == TypeKind.Interface ? "implements_member" : "overrides_member",
                                file = loc?.SourceTree is not null ? _guard.GetRelativePathUnix(ws, loc.SourceTree.FilePath) : null,
                                line = span.HasValue ? span.Value.StartLinePosition.Line + 1 : 0,
                                column = span.HasValue ? span.Value.StartLinePosition.Character + 1 : 0
                            });
                            if (impls.Count >= maxResults) break;
                        }
                    }
                }

                return Task.FromResult(ToolResult.Ok(
                    $"Found {impls.Count} semantic implementation(s) for '{symbolName}'.",
                    new { engine = "roslyn-semantic", isSemantic = true, symbolName, count = impls.Count, implementations = impls }));
            });

        yield return new ToolDefinition(
            Name: "inspect_type",
            Category: "code_intel",
            Description: "Inspects a C# type or method using Roslyn, returning namespace, base type, interfaces, constructors, properties, method signatures, and source location.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("typeName", "string", "Simple or fully-qualified C# type name (e.g. 'Calculator' or 'MyApp.Calculator').", Required: true),
                new("memberName", "string", "Optional method or property name on the type for focused signature inspection.")
            ],
            Handler: (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var typeName = args.RequireString("typeName");
                var memberName = args.OptionalString("memberName");

                if (!_config.Integrations.EnableRoslynSemantic)
                    return Task.FromResult(ToolResult.Fail("semantic_disabled",
                        "Roslyn semantic analysis is disabled by integrations.enableRoslynSemantic."));
                var (compilation, _) = BuildWorkspaceCompilation(ws, ct);
                var match = compilation.GetTypeByMetadataName(typeName)
                    ?? EnumerateAllSourceTypes(compilation.GlobalNamespace, ct).FirstOrDefault(t =>
                        string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.ToDisplayString(), typeName, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                    return Task.FromResult(ToolResult.Fail("not_found", $"Type '{typeName}' was not found in workspace C# compilation or core BCL."));

                var members = match.GetMembers()
                    .Where(m => !m.IsImplicitlyDeclared &&
                                (string.IsNullOrWhiteSpace(memberName) || string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase)))
                    .Take(80)
                    .Select(m =>
                    {
                        var mLoc = m.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
                        return new
                        {
                            name = m.Name,
                            kind = m.Kind.ToString(),
                            accessibility = m.DeclaredAccessibility.ToString(),
                            isStatic = m.IsStatic,
                            signature = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            line = mLoc.HasValue ? mLoc.Value.StartLinePosition.Line + 1 : (int?)null
                        };
                    }).ToList();

                var loc = match.Locations.FirstOrDefault(l => l.IsInSource);
                var span = loc?.GetLineSpan();

                return Task.FromResult(ToolResult.Ok(
                    $"Inspected {match.TypeKind} '{match.ToDisplayString()}' ({members.Count} member(s)).",
                    new
                    {
                        engine = "roslyn-semantic",
                        isSemantic = true,
                        fullName = match.ToDisplayString(),
                        name = match.Name,
                        typeKind = match.TypeKind.ToString(),
                        accessibility = match.DeclaredAccessibility.ToString(),
                        isAbstract = match.IsAbstract,
                        isSealed = match.IsSealed,
                        isRecord = match.IsRecord,
                        containingNamespace = match.ContainingNamespace?.IsGlobalNamespace == true ? null : match.ContainingNamespace?.ToDisplayString(),
                        baseType = match.BaseType?.ToDisplayString(),
                        interfaces = match.Interfaces.Select(i => i.ToDisplayString()).ToArray(),
                        file = loc?.SourceTree is not null ? _guard.GetRelativePathUnix(ws, loc.SourceTree.FilePath) : null,
                        line = span.HasValue ? span.Value.StartLinePosition.Line + 1 : (int?)null,
                        members
                    }));
            });

        yield return new ToolDefinition(
            Name: "compiler_diagnostics",
            Category: "code_intel",
            Description: "Collects structured C# compiler errors and warnings either via full MSBuild/dotnet build ('build' mode, includes NuGet packages) or fast in-process Roslyn syntax/semantic check ('roslyn' mode).",
            IsReadOnly: false, // Default build mode writes bin/obj and must obey pause/write locks.
            IsIdempotent: true,
            SideEffects: "Build mode runs dotnet build and writes outputs; roslyn mode analyzes in memory.",
            Parameters:
            [
                new("mode", "string", "build (accurate with NuGet references) | roslyn (fast in-memory).", EnumValues: ["build", "roslyn"], Default: "build"),
                new("target", "string", "Optional relative .sln or .csproj path (for 'build' mode)."),
                new("includeWarnings", "boolean", "Include warning diagnostics in addition to errors.", Default: true)
            ],
            Handler: async (args, _, ct) =>
            {
                var ws = _guard.RequireActiveWorkspace();
                var mode = args.OptionalString("mode", "build")!.ToLowerInvariant();
                var includeWarnings = args.OptionalBool("includeWarnings", true);

                if (mode == "roslyn")
                {
                    if (!_config.Integrations.EnableRoslynSemantic)
                        return ToolResult.Fail("semantic_disabled",
                            "Roslyn semantic compilation is disabled. Use compiler_diagnostics mode='build' instead.");
                    var (comp, treeMap) = BuildWorkspaceCompilation(ws, ct);
                    var diags = comp.GetDiagnostics(ct)
                        .Where(d => d.Severity == DiagnosticSeverity.Error || (includeWarnings && d.Severity == DiagnosticSeverity.Warning))
                        .Take(100)
                        .Select(d =>
                        {
                            var sp = d.Location.GetLineSpan();
                            var file = d.Location.SourceTree is not null
                                ? _guard.GetRelativePathUnix(ws, d.Location.SourceTree.FilePath)
                                : "";
                            return new CompilerDiagnostic(
                                file,
                                sp.StartLinePosition.Line + 1,
                                sp.StartLinePosition.Character + 1,
                                d.Severity.ToString().ToLowerInvariant(),
                                d.Id,
                                d.GetMessage());
                        }).ToList();

                    var errCount = diags.Count(d => d.Severity == "error");
                    var warnCount = diags.Count(d => d.Severity == "warning");
                    return ToolResult.Ok(
                        $"Roslyn analyzed {treeMap.Count} file(s): {errCount} error(s), {warnCount} warning(s).",
                        new
                        {
                            engine = "roslyn-inprocess",
                            note = "In-process Roslyn references core .NET BCL assemblies only; use mode='build' when external NuGet references are involved.",
                            filesAnalyzed = treeMap.Count,
                            errorCount = errCount,
                            warningCount = warnCount,
                            diagnostics = diags
                        });
                }

                var cmdArgs = new List<string> { "build", "--nologo", "-v", "minimal" };
                var target = args.OptionalString("target");
                if (!string.IsNullOrWhiteSpace(target))
                {
                    var fullTarget = _guard.ResolvePath(target);
                    cmdArgs.Insert(1, fullTarget);
                }

                var exec = await _runner.RunAsync("diagnostics", "dotnet", cmdArgs, ws, parseCompilerDiagnostics: true, ct: ct);
                var filtered = includeWarnings
                    ? exec.Diagnostics
                    : exec.Diagnostics.Where(d => d.Severity == "error").ToList();
                var errs = filtered.Count(d => d.Severity == "error");
                var warns = filtered.Count(d => d.Severity == "warning");

                var diagnosticPayload = new
                    {
                        engine = "dotnet-msbuild-roslyn",
                        executionId = exec.ExecutionId,
                        exitCode = exec.ExitCode,
                        durationMs = exec.DurationMs,
                        errorCount = errs,
                        warningCount = warns,
                        diagnostics = filtered
                    };
                var summary = $"Compiler diagnostics complete (exit={exec.ExitCode}): {errs} error(s), {warns} warning(s).";
                return exec.ExitCode == 0
                    ? ToolResult.Ok(summary, diagnosticPayload, exec.ExecutionId)
                    : ToolResult.Fail(exec.TimedOut ? "timeout" : exec.Cancelled ? "cancelled" : "build_failed",
                        summary, diagnosticPayload, exec.ExecutionId);
            });
    }

    private (List<object> Matches, int FilesScanned, bool Truncated) ScanLines(
        string workspaceRoot,
        string? filePattern,
        int maxResults,
        Func<string, int> matcher,
        CancellationToken ct)
    {
        var glob = string.IsNullOrWhiteSpace(filePattern) ? null : WorkspaceTools.PatternToMatcher(filePattern);
        var matches = new List<object>();
        var filesScanned = 0;
        var truncated = false;

        foreach (var file in WorkspaceTools.EnumerateWorkspaceFiles(workspaceRoot, _guard, includeIgnored: false, ct))
        {
            var rel = _guard.GetRelativePathUnix(workspaceRoot, file);
            var name = Path.GetFileName(file);
            if (glob is not null && !glob.IsMatch(name) && !glob.IsMatch(rel))
                continue;
            if (glob is null && !string.IsNullOrWhiteSpace(filePattern) &&
                !name.Contains(filePattern, StringComparison.OrdinalIgnoreCase))
                continue;

            FileInfo fi;
            try { fi = new FileInfo(file); } catch { continue; }
            if (fi.Length == 0 || fi.Length > MaxSearchFileBytes) continue;
            if (IsProbablyBinary(file)) continue;

            filesScanned++;
            var lineNo = 0;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = sr.ReadLine()) is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    lineNo++;
                    if (line.Length > 4000) continue; // Skip minified single-line bundles.
                    var col = matcher(line);
                    if (col < 1) continue;

                    if (matches.Count >= maxResults)
                    {
                        truncated = true;
                        return (matches, filesScanned, truncated);
                    }

                    var snippet = line.Length > 240 ? line[..240] + "..." : line;
                    matches.Add(new { file = rel, line = lineNo, column = col, text = snippet.TrimEnd() });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return (matches, filesScanned, truncated);
    }

    private List<(string RelPath, SyntaxTree Tree)> LoadWorkspaceSyntaxTrees(string ws, CancellationToken ct)
    {
        var trees = new List<(string, SyntaxTree)>();
        foreach (var file in WorkspaceTools.EnumerateWorkspaceFiles(ws, _guard, includeIgnored: false, ct))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            FileInfo fi;
            try { fi = new FileInfo(file); } catch { continue; }
            if (fi.Length > 1024 * 1024) continue;

            var rel = _guard.GetRelativePathUnix(ws, file);
            if (_syntaxCache.TryGet(file, out var cached) &&
                cached.LastWriteUtc == fi.LastWriteTimeUtc &&
                cached.Length == fi.Length)
            {
                trees.Add((rel, cached.Tree));
            }
            else
            {
                var source = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default, path: file, cancellationToken: ct);
                _syntaxCache.Set(file, new CachedSyntaxTree(fi.LastWriteTimeUtc, fi.Length, tree), Math.Max(1024, fi.Length * 2));
                trees.Add((rel, tree));
            }

            if (trees.Count >= MaxRoslynFiles) break;
        }
        return trees;
    }

    private (CSharpCompilation Compilation, List<(string RelPath, SyntaxTree Tree)> Trees) BuildWorkspaceCompilation(string ws, CancellationToken ct)
    {
        var trees = LoadWorkspaceSyntaxTrees(ws, ct);
        // Synthesize standard .NET implicit global usings so modern C# files resolve cleanly.
        var implicitUsings = CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Threading;
            global using System.Threading.Tasks;
            """, path: "__ImplicitUsings.g.cs", cancellationToken: ct);

        var compilation = CSharpCompilation.Create(
            assemblyName: "WorkspaceAnalysis",
            syntaxTrees: trees.Select(t => t.Tree).Append(implicitUsings),
            references: _coreReferences.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return (compilation, trees);
    }

    private List<ISymbol> ResolveTargetSymbols(
        CSharpCompilation compilation,
        List<(string RelPath, SyntaxTree Tree)> trees,
        string ws,
        string? file,
        int line,
        int column,
        string? symbolName,
        CancellationToken ct)
    {
        var found = new List<ISymbol>();

        if (!string.IsNullOrWhiteSpace(file))
        {
            var full = _guard.ResolvePath(file);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var entry = trees.FirstOrDefault(t => string.Equals(t.Tree.FilePath, full, cmp));
            if (entry.Tree is not null)
            {
                var text = entry.Tree.GetText(ct);
                var lineIdx = Math.Clamp(line - 1, 0, Math.Max(0, text.Lines.Count - 1));
                var textLine = text.Lines[lineIdx];
                var colIdx = Math.Clamp(column - 1, 0, Math.Max(0, textLine.Span.Length));
                var pos = textLine.Start + colIdx;

                var root = entry.Tree.GetRoot(ct);
                var token = root.FindToken(pos);
                var node = token.Parent;
                var model = compilation.GetSemanticModel(entry.Tree);

                if (node is not null)
                {
                    var decl = model.GetDeclaredSymbol(node, ct);
                    if (decl is not null) found.Add(decl);
                    else
                    {
                        var info = model.GetSymbolInfo(node, ct);
                        if (info.Symbol is not null) found.Add(info.Symbol);
                        else found.AddRange(info.CandidateSymbols);
                    }
                }
            }
        }

        if (found.Count == 0 && !string.IsNullOrWhiteSpace(symbolName))
        {
            foreach (var s in compilation.GetSymbolsWithName(
                         n => string.Equals(n, symbolName, StringComparison.Ordinal),
                         SymbolFilter.TypeAndMember, ct))
            {
                found.Add(s);
            }

            if (found.Count == 0)
            {
                var byMeta = compilation.GetTypeByMetadataName(symbolName);
                if (byMeta is not null) found.Add(byMeta);
            }
        }

        return found;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateAllSourceTypes(INamespaceSymbol ns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var t in ns.GetTypeMembers())
        {
            if (t.Locations.Any(l => l.IsInSource))
            {
                yield return t;
                foreach (var nested in t.GetTypeMembers())
                    yield return nested;
            }
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var t in EnumerateAllSourceTypes(child, ct))
                yield return t;
        }
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol targetBase)
    {
        var cur = type.BaseType;
        while (cur is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(cur.OriginalDefinition, targetBase.OriginalDefinition))
                return true;
            cur = cur.BaseType;
        }
        return false;
    }

    private static string GetLineText(SyntaxTree tree, int zeroBasedLine)
    {
        var lines = tree.GetText().Lines;
        if (zeroBasedLine < 0 || zeroBasedLine >= lines.Count) return "";
        var s = lines[zeroBasedLine].ToString().Trim();
        return s.Length > 200 ? s[..200] + "..." : s;
    }

    private static (string Name, string Kind, string? Container, string Signature, SyntaxToken IdentifierToken)? ExtractSyntaxSymbol(SyntaxNode node)
    {
        string? ContainerOf(SyntaxNode n) =>
            n.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

        return node switch
        {
            ClassDeclarationSyntax c => (c.Identifier.ValueText, "class", ContainerOf(c), $"class {c.Identifier.ValueText}{c.TypeParameterList}", c.Identifier),
            InterfaceDeclarationSyntax i => (i.Identifier.ValueText, "interface", ContainerOf(i), $"interface {i.Identifier.ValueText}{i.TypeParameterList}", i.Identifier),
            RecordDeclarationSyntax r => (r.Identifier.ValueText, "record", ContainerOf(r), $"record {r.Identifier.ValueText}{r.ParameterList}", r.Identifier),
            StructDeclarationSyntax s => (s.Identifier.ValueText, "struct", ContainerOf(s), $"struct {s.Identifier.ValueText}", s.Identifier),
            EnumDeclarationSyntax e => (e.Identifier.ValueText, "enum", ContainerOf(e), $"enum {e.Identifier.ValueText}", e.Identifier),
            MethodDeclarationSyntax m => (m.Identifier.ValueText, "method", ContainerOf(m), $"{m.ReturnType} {m.Identifier.ValueText}{m.TypeParameterList}{m.ParameterList}", m.Identifier),
            PropertyDeclarationSyntax p => (p.Identifier.ValueText, "property", ContainerOf(p), $"{p.Type} {p.Identifier.ValueText}", p.Identifier),
            _ => null
        };
    }

    private static bool MatchesKindFilter(string symbolKind, string filter) => filter switch
    {
        "any" => true,
        "type" => symbolKind is "class" or "interface" or "record" or "struct" or "enum",
        "interface" => symbolKind == "interface",
        "enum" => symbolKind == "enum",
        "method" => symbolKind == "method",
        "property" => symbolKind == "property",
        _ => true
    };

    private static IReadOnlyList<MetadataReference> LoadCoreMetadataReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !seen.Add(path)) return;
            try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
        }

        // Works for normal framework-dependent execution via TRUSTED_PLATFORM_ASSEMBLIES.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && !string.IsNullOrEmpty(tpa))
        {
            var wanted = new[]
            {
                "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll",
                "System.Linq.dll", "System.Console.dll", "System.IO.dll", "System.Text.Json.dll",
                "System.Text.RegularExpressions.dll", "System.Net.Http.dll", "System.Threading.dll",
                "System.Threading.Tasks.dll", "netstandard.dll"
            };
            foreach (var p in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (wanted.Any(w => p.EndsWith(w, StringComparison.OrdinalIgnoreCase)))
                    TryAddFile(p);
            }
        }

        // Fallback for self-contained single-file publish: locate installed .NET shared framework on the PC.
        if (refs.Count == 0)
        {
            var dotnetRoots = new[]
            {
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                OperatingSystem.IsWindows() ? @"C:\Program Files\dotnet" : "/usr/share/dotnet",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet")
            };
            foreach (var root in dotnetRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var sharedCore = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(sharedCore)) continue;
                var latest = Directory.GetDirectories(sharedCore).OrderByDescending(d => d).FirstOrDefault();
                if (latest is null) continue;
                foreach (var dll in new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll", "System.Console.dll", "netstandard.dll" })
                    TryAddFile(Path.Combine(latest, dll));
                if (refs.Count > 0) break;
            }
        }

        return refs;
    }

    private static object ParseMsBuildProjectSummary(string fullPath, string relPath)
    {
        try
        {
            var doc = XDocument.Load(fullPath);
            var sdk = doc.Root?.Attribute("Sdk")?.Value;
            var tfm = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")?.Value;
            var outputType = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value;
            var isTest = doc.Descendants().Any(e =>
                (e.Name.LocalName == "IsTestProject" && e.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                (e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) == true));
            return new { path = relPath, sdk, targetFramework = tfm, outputType, isTestProject = isTest };
        }
        catch (Exception ex)
        {
            return new { path = relPath, parseError = ex.Message };
        }
    }

    private static object InspectCsprojDependencies(string fullPath, string relPath)
    {
        try
        {
            var doc = XDocument.Load(fullPath);
            var tfm = doc.Descendants().FirstOrDefault(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")?.Value;
            var packages = doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => new
                {
                    name = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "",
                    version = e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value
                })
                .Where(p => !string.IsNullOrEmpty(p.name))
                .ToList();

            var projRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            return new { path = relPath, type = "msbuild", targetFramework = tfm, packageReferences = packages, projectReferences = projRefs };
        }
        catch (Exception ex)
        {
            return new { path = relPath, type = "msbuild", error = ex.Message };
        }
    }

    private static object InspectPackageJson(string fullPath, string relPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fullPath));
            var root = doc.RootElement;
            static Dictionary<string, string> ReadDeps(JsonElement r, string prop)
            {
                var d = new Dictionary<string, string>();
                if (r.TryGetProperty(prop, out var obj) && obj.ValueKind == JsonValueKind.Object)
                    foreach (var p in obj.EnumerateObject()) d[p.Name] = p.Value.ToString();
                return d;
            }
            return new
            {
                path = relPath,
                type = "npm",
                name = root.TryGetProperty("name", out var n) ? n.GetString() : null,
                dependencies = ReadDeps(root, "dependencies"),
                devDependencies = ReadDeps(root, "devDependencies")
            };
        }
        catch (Exception ex)
        {
            return new { path = relPath, type = "npm", error = ex.Message };
        }
    }

    private static bool IsProbablyBinary(string path)
    {
        try
        {
            Span<byte> buf = stackalloc byte[512];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var n = fs.Read(buf);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF)))
                return false; // UTF-16 BOM text file
            for (var i = 0; i < n; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return true; }
    }
}
