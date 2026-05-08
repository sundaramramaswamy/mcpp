using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mcpp.Indexing;

namespace Mcpp.Tools;

/// <summary>
/// MCP tools for C++ semantic analysis — callers, references, definitions.
/// Queries the XrefDatabase (SQLite) built by CppIndexer via ClangXref.dll.
/// </summary>
[McpServerToolType]
public sealed class CppSemanticTools
{
    private readonly XrefDatabase _xrefDb;
    private readonly CppIndexer _cppIndexer;
    private CallGraphSearch? _callGraphSearch;

    public CppSemanticTools(XrefDatabase xrefDb, CppIndexer cppIndexer)
    {
        _xrefDb = xrefDb;
        _cppIndexer = cppIndexer;
    }

    private CallGraphSearch GetCallGraphSearch()
    {
        if (_callGraphSearch != null)
            return _callGraphSearch;

        if (!_xrefDb.IsCallGraphLoaded)
            throw new InvalidOperationException("Call graph not loaded");

        _callGraphSearch = new CallGraphSearch(
            _xrefDb.ForwardGraph!, _xrefDb.BackwardGraph!,
            _xrefDb.FanInCount!, _xrefDb);
        return _callGraphSearch;
    }

    [McpServerTool(Name = "find_callers", ReadOnly = true,
     Title = "Find all callers of a C++ function or method"),
     Description("Find all functions/methods that directly call the given symbol (1-hop incoming). " +
                 "For multi-hop reachability ('can A eventually reach B?'), use find_call_path. " +
                 "For transitive blast-radius ('what's affected if I change this?'), use get_impact_radius. " +
                 "Example: find_callers('HashGrid::query') or find_callers('Scene::update')")]
    public string FindCallers(string symbol)
    {
        if (!CheckReady(out var msg, symbol)) return msg;

        var callers = _xrefDb.FindCallers(symbol);
        if (callers.Count == 0)
            return TryFuzzyFallback(symbol, "callers");

        var sb = new StringBuilder();
        sb.AppendLine($"## Callers of '{symbol}' ({callers.Count}):");
        sb.AppendLine();

        var byFile = callers.GroupBy(c => c.File).OrderBy(g => g.Key);
        foreach (var group in byFile)
        {
            sb.AppendLine($"### `{group.Key}`");
            foreach (var call in group.OrderBy(c => c.Line))
            {
                var callerName = call.CallerQualifiedName ?? call.CallerName ?? call.CallerUsr;
                sb.AppendLine($"  - line {call.Line}: `{callerName}`");
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "find_callees", ReadOnly = true,
     Title = "Find all functions called by a C++ function or method"),
     Description("Find all functions/methods that the given symbol directly calls (1-hop outgoing). " +
                 "Shows what a function does internally — its direct dependencies. " +
                 "For multi-hop paths between two functions, use find_call_path. " +
                 "Example: find_callees('main') or find_callees('Scene::update')")]
    public string FindCallees(string symbol)
    {
        if (!CheckReady(out var msg, symbol)) return msg;

        var callees = _xrefDb.FindCallees(symbol);
        if (callees.Count == 0)
            return TryFuzzyFallback(symbol, "callees");

        var sb = new StringBuilder();
        sb.AppendLine($"## Functions called by '{symbol}' ({callees.Count}):");
        sb.AppendLine();

        var byFile = callees.GroupBy(c => c.File).OrderBy(g => g.Key);
        foreach (var group in byFile)
        {
            sb.AppendLine($"### `{group.Key}`");
            foreach (var call in group.OrderBy(c => c.Line))
            {
                var calleeName = call.CalleeName ?? call.CalleeUsr;
                sb.AppendLine($"  - line {call.Line}: `{calleeName}`");
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "find_cpp_references", ReadOnly = true,
     Title = "Find all references to a C++ symbol"),
     Description("Find all usage sites of a C++ symbol (function, class, variable, macro) across the codebase. " +
                 "Includes call sites, macro expansions, and #include directives. " +
                 "For finding just the callers of a function (structured call graph), use find_callers. " +
                 "For finding where a symbol is defined, use get_cpp_definition. " +
                 "Example: find_cpp_references('HashGrid') or find_cpp_references('MAX_OBJECTS')")]
    public string FindCppReferences(string symbol)
    {
        if (!CheckReady(out var msg, symbol)) return msg;

        var refs = _xrefDb.FindReferences(symbol);
        if (refs.Count == 0)
            return TryFuzzyFallback(symbol, "references");

        var sb = new StringBuilder();
        sb.AppendLine($"## References to '{symbol}' ({refs.Count}):");
        sb.AppendLine();

        var byFile = refs.GroupBy(r => r.File).OrderBy(g => g.Key);
        foreach (var group in byFile)
        {
            var lines = string.Join(", ", group.Select(r => r.Line).Distinct().OrderBy(l => l));
            var kinds = string.Join(", ", group.Select(r => r.RefKind).Distinct());
            sb.AppendLine($"  - `{group.Key}` (lines: {lines}) [{kinds}]");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_cpp_definition", ReadOnly = true,
     Title = "Find the definition of a C++ symbol"),
     Description("Find where a C++ symbol is defined (class, function, method, variable, enum, macro, etc.). " +
                 "Returns the file, line, kind, and signature. Also finds #define macro definitions. " +
                 "Example: get_cpp_definition('HashGrid') or get_cpp_definition('MAX_OBJECTS')")]
    public string GetCppDefinition(string symbol)
    {
        if (!CheckReady(out var msg, symbol)) return msg;

        var symbols = _xrefDb.FindSymbol(symbol);
        if (symbols.Count == 0)
            return TryFuzzyFallback(symbol, "definition");

        var sb = new StringBuilder();
        sb.AppendLine($"## Definition of '{symbol}':");
        sb.AppendLine();

        foreach (var sym in symbols)
        {
            var displayName = sym.QualifiedName ?? sym.Name;
            sb.AppendLine($"### `{displayName}` ({sym.Kind})");
            sb.AppendLine($"  - File: `{sym.File}` (line {sym.Line})");
            if (sym.Signature != null)
                sb.AppendLine($"  - Signature: `{sym.Signature}`");
            if (sym.ParentUsr != null)
            {
                var parents = _xrefDb.FindSymbol(sym.ParentUsr);
                if (parents.Count > 0)
                    sb.AppendLine($"  - Parent: `{parents[0].QualifiedName ?? parents[0].Name}` ({parents[0].Kind})");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_class_members", ReadOnly = true,
     Title = "List all members of a C++ class or struct"),
     Description("List all methods, fields, and nested types of a C++ class or struct. " +
                 "Shows member names, kinds, signatures, and source locations. " +
                 "For member-level caller counts and dead code analysis, use get_symbol_stats instead. " +
                 "Example: get_class_members('HashGrid') or get_class_members('StaticScene')")]
    public string GetClassMembers(string className)
    {
        if (!CheckReady(out var msg, className)) return msg;

        var members = _xrefDb.GetMembers(className);
        if (members.Count == 0)
        {
            // Try finding the class first to give a better error
            var classSym = _xrefDb.FindSymbol(className);
            if (classSym.Count == 0)
                return TryFuzzyFallback(className, "class members");
            return $"Class '{className}' found but has no indexed members.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Members of '{className}' ({members.Count}):");
        sb.AppendLine();

        var byKind = members.GroupBy(m => m.Kind).OrderBy(g => g.Key);
        foreach (var group in byKind)
        {
            sb.AppendLine($"### {group.Key}s:");
            foreach (var member in group.OrderBy(m => m.Name))
            {
                var sig = member.Signature != null ? $" — `{member.Signature}`" : "";
                sb.AppendLine($"  - `{member.Name}`{sig}  (`{member.File}:{member.Line}`)");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_symbol_stats", ReadOnly = true,
     Title = "Get call graph statistics for a C++ class"),
     Description("Per-member fan-in (caller count) for every member of a class. " +
                 "Answers: 'Is this a god-class?', 'Which methods are dead code (0 callers)?', " +
                 "'Which methods are hotspots (many callers)?' " +
                 "For just listing members without stats, use get_class_members. " +
                 "Example: get_symbol_stats('HashGrid')")]
    public string GetSymbolStats(string className)
    {
        if (!CheckReady(out var msg, className)) return msg;

        var members = _xrefDb.GetMemberStats(className);
        if (members.Count == 0)
        {
            var classSym = _xrefDb.FindSymbol(className);
            if (classSym.Count == 0)
                return TryFuzzyFallback(className, "symbol stats");
            return $"Class '{className}' found but has no indexed members.";
        }

        var sb = new StringBuilder();
        var totalMembers = members.Count;
        var totalCallers = members.Sum(m => m.FanIn);
        var deadCount = members.Count(m => m.FanIn == 0);
        var methods = members.Where(m => m.Kind is "method" or "function" or "static-method").ToList();

        sb.AppendLine($"## Symbol stats: `{className}` ({totalMembers} members, " +
            $"{totalCallers} total callers, {deadCount} uncalled)");
        sb.AppendLine();

        foreach (var m in members)
        {
            var display = m.QualifiedName ?? m.Name;
            var deadTag = m.FanIn == 0 && m.Kind is "method" or "function" or "static-method"
                ? " ⚠️" : "";
            sb.AppendLine($"  - `{display}` ({m.Kind}) — {m.FanIn} callers{deadTag}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "search_cpp_symbols", ReadOnly = true,
     Title = "Search C++ symbols by name"),
     Description("Search for C++ symbols (functions, classes, methods, variables, macros) by name substring. " +
                 "Returns symbol definitions — where things are declared/defined. " +
                 "For finding all usage sites of a known symbol, use find_cpp_references instead. " +
                 "For finding callers of a function, use find_callers. " +
                 "Example: search_cpp_symbols('Scene') or search_cpp_symbols('MAX_')")]
    public string SearchCppSymbols(string query, int maxResults = 30)
    {
        if (!CheckReady(out var msg, query)) return msg;

        var symbols = _xrefDb.SearchSymbols(query, maxResults);
        if (symbols.Count == 0)
            return $"No C++ symbols matching '{query}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## C++ symbols matching '{query}' ({symbols.Count}):");
        sb.AppendLine();

        var byKind = symbols.GroupBy(s => s.Kind).OrderBy(g => g.Key);
        foreach (var group in byKind)
        {
            sb.AppendLine($"### {group.Key}s:");
            foreach (var sym in group.OrderBy(s => s.QualifiedName ?? s.Name))
            {
                var qname = sym.QualifiedName ?? sym.Name;
                sb.AppendLine($"  - `{qname}` — `{sym.File}:{sym.Line}`");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "find_virtual_overrides", ReadOnly = true,
     Title = "Find all classes that override a virtual method"),
     Description("Find which classes override a given virtual method. " +
                 "Uses the C++ override relation from the index — precise, not text search. " +
                 "Example: find_virtual_overrides('draw') or " +
                 "find_virtual_overrides('Scene::update')")]
    public string FindVirtualOverrides(string method)
    {
        if (!CheckReady(out var msg, method)) return msg;

        // Extract short name for the query (qualified names filter further below)
        var shortName = method;
        string? qualifiedFilter = null;
        var sep = method.LastIndexOf("::");
        if (sep >= 0)
        {
            shortName = method[(sep + 2)..];
            qualifiedFilter = method;
        }

        var overrides = _xrefDb.FindOverrides(shortName);

        // If user gave a qualified name, filter to that specific base
        if (qualifiedFilter != null)
        {
            overrides = overrides
                .Where(o => o.BaseQualifiedName != null &&
                    o.BaseQualifiedName.Contains(qualifiedFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (overrides.Count == 0)
        {
            // Could be empty relations table (needs re-index) or no overrides
            var symbols = _xrefDb.FindSymbol(method);
            if (symbols.Count == 0)
                return TryFuzzyFallback(method, "virtual overrides");
            return $"No overrides found for '{method}'.\n" +
                   "(If the C++ index was built before the relations table was added, re-index to populate it.)";
        }

        var sb = new StringBuilder();

        // Group by base method
        var byBase = overrides.GroupBy(o => o.BaseQualifiedName ?? o.BaseName ?? "unknown");
        foreach (var baseGroup in byBase)
        {
            var first = baseGroup.First();
            var baseDisplay = first.BaseQualifiedName ?? first.BaseName ?? "unknown";
            var baseLoc = first.BaseFile != null ? $" (`{first.BaseFile}:{first.BaseLine}`)" : "";

            sb.AppendLine($"## Overrides of `{baseDisplay}`{baseLoc} ({baseGroup.Count()}):");
            sb.AppendLine();

            foreach (var o in baseGroup)
            {
                var className = o.ParentQualifiedName ?? o.ParentName ?? "?";
                var display = o.QualifiedName ?? o.Name;
                sb.AppendLine($"  - `{display}` in `{className}` (`{o.File}:{o.Line}`)");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_impact_radius", ReadOnly = true,
     Title = "Transitive blast-radius analysis for a C++ symbol"),
     Description("If I change this function, what's affected? " +
                 "Walks callers + virtual overrides outward N hops (max 200 nodes). " +
                 "Returns every affected symbol with depth, edge type, and connecting symbol " +
                 "so the client can reconstruct the full tree. " +
                 "Example: get_impact_radius('HashGrid::add', 3)")]
    public string GetImpactRadius(string symbol, int depth = 3, int maxNodes = 200)
    {
        if (!CheckCallGraphReady(out var msg)) return msg;

        var usrs = ResolveToUsrs(symbol);
        if (usrs == null)
            return TryFuzzyFallback(symbol, "impact radius");

        depth = Math.Clamp(depth, 1, 5);
        maxNodes = Math.Clamp(maxNodes, 10, 500);

        var search = GetCallGraphSearch();
        var result = search.GetImpactRadius(usrs, depth, _xrefDb, maxNodes);

        if (result.Records.Count == 0)
            return $"No callers or overrides found for '{symbol}' within {depth} hops.\n" +
                   $"(Searched {result.NodesVisited} nodes in {result.Elapsed.TotalMilliseconds:F0}ms.)";

        var sb = new StringBuilder();
        sb.AppendLine($"## Impact radius of '{symbol}' ({result.Records.Count} affected symbols, " +
            $"depth {depth}{(result.Capped ? $", capped at {maxNodes}" : "")}):");
        sb.AppendLine();

        // Group by depth
        var byDepth = result.Records.GroupBy(r => r.Depth).OrderBy(g => g.Key);
        foreach (var depthGroup in byDepth)
        {
            sb.AppendLine($"### Depth {depthGroup.Key} ({depthGroup.Count()}):");
            sb.AppendLine();
            foreach (var rec in depthGroup.OrderBy(r => r.EdgeType))
            {
                var sym = _xrefDb.FindSymbolByUsr(rec.Usr);
                var name = sym.Count > 0
                    ? (sym[0].QualifiedName ?? sym[0].Name) : rec.Usr;
                var file = sym.Count > 0 ? $" (`{sym[0].File}:{sym[0].Line}`)" : "";

                var viaSym = _xrefDb.FindSymbolByUsr(rec.ViaUsr);
                var viaName = viaSym.Count > 0
                    ? (viaSym[0].QualifiedName ?? viaSym[0].Name) : rec.ViaUsr;

                sb.AppendLine($"  - `{name}`{file} [{rec.EdgeType} of `{viaName}`]");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"_Searched {result.NodesVisited} nodes in " +
            $"{result.Elapsed.TotalMilliseconds:F0}ms." +
            $"{(result.Capped ? $" Results capped at {maxNodes} — increase maxNodes to see more." : "")}_");
        return sb.ToString();
    }

    [McpServerTool(Name = "find_call_path", ReadOnly = true,
     Title = "Find call path between two C++ functions"),
     Description("Find if there is a call path from function A to function B and show it. " +
                 "Multi-hop reachability: 'Can a pointer event eventually trigger layout?' " +
                 "Uses bidirectional BFS over the call graph. Returns all shortest paths. " +
                 "For single-hop callers/callees, use find_callers or find_callees. " +
                 "For paths that must pass through a specific function, use find_call_path_via. " +
                 "Example: find_call_path('main', 'HashGrid::query')")]
    public string FindCallPath(string from, string to, int maxDepth = 10)
    {
        if (!CheckCallGraphReady(out var msg)) return msg;

        var fromUsrs = ResolveToUsrs(from);
        if (fromUsrs == null)
            return TryFuzzyFallback(from, "call path source");
        var toUsrs = ResolveToUsrs(to);
        if (toUsrs == null)
            return TryFuzzyFallback(to, "call path target");

        maxDepth = Math.Clamp(maxDepth, 1, 15);

        var search = GetCallGraphSearch();
        var allResults = new List<(CallPathResult result, string fromUsr, string toUsr)>();

        foreach (var fu in fromUsrs)
            foreach (var tu in toUsrs)
            {
                var result = search.FindPaths(fu, tu, maxDepth);
                if (result.Paths.Count > 0)
                    allResults.Add((result, fu, tu));
            }

        if (allResults.Count == 0)
        {
            var totalNodes = fromUsrs.Sum(fu => toUsrs.Sum(tu =>
                search.FindPaths(fu, tu, maxDepth).NodesVisited));
            return $"No call path found from '{from}' to '{to}' within {maxDepth} hops.\n" +
                   $"(Searched {totalNodes:N0} nodes. The functions may be in unrelated subsystems.)";
        }

        // Pick the result with shortest paths
        var best = allResults.OrderBy(r => r.result.Paths[0].Count).First();
        return FormatPathResult(from, to, best.result);
    }

    [McpServerTool(Name = "find_call_path_via", ReadOnly = true,
     Title = "Find call path between two C++ functions through a waypoint"),
     Description("Find call paths from A to B that pass through C. " +
                 "Useful for impact analysis: does changing C affect the path from A to B? " +
                 "Example: find_call_path_via('main', 'HashGrid::query', 'Scene::update')")]
    public string FindCallPathVia(string from, string to, string via, int maxDepth = 10)
    {
        if (!CheckCallGraphReady(out var msg)) return msg;

        var fromUsrs = ResolveToUsrs(from);
        if (fromUsrs == null)
            return TryFuzzyFallback(from, "call path source");
        var toUsrs = ResolveToUsrs(to);
        if (toUsrs == null)
            return TryFuzzyFallback(to, "call path target");
        var viaUsrs = ResolveToUsrs(via);
        if (viaUsrs == null)
            return TryFuzzyFallback(via, "call path waypoint");

        maxDepth = Math.Clamp(maxDepth, 1, 15);

        var search = GetCallGraphSearch();

        // Find best from→via and via→to paths
        CallPathResult? bestLeg1 = null;
        CallPathResult? bestLeg2 = null;

        foreach (var fu in fromUsrs)
            foreach (var vu in viaUsrs)
            {
                var r = search.FindPaths(fu, vu, maxDepth);
                if (r.Paths.Count > 0 && (bestLeg1 == null || r.Paths[0].Count < bestLeg1.Paths[0].Count))
                    bestLeg1 = r;
            }

        foreach (var vu in viaUsrs)
            foreach (var tu in toUsrs)
            {
                var r = search.FindPaths(vu, tu, maxDepth);
                if (r.Paths.Count > 0 && (bestLeg2 == null || r.Paths[0].Count < bestLeg2.Paths[0].Count))
                    bestLeg2 = r;
            }

        if (bestLeg1 == null && bestLeg2 == null)
            return $"No call path found from '{from}' to '{to}' via '{via}' within {maxDepth} hops.";
        if (bestLeg1 == null)
            return $"No call path found from '{from}' to '{via}' within {maxDepth} hops.\n" +
                   $"(Path from '{via}' to '{to}' exists: {bestLeg2!.Paths[0].Count - 1} hops.)";
        if (bestLeg2 == null)
            return $"No call path found from '{via}' to '{to}' within {maxDepth} hops.\n" +
                   $"(Path from '{from}' to '{via}' exists: {bestLeg1.Paths[0].Count - 1} hops.)";

        // Combine: show leg1, then leg2
        var sb = new StringBuilder();
        sb.AppendLine($"## Call path from '{from}' to '{to}' via '{via}':");
        sb.AppendLine();

        sb.AppendLine($"### Leg 1: '{from}' → '{via}' ({bestLeg1.Paths[0].Count - 1} hops, " +
            $"{bestLeg1.Paths.Count} path(s)):");
        sb.AppendLine();
        FormatPaths(sb, bestLeg1.Paths);

        sb.AppendLine();
        sb.AppendLine($"### Leg 2: '{via}' → '{to}' ({bestLeg2.Paths[0].Count - 1} hops, " +
            $"{bestLeg2.Paths.Count} path(s)):");
        sb.AppendLine();
        FormatPaths(sb, bestLeg2.Paths);

        var totalNodes = bestLeg1.NodesVisited + bestLeg2.NodesVisited;
        var totalTime = bestLeg1.Elapsed + bestLeg2.Elapsed;
        sb.AppendLine();
        sb.AppendLine($"_Searched {totalNodes:N0} nodes in {totalTime.TotalMilliseconds:F0}ms._");

        return sb.ToString();
    }

    // --- Path formatting helpers ---

    private string FormatPathResult(string from, string to, CallPathResult result)
    {
        var sb = new StringBuilder();
        var hopCount = result.Paths[0].Count - 1;
        sb.AppendLine($"## Call path: '{from}' → '{to}' ({hopCount} hops, " +
            $"{result.Paths.Count} path(s)):");
        sb.AppendLine();
        FormatPaths(sb, result.Paths);
        sb.AppendLine();
        sb.AppendLine($"_Searched {result.NodesVisited:N0} nodes in " +
            $"{result.Elapsed.TotalMilliseconds:F0}ms._");
        return sb.ToString();
    }

    private void FormatPaths(StringBuilder sb, List<List<string>> paths)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (paths.Count > 1)
                sb.AppendLine($"**Path {i + 1}:**");

            var path = paths[i];
            for (int j = 0; j < path.Count; j++)
            {
                var usr = path[j];
                var sym = _xrefDb.FindSymbolByUsr(usr);
                var name = sym.Count > 0
                    ? (sym[0].QualifiedName ?? sym[0].Name)
                    : usr;
                var file = sym.Count > 0 ? $" (`{sym[0].File}:{sym[0].Line}`)" : "";
                var prefix = j == 0 ? "  " : "  → ";
                sb.AppendLine($"{prefix}`{name}`{file}");
            }
            if (paths.Count > 1 && i < paths.Count - 1)
                sb.AppendLine();
        }
    }

    private List<string>? ResolveToUsrs(string nameOrUsr)
    {
        var symbols = _xrefDb.FindSymbol(nameOrUsr);
        if (symbols.Count == 0) return null;

        // Prefer definitions (functions/methods with the name as a definition)
        var usrs = symbols
            .Where(s => s.Kind is "function" or "method" or "constructor" or "static-method")
            .Select(s => s.Usr)
            .Distinct()
            .ToList();

        // Fallback to any match if no function/method found
        if (usrs.Count == 0)
            usrs = symbols.Select(s => s.Usr).Distinct().ToList();

        return usrs;
    }

    private bool CheckCallGraphReady(out string msg)
    {
        if (!CheckReady(out msg)) return false;

        if (!_xrefDb.IsCallGraphLoaded)
        {
            msg = "Call graph is loading. Try again shortly.";
            return false;
        }
        return true;
    }

    // --- Helpers ---

    private bool CheckReady(out string message, string? symbolHint = null)
    {
        if (_xrefDb.IsReady)
        {
            message = "";
            return true;
        }

        if (_xrefDb.Error != null)
        {
            message = $"C++ index unavailable: {_xrefDb.Error}\n" +
                "Run `mcpp index compile_commands.json -o xref.db` first.";
            return false;
        }

        if (_xrefDb.TotalFiles > 0)
        {
            // Nudge the indexer to prioritize relevant files
            if (symbolHint != null)
                PrioritizeForSymbol(symbolHint);

            message = $"C++ index is building ({_xrefDb.Progress:F0}% complete — " +
                $"{_xrefDb.IndexedFiles}/{_xrefDb.TotalFiles} files). " +
                (symbolHint != null ? "Requested priority indexing for related files. " : "") +
                "Try again shortly.";
            return false;
        }

        message = "C++ index not available. Run `mcpp index compile_commands.json -o xref.db` first.";
        return false;
    }

    private string TryFuzzyFallback(string symbol, string queryType)
    {
        // Try substring search as fallback
        var shortName = symbol;
        var lastSep = symbol.LastIndexOf("::");
        if (lastSep >= 0) shortName = symbol[(lastSep + 2)..];

        var similar = _xrefDb.SearchSymbols(shortName, 10);
        if (similar.Count == 0)
            return $"No {queryType} found for '{symbol}' in the C++ index.";

        var sb = new StringBuilder();
        sb.AppendLine($"No exact match for '{symbol}'. Similar symbols:");
        foreach (var s in similar)
            sb.AppendLine($"  - `{s.QualifiedName ?? s.Name}` ({s.Kind}) — `{s.File}:{s.Line}`");

        return sb.ToString();
    }

    /// <summary>
    /// When the index is still building, request priority indexing of files
    /// that likely contain the queried symbol (by name convention).
    /// </summary>
    private void PrioritizeForSymbol(string symbol)
    {
        // Extract the short class/type name for file matching
        var name = symbol;
        var sep = symbol.LastIndexOf("::");
        if (sep >= 0) name = symbol[..sep];  // "Foo::Bar" -> prioritize "Foo" files

        // Glob for likely source files
        var patterns = new[] { $"*{name}*.cpp", $"*{name}*.h" };
        foreach (var pattern in patterns)
        {
            try
            {
                var files = Directory.GetFiles(_cppIndexer.RepoRoot, pattern, SearchOption.AllDirectories);
                foreach (var f in files.Take(10))  // cap to avoid flooding
                    _cppIndexer.Prioritize(f);
            }
            catch (Exception ex)
            {
                McpLogger.Log("Tool", $"PrioritizeForSymbol glob failed: {ex.Message}", "WARN");
            }
        }
    }
}
