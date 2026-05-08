using System.Diagnostics;

namespace Mcpp.Indexing;

/// <summary>
/// Bidirectional BFS over the in-memory call graph.
/// Finds all shortest paths between two functions, with pruning for
/// infrastructure noise and hyper-connected nodes.
/// </summary>
public sealed class CallGraphSearch
{
    private readonly Dictionary<string, HashSet<string>> _forward;
    private readonly Dictionary<string, HashSet<string>> _backward;
    private readonly Dictionary<string, int> _fanIn;
    private readonly HashSet<string> _blocklist;
    private readonly Func<string, string?> _usrToFile;

    private const int FanInCap = 500;

    // Non-production path prefixes (normalized to forward slashes for comparison)
    private static readonly string[] NonProdPrefixes =
        ["test/", "samples/", "tools/", "etc/", "perf/"];

    // Infrastructure symbols to always block (matched by qualified_name)
    private static readonly string[] BlockedNames =
    [
        "wil::verify_hresult",
        "OnFailure",
        "TraceForFailFast",
        "GetStowedExceptionsForFailFast",
        "FailFastWithStowedExceptions",
    ];

    public CallGraphSearch(
        Dictionary<string, HashSet<string>> forward,
        Dictionary<string, HashSet<string>> backward,
        Dictionary<string, int> fanIn,
        XrefDatabase xrefDb)
    {
        _forward = forward;
        _backward = backward;
        _fanIn = fanIn;

        // Build blocklist: resolve known infrastructure names to USRs
        _blocklist = [];
        foreach (var name in BlockedNames)
        {
            var symbols = xrefDb.FindSymbol(name);
            foreach (var s in symbols)
                _blocklist.Add(s.Usr);
        }

        // Build a USR→file lookup for non-prod path filtering
        // Lazy file lookup: cache USR→file mappings on demand from the DB
        var usrToFile = new Dictionary<string, string>();
        _usrToFile = usr =>
        {
            if (usrToFile.TryGetValue(usr, out var cached))
                return cached;
            var syms = xrefDb.FindSymbolByUsr(usr);
            var file = syms.Count > 0 ? syms[0].File : null;
            if (file != null)
                usrToFile[usr] = file;
            return file;
        };

        McpLogger.Log("Tool", $"CallGraphSearch: {_blocklist.Count} blocked USRs");
    }

    /// <summary>
    /// Find all shortest paths from source to target using bidirectional BFS.
    /// </summary>
    public CallPathResult FindPaths(string sourceUsr, string targetUsr, int maxDepth = 10)
    {
        var sw = Stopwatch.StartNew();

        if (sourceUsr == targetUsr)
            return new CallPathResult([[sourceUsr]], 1, sw.Elapsed);

        // Forward BFS state: USR → parent USR (null for source)
        var forwardVisited = new Dictionary<string, HashSet<string>> { [sourceUsr] = [] };
        var forwardFrontier = new HashSet<string> { sourceUsr };

        // Backward BFS state: USR → child USR (null for target)
        var backwardVisited = new Dictionary<string, HashSet<string>> { [targetUsr] = [] };
        var backwardFrontier = new HashSet<string> { targetUsr };

        var endpoints = new HashSet<string> { sourceUsr, targetUsr };
        int nodesVisited = 2;

        for (int depth = 0; depth < maxDepth; depth++)
        {
            // Expand the smaller frontier (bidirectional optimization)
            bool expandForward = forwardFrontier.Count <= backwardFrontier.Count;

            if (expandForward)
            {
                var nextFrontier = new HashSet<string>();
                foreach (var node in forwardFrontier)
                {
                    if (!_forward.TryGetValue(node, out var callees))
                        continue;
                    foreach (var callee in callees)
                    {
                        if (!ShouldTraverse(callee, endpoints))
                            continue;

                        // Skip if visited in a previous level (shorter paths exist).
                        // Allow same-level revisits to collect all shortest-path parents.
                        bool previousLevel = forwardVisited.ContainsKey(callee) && !nextFrontier.Contains(callee);
                        if (previousLevel)
                            continue;

                        if (!forwardVisited.TryGetValue(callee, out var parents))
                        {
                            parents = [];
                            forwardVisited[callee] = parents;
                            nodesVisited++;
                        }
                        parents.Add(node);
                        nextFrontier.Add(callee);
                    }
                }
                forwardFrontier = nextFrontier;
            }
            else
            {
                var nextFrontier = new HashSet<string>();
                foreach (var node in backwardFrontier)
                {
                    if (!_backward.TryGetValue(node, out var callers))
                        continue;
                    foreach (var caller in callers)
                    {
                        if (!ShouldTraverse(caller, endpoints))
                            continue;

                        bool previousLevel = backwardVisited.ContainsKey(caller) && !nextFrontier.Contains(caller);
                        if (previousLevel)
                            continue;

                        if (!backwardVisited.TryGetValue(caller, out var children))
                        {
                            children = [];
                            backwardVisited[caller] = children;
                            nodesVisited++;
                        }
                        children.Add(node);
                        nextFrontier.Add(caller);
                    }
                }
                backwardFrontier = nextFrontier;
            }

            // Check for intersection
            var meetingPoints = new HashSet<string>();
            foreach (var node in expandForward ? forwardFrontier : backwardFrontier)
            {
                if ((expandForward ? backwardVisited : forwardVisited).ContainsKey(node))
                    meetingPoints.Add(node);
            }

            if (meetingPoints.Count > 0)
            {
                var paths = ReconstructPaths(sourceUsr, targetUsr,
                    forwardVisited, backwardVisited, meetingPoints);
                McpLogger.Log("Tool", $"CallGraphSearch: {paths.Count} path(s), " +
                    $"{paths[0].Count - 1} hops, {nodesVisited} nodes in {sw.ElapsedMilliseconds}ms");
                return new CallPathResult(paths, nodesVisited, sw.Elapsed);
            }

            // If either frontier is empty, no path exists
            if (forwardFrontier.Count == 0 || backwardFrontier.Count == 0)
                break;
        }

        McpLogger.Log("Tool", $"CallGraphSearch: no path, {nodesVisited} nodes in {sw.ElapsedMilliseconds}ms");
        return new CallPathResult([], nodesVisited, sw.Elapsed);
    }

    private bool ShouldTraverse(string usr, HashSet<string> endpoints)
    {
        // Endpoints are always exempt from pruning
        if (endpoints.Contains(usr))
            return true;

        // Static blocklist
        if (_blocklist.Contains(usr))
            return false;

        // Dynamic fan-in cap
        if (_fanIn.TryGetValue(usr, out var fi) && fi > FanInCap)
            return false;

        // Non-production file path filter
        var file = _usrToFile(usr);
        if (file != null)
        {
            var normalized = file.Replace('\\', '/').ToLowerInvariant();
            foreach (var prefix in NonProdPrefixes)
            {
                if (normalized.Contains('/' + prefix) ||
                    normalized.StartsWith(prefix, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reconstruct all shortest paths through the meeting points.
    /// </summary>
    private static List<List<string>> ReconstructPaths(
        string source, string target,
        Dictionary<string, HashSet<string>> forwardParents,
        Dictionary<string, HashSet<string>> backwardChildren,
        HashSet<string> meetingPoints)
    {
        var allPaths = new List<List<string>>();

        foreach (var meet in meetingPoints)
        {
            // Enumerate all forward paths: source → ... → meet
            var forwardPaths = TracePaths(meet, source, forwardParents);
            // Enumerate all backward paths: meet → ... → target
            var backwardPaths = TracePaths(meet, target, backwardChildren);

            foreach (var fp in forwardPaths)
            {
                fp.Reverse(); // was meet→...→source, now source→...→meet
                foreach (var bp in backwardPaths)
                {
                    var path = new List<string>(fp);
                    // bp is meet→...→target; skip first element (meet) to avoid duplicate
                    for (int i = 1; i < bp.Count; i++)
                        path.Add(bp[i]);
                    allPaths.Add(path);
                }
            }
        }

        return allPaths;
    }

    /// <summary>
    /// Trace all paths from 'node' back to 'root' through the parent map.
    /// Returns paths as lists from node→...→root.
    /// </summary>
    private static List<List<string>> TracePaths(
        string node, string root,
        Dictionary<string, HashSet<string>> parentMap)
    {
        if (node == root)
            return [[root]];

        var results = new List<List<string>>();
        if (!parentMap.TryGetValue(node, out var parents) || parents.Count == 0)
            return results;

        foreach (var parent in parents)
        {
            var subPaths = TracePaths(parent, root, parentMap);
            foreach (var sp in subPaths)
            {
                sp.Insert(0, node);
                results.Add(sp);
            }
        }

        return results;
    }

    /// <summary>
    /// Walk outward from a symbol: callers + overrides at each hop.
    /// Returns every affected symbol with depth, edge type, and connecting symbol.
    /// Stops at maxNodes to prevent runaway expansion.
    /// </summary>
    public ImpactResult GetImpactRadius(
        List<string> sourceUsrs, int maxDepth, XrefDatabase xrefDb,
        int maxNodes = 200)
    {
        var sw = Stopwatch.StartNew();
        var visited = new HashSet<string>(sourceUsrs);
        var records = new List<ImpactRecord>();
        var endpoints = new HashSet<string>(sourceUsrs);
        bool capped = false;

        // Seed frontier: source + any overrides of source (if virtual)
        var frontier = new HashSet<string>(sourceUsrs);
        foreach (var src in sourceUsrs)
        {
            var overrides = xrefDb.FindOverridesByUsr(src);
            foreach (var ov in overrides)
            {
                if (visited.Add(ov))
                {
                    frontier.Add(ov);
                    records.Add(new ImpactRecord(ov, 0, "override", src));
                }
            }
        }

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            var nextFrontier = new HashSet<string>();

            foreach (var node in frontier)
            {
                // Expand callers (backward edges)
                if (_backward.TryGetValue(node, out var callers))
                {
                    foreach (var caller in callers)
                    {
                        if (records.Count >= maxNodes) { capped = true; break; }
                        if (visited.Contains(caller))
                            continue;
                        if (!ShouldTraverse(caller, endpoints))
                            continue;

                        visited.Add(caller);
                        records.Add(new ImpactRecord(caller, depth, "caller", node));
                        nextFrontier.Add(caller);
                    }
                }
                if (capped) break;

                // Expand overrides (if this node is a virtual method)
                var nodeOverrides = xrefDb.FindOverridesByUsr(node);
                foreach (var ov in nodeOverrides)
                {
                    if (records.Count >= maxNodes) { capped = true; break; }
                    if (visited.Contains(ov))
                        continue;
                    if (!ShouldTraverse(ov, endpoints))
                        continue;

                    visited.Add(ov);
                    records.Add(new ImpactRecord(ov, depth, "override", node));
                    nextFrontier.Add(ov);
                }
                if (capped) break;
            }

            if (capped) break;
            frontier = nextFrontier;
            if (frontier.Count == 0)
                break;
        }

        McpLogger.Log("Tool", $"ImpactRadius: {records.Count} affected" +
            $"{(capped ? " (capped)" : "")}, {visited.Count} visited in {sw.ElapsedMilliseconds}ms");
        return new ImpactResult(records, visited.Count, sw.Elapsed, capped);
    }
}

/// <summary>
/// Result of a call path search.
/// </summary>
public record CallPathResult(
    List<List<string>> Paths,
    int NodesVisited,
    TimeSpan Elapsed);

/// <summary>
/// A single affected symbol from an impact radius walk.
/// </summary>
public record ImpactRecord(
    string Usr,
    int Depth,
    string EdgeType,  // "caller" or "override"
    string ViaUsr);   // connecting symbol at depth-1

/// <summary>
/// Result of an impact radius walk.
/// </summary>
public record ImpactResult(
    List<ImpactRecord> Records,
    int NodesVisited,
    TimeSpan Elapsed,
    bool Capped);
