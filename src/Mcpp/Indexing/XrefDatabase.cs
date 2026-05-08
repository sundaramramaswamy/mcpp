using Microsoft.Data.Sqlite;

namespace Mcpp.Indexing;

/// <summary>
/// SQLite-backed cross-reference database for C++ symbols, call graphs, and references.
/// Built by CppIndexer, queried by CppSemanticTools.
/// </summary>
public sealed class XrefDatabase : IDisposable
{
    private string _dbPath;
    private SqliteConnection? _connection;
    private volatile bool _isReady;
    private volatile int _totalFiles;
    private volatile int _indexedFiles;
    private volatile string? _error;

    // In-memory call graph for path queries (loaded after DB is ready)
    private Dictionary<string, HashSet<string>>? _forwardGraph;  // caller → callees
    private Dictionary<string, HashSet<string>>? _backwardGraph; // callee → callers
    private Dictionary<string, int>? _fanInCount;                // callee_usr → fan-in

    public bool IsReady => _isReady;
    public bool IsCallGraphLoaded => _forwardGraph != null;
    public int TotalFiles => _totalFiles;
    public int IndexedFiles => _indexedFiles;
    public string? Error => _error;
    public string DbPath => _dbPath;

    public float Progress => _totalFiles > 0 ? (float)_indexedFiles / _totalFiles * 100 : 0;

    public XrefDatabase(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// Switch to a different DB path (for snapshot loading from a remote dir).
    /// Must be called before Open/TryLoadExisting.
    /// </summary>
    public void SwitchPath(string newPath) => _dbPath = newPath;

    public void Open()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        InitializeSchema();
    }

    /// <summary>
    /// Checks whether the DB file exists and has symbol data (from a prior run).
    /// </summary>
    public bool TryLoadExisting()
    {
        if (!File.Exists(_dbPath))
            return false;

        try
        {
            Open();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM symbols";
            var count = (long)cmd.ExecuteScalar()!;
            if (count > 0)
            {
                _isReady = true;
                LoadCallGraph();
                return true;
            }
        }
        catch (Exception ex)
        {
            // Corrupt or incompatible DB — will be rebuilt
            McpLogger.Log("XrefDB", $"Failed to load {_dbPath}: {ex.Message}", "WARN");
        }

        return false;
    }

    public void SetProgress(int indexed, int total)
    {
        _indexedFiles = indexed;
        _totalFiles = total;
    }

    public void MarkReady() => _isReady = true;
    public void MarkError(string error) => _error = error;

    /// <summary>
    /// Re-opens the DB file (e.g. after another process swapped in a new index).
    /// Clears connection pools to release stale file handles, then checks for data.
    /// </summary>
    public void Reload()
    {
        Close();
        SqliteConnection.ClearAllPools();

        if (!File.Exists(_dbPath))
            return;

        try
        {
            Open();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM symbols";
            var count = (long)cmd.ExecuteScalar()!;
            if (count > 0)
            {
                _isReady = true;
                LoadCallGraph();
                McpLogger.Log("XrefDB", $"Reloaded — {count} symbols");
            }
        }
        catch (Exception ex)
        {
            McpLogger.Log("XrefDB", $"Reload failed: {ex.Message}", "ERROR");
        }
    }

    /// <summary>
    /// Loads the call graph into memory as adjacency lists for BFS path queries.
    /// ~303K unique pairs ≈ 36 MB. Called after the DB is ready.
    /// </summary>
    public void LoadCallGraph()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var forward = new Dictionary<string, HashSet<string>>();
        var backward = new Dictionary<string, HashSet<string>>();
        var fanIn = new Dictionary<string, int>();

        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT caller_usr, callee_usr FROM calls";
        using var reader = cmd.ExecuteReader();

        int pairCount = 0;
        while (reader.Read())
        {
            var caller = reader.GetString(0);
            var callee = reader.GetString(1);

            if (!forward.TryGetValue(caller, out var fSet))
            {
                fSet = [];
                forward[caller] = fSet;
            }
            fSet.Add(callee);

            if (!backward.TryGetValue(callee, out var bSet))
            {
                bSet = [];
                backward[callee] = bSet;
            }
            bSet.Add(caller);

            fanIn[callee] = fanIn.GetValueOrDefault(callee) + 1;
            pairCount++;
        }

        _forwardGraph = forward;
        _backwardGraph = backward;
        _fanInCount = fanIn;

        McpLogger.Log("XrefDB", $"Call graph loaded: {pairCount:N0} pairs, " +
            $"{forward.Count:N0} callers, {backward.Count:N0} callees in {sw.ElapsedMilliseconds}ms");
    }

    public Dictionary<string, HashSet<string>>? ForwardGraph => _forwardGraph;
    public Dictionary<string, HashSet<string>>? BackwardGraph => _backwardGraph;
    public Dictionary<string, int>? FanInCount => _fanInCount;

    // --- Schema ---

    private void InitializeSchema()
    {
        Execute(@"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-64000;

            CREATE TABLE IF NOT EXISTS symbols (
                usr TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                qualified_name TEXT,
                kind TEXT NOT NULL,
                file TEXT NOT NULL,
                line INTEGER NOT NULL,
                col INTEGER DEFAULT 0,
                parent_usr TEXT,
                signature TEXT
            );

            CREATE TABLE IF NOT EXISTS refs (
                symbol_usr TEXT NOT NULL,
                file TEXT NOT NULL,
                line INTEGER NOT NULL,
                col INTEGER DEFAULT 0,
                ref_kind TEXT DEFAULT 'ref',
                UNIQUE(symbol_usr, file, line, col)
            );

            CREATE TABLE IF NOT EXISTS calls (
                caller_usr TEXT NOT NULL,
                callee_usr TEXT NOT NULL,
                file TEXT NOT NULL,
                line INTEGER NOT NULL,
                UNIQUE(caller_usr, callee_usr, file, line)
            );

            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS relations (
                subject_usr TEXT NOT NULL,
                predicate TEXT NOT NULL,
                object_usr TEXT NOT NULL,
                UNIQUE(subject_usr, predicate, object_usr)
            );

            CREATE INDEX IF NOT EXISTS idx_sym_name ON symbols(name);
            CREATE INDEX IF NOT EXISTS idx_sym_name_lower ON symbols(name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_sym_file ON symbols(file);
            CREATE INDEX IF NOT EXISTS idx_sym_parent ON symbols(parent_usr);
            CREATE INDEX IF NOT EXISTS idx_sym_kind ON symbols(kind);
            CREATE INDEX IF NOT EXISTS idx_ref_file ON refs(file);
            CREATE INDEX IF NOT EXISTS idx_call_caller ON calls(caller_usr);
            CREATE INDEX IF NOT EXISTS idx_call_callee ON calls(callee_usr);
            CREATE INDEX IF NOT EXISTS idx_rel_subject ON relations(subject_usr);
            CREATE INDEX IF NOT EXISTS idx_rel_object ON relations(object_usr);
            CREATE INDEX IF NOT EXISTS idx_rel_predicate ON relations(predicate);
        ");
    }

    /// <summary>Wipes all data for a fresh re-index.</summary>
    public void Clear()
    {
        Execute("DELETE FROM relations; DELETE FROM calls; DELETE FROM refs; DELETE FROM symbols; DELETE FROM meta;");
    }

    // --- Batch insert (used by CppIndexer) ---

    public SqliteTransaction BeginTransaction() => _connection!.BeginTransaction();

    public void InsertSymbol(string usr, string name, string? qualifiedName, string kind,
        string file, int line, int col = 0, string? parentUsr = null, string? signature = null)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO symbols (usr, name, qualified_name, kind, file, line, col, parent_usr, signature)
            VALUES ($usr, $name, $qname, $kind, $file, $line, $col, $parent, $sig)";
        cmd.Parameters.AddWithValue("$usr", usr);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$qname", (object?)qualifiedName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$file", file);
        cmd.Parameters.AddWithValue("$line", line);
        cmd.Parameters.AddWithValue("$col", col);
        cmd.Parameters.AddWithValue("$parent", (object?)parentUsr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", (object?)signature ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void InsertReference(string symbolUsr, string file, int line, int col = 0, string refKind = "ref")
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO refs (symbol_usr, file, line, col, ref_kind)
            VALUES ($usr, $file, $line, $col, $kind)";
        cmd.Parameters.AddWithValue("$usr", symbolUsr);
        cmd.Parameters.AddWithValue("$file", file);
        cmd.Parameters.AddWithValue("$line", line);
        cmd.Parameters.AddWithValue("$col", col);
        cmd.Parameters.AddWithValue("$kind", refKind);
        cmd.ExecuteNonQuery();
    }

    public void InsertCall(string callerUsr, string calleeUsr, string file, int line)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO calls (caller_usr, callee_usr, file, line)
            VALUES ($caller, $callee, $file, $line)";
        cmd.Parameters.AddWithValue("$caller", callerUsr);
        cmd.Parameters.AddWithValue("$callee", calleeUsr);
        cmd.Parameters.AddWithValue("$file", file);
        cmd.Parameters.AddWithValue("$line", line);
        cmd.ExecuteNonQuery();
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // --- Query methods (used by MCP tools) ---
    // Each query opens its own connection from the pool. This is critical:
    // the MCP SDK dispatches tool calls concurrently, and SqliteConnection
    // is NOT thread-safe. Separate connections allow true parallel reads
    // under WAL mode. The pool handles connection reuse internally.

    /// <summary>Opens a read-only connection from the pool.</summary>
    private SqliteConnection OpenReadConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    /// <summary>Find a symbol by exact name or USR.</summary>
    public List<SymbolRecord> FindSymbol(string nameOrUsr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT usr, name, qualified_name, kind, file, line, col, parent_usr, signature
            FROM symbols
            WHERE name = $q OR qualified_name = $q OR usr = $q
            ORDER BY kind, name
            LIMIT 50";
        cmd.Parameters.AddWithValue("$q", nameOrUsr);
        return ReadSymbols(cmd);
    }

    /// <summary>Find a symbol by exact USR only (fast primary key lookup).</summary>
    public List<SymbolRecord> FindSymbolByUsr(string usr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT usr, name, qualified_name, kind, file, line, col, parent_usr, signature
            FROM symbols WHERE usr = $q LIMIT 1";
        cmd.Parameters.AddWithValue("$q", usr);
        return ReadSymbols(cmd);
    }

    /// <summary>Find all classes that override a given virtual method.</summary>
    public List<OverrideRecord> FindOverrides(string methodName)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.usr, s.name, s.qualified_name, s.file, s.line,
                   parent.name AS parent_name, parent.qualified_name AS parent_qname,
                   base.name AS base_name, base.qualified_name AS base_qname,
                   base.file AS base_file, base.line AS base_line
            FROM relations r
            JOIN symbols s ON r.subject_usr = s.usr
            JOIN symbols base ON r.object_usr = base.usr
            LEFT JOIN symbols parent ON s.parent_usr = parent.usr
            WHERE (base.name = $q OR base.qualified_name = $q)
              AND r.predicate = 'overridden_by'
            ORDER BY parent.name, s.file";
        cmd.Parameters.AddWithValue("$q", methodName);

        var results = new List<OverrideRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new OverrideRecord(
                Usr: reader.GetString(0),
                Name: reader.GetString(1),
                QualifiedName: reader.IsDBNull(2) ? null : reader.GetString(2),
                File: reader.GetString(3),
                Line: reader.GetInt32(4),
                ParentName: reader.IsDBNull(5) ? null : reader.GetString(5),
                ParentQualifiedName: reader.IsDBNull(6) ? null : reader.GetString(6),
                BaseName: reader.IsDBNull(7) ? null : reader.GetString(7),
                BaseQualifiedName: reader.IsDBNull(8) ? null : reader.GetString(8),
                BaseFile: reader.IsDBNull(9) ? null : reader.GetString(9),
                BaseLine: reader.IsDBNull(10) ? 0 : reader.GetInt32(10)));
        }
        return results;
    }

    /// <summary>Find all methods that override a given base method (by USR).</summary>
    public List<string> FindOverridesByUsr(string baseMethodUsr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT subject_usr FROM relations
            WHERE object_usr = $usr AND predicate = 'overridden_by'";
        cmd.Parameters.AddWithValue("$usr", baseMethodUsr);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }
    public List<SymbolRecord> SearchSymbols(string query, int limit = 30)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT usr, name, qualified_name, kind, file, line, col, parent_usr, signature
            FROM symbols
            WHERE name LIKE $q OR qualified_name LIKE $q
            ORDER BY LENGTH(name), name
            LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadSymbols(cmd);
    }

    /// <summary>Find all callers of a symbol (by name or USR).</summary>
    public List<CallRecord> FindCallers(string symbolNameOrUsr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.caller_usr, c.callee_usr, c.file, c.line,
                   caller.name AS caller_name, caller.qualified_name AS caller_qname,
                   callee.name AS callee_name
            FROM calls c
            JOIN symbols callee ON c.callee_usr = callee.usr
            LEFT JOIN symbols caller ON c.caller_usr = caller.usr
            WHERE callee.name = $q OR callee.qualified_name = $q OR callee.usr = $q
            ORDER BY c.file, c.line
            LIMIT 100";
        cmd.Parameters.AddWithValue("$q", symbolNameOrUsr);
        return ReadCalls(cmd);
    }

    /// <summary>Find all callees of a symbol.</summary>
    public List<CallRecord> FindCallees(string symbolNameOrUsr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.caller_usr, c.callee_usr, c.file, c.line,
                   caller.name AS caller_name,
                   callee.name AS callee_name, callee.qualified_name AS callee_qname
            FROM calls c
            JOIN symbols caller ON c.caller_usr = caller.usr
            LEFT JOIN symbols callee ON c.callee_usr = callee.usr
            WHERE caller.name = $q OR caller.qualified_name = $q OR caller.usr = $q
            ORDER BY c.file, c.line
            LIMIT 100";
        cmd.Parameters.AddWithValue("$q", symbolNameOrUsr);
        return ReadCalls(cmd);
    }

    /// <summary>Find all references to a symbol.</summary>
    public List<RefRecord> FindReferences(string symbolNameOrUsr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT r.symbol_usr, r.file, r.line, r.col, r.ref_kind, s.name, s.qualified_name
            FROM refs r
            JOIN symbols s ON r.symbol_usr = s.usr
            WHERE s.name = $q OR s.qualified_name = $q OR s.usr = $q
            ORDER BY r.file, r.line
            LIMIT 200";
        cmd.Parameters.AddWithValue("$q", symbolNameOrUsr);
        return ReadRefs(cmd);
    }

    /// <summary>Get all members of a class/struct.</summary>
    public List<SymbolRecord> GetMembers(string classNameOrUsr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT child.usr, child.name, child.qualified_name, child.kind,
                   child.file, child.line, child.col, child.parent_usr, child.signature
            FROM symbols child
            JOIN symbols parent ON child.parent_usr = parent.usr
            WHERE parent.name = $q OR parent.qualified_name = $q OR parent.usr = $q
            ORDER BY child.kind, child.name
            LIMIT 100";
        cmd.Parameters.AddWithValue("$q", classNameOrUsr);
        return ReadSymbols(cmd);
    }

    /// <summary>Get per-member call stats for a class (fan-in per member).</summary>
    public List<MemberStatRecord> GetMemberStats(string classNameOrUsr)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT child.name, child.qualified_name, child.kind,
                   child.file, child.line,
                   COUNT(DISTINCT c.caller_usr) AS fan_in
            FROM symbols child
            JOIN symbols parent ON child.parent_usr = parent.usr
            LEFT JOIN calls c ON c.callee_usr = child.usr
            WHERE parent.name = $q OR parent.qualified_name = $q OR parent.usr = $q
            GROUP BY child.usr
            ORDER BY fan_in DESC, child.name";
        cmd.Parameters.AddWithValue("$q", classNameOrUsr);

        var results = new List<MemberStatRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MemberStatRecord(
                Name: reader.GetString(0),
                QualifiedName: reader.IsDBNull(1) ? null : reader.GetString(1),
                Kind: reader.GetString(2),
                File: reader.GetString(3),
                Line: reader.GetInt32(4),
                FanIn: reader.GetInt32(5)));
        }
        return results;
    }

    /// <summary>Get file-level call dependencies for a directory prefix.</summary>
    public List<FileDependencyRecord> GetComponentDependencies(string pathPrefix, bool outgoing)
    {
        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();

        // Normalize: accept both / and \ in the query
        var normalizedPrefix = pathPrefix.Replace('/', '\\');

        if (outgoing)
        {
            // What files does this component call into?
            cmd.CommandText = @"
                SELECT callee_s.file, COUNT(DISTINCT c.callee_usr), COUNT(*)
                FROM calls c
                JOIN symbols caller_s ON c.caller_usr = caller_s.usr
                JOIN symbols callee_s ON c.callee_usr = callee_s.usr
                WHERE caller_s.file LIKE $prefix
                  AND callee_s.file NOT LIKE $prefix
                GROUP BY callee_s.file
                ORDER BY COUNT(DISTINCT c.callee_usr) DESC
                LIMIT 200";
        }
        else
        {
            // What files call into this component?
            cmd.CommandText = @"
                SELECT caller_s.file, COUNT(DISTINCT c.caller_usr), COUNT(*)
                FROM calls c
                JOIN symbols callee_s ON c.callee_usr = callee_s.usr
                JOIN symbols caller_s ON c.caller_usr = caller_s.usr
                WHERE callee_s.file LIKE $prefix
                  AND caller_s.file NOT LIKE $prefix
                GROUP BY caller_s.file
                ORDER BY COUNT(DISTINCT c.caller_usr) DESC
                LIMIT 200";
        }
        cmd.Parameters.AddWithValue("$prefix", $"%{normalizedPrefix}%");

        var results = new List<FileDependencyRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FileDependencyRecord(
                File: reader.GetString(0),
                DistinctSymbols: reader.GetInt32(1),
                EdgeCount: reader.GetInt32(2)));
        }
        return results;
    }
    public (long symbols, long refs, long calls) GetStats()
    {
        using var conn = OpenReadConnection();
        long Count(string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)cmd.ExecuteScalar()!;
        }
        return (Count("symbols"), Count("refs"), Count("calls"));
    }

    // --- Helpers ---

    private void Execute(string sql)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<SymbolRecord> ReadSymbols(SqliteCommand cmd)
    {
        var results = new List<SymbolRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SymbolRecord(
                Usr: reader.GetString(0),
                Name: reader.GetString(1),
                QualifiedName: reader.IsDBNull(2) ? null : reader.GetString(2),
                Kind: reader.GetString(3),
                File: reader.GetString(4),
                Line: reader.GetInt32(5),
                Col: reader.GetInt32(6),
                ParentUsr: reader.IsDBNull(7) ? null : reader.GetString(7),
                Signature: reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return results;
    }

    private static List<CallRecord> ReadCalls(SqliteCommand cmd)
    {
        var results = new List<CallRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CallRecord(
                CallerUsr: reader.GetString(0),
                CalleeUsr: reader.GetString(1),
                File: reader.GetString(2),
                Line: reader.GetInt32(3),
                CallerName: reader.IsDBNull(4) ? null : reader.GetString(4),
                CallerQualifiedName: reader.IsDBNull(5) ? null : reader.GetString(5),
                CalleeName: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return results;
    }

    private static List<RefRecord> ReadRefs(SqliteCommand cmd)
    {
        var results = new List<RefRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RefRecord(
                SymbolUsr: reader.GetString(0),
                File: reader.GetString(1),
                Line: reader.GetInt32(2),
                Col: reader.GetInt32(3),
                RefKind: reader.GetString(4),
                SymbolName: reader.IsDBNull(5) ? null : reader.GetString(5),
                QualifiedName: reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return results;
    }

    public void Close()
    {
        if (_connection != null)
        {
            // Checkpoint WAL to main DB file before closing.
            // Without this, data stays in the WAL file which gets
            // orphaned when the DB file is moved (e.g. atomic swap).
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            catch { }

            _connection.Close();
            _connection.Dispose();
            _connection = null;
        }
    }

    public void Dispose()
    {
        Close();
    }

    // --- Bulk insert (used by parallel indexer) ---

    /// <summary>
    /// Inserts all records from a TuResult batch using prepared statements in a single
    /// transaction. Much faster than individual InsertSymbol/InsertReference/InsertCall
    /// calls (~50× fewer allocations, one prepare per statement type).
    /// </summary>
    public void BulkInsert(TuResult result)
    {
        using var symCmd = _connection!.CreateCommand();
        symCmd.CommandText = @"
            INSERT OR REPLACE INTO symbols (usr, name, qualified_name, kind, file, line, col, parent_usr, signature)
            VALUES ($usr, $name, $qname, $kind, $file, $line, $col, $parent, $sig)";
        var symUsr = symCmd.Parameters.Add("$usr", SqliteType.Text);
        var symName = symCmd.Parameters.Add("$name", SqliteType.Text);
        var symQName = symCmd.Parameters.Add("$qname", SqliteType.Text);
        var symKind = symCmd.Parameters.Add("$kind", SqliteType.Text);
        var symFile = symCmd.Parameters.Add("$file", SqliteType.Text);
        var symLine = symCmd.Parameters.Add("$line", SqliteType.Integer);
        var symCol = symCmd.Parameters.Add("$col", SqliteType.Integer);
        var symParent = symCmd.Parameters.Add("$parent", SqliteType.Text);
        var symSig = symCmd.Parameters.Add("$sig", SqliteType.Text);
        symCmd.Prepare();

        foreach (var s in result.Symbols)
        {
            symUsr.Value = s.Usr;
            symName.Value = s.Name;
            symQName.Value = (object?)s.QualifiedName ?? DBNull.Value;
            symKind.Value = s.Kind;
            symFile.Value = s.File;
            symLine.Value = s.Line;
            symCol.Value = s.Col;
            symParent.Value = (object?)s.ParentUsr ?? DBNull.Value;
            symSig.Value = (object?)s.Signature ?? DBNull.Value;
            symCmd.ExecuteNonQuery();
        }

        using var refCmd = _connection!.CreateCommand();
        refCmd.CommandText = @"
            INSERT OR IGNORE INTO refs (symbol_usr, file, line, col, ref_kind)
            VALUES ($usr, $file, $line, $col, $kind)";
        var refUsr = refCmd.Parameters.Add("$usr", SqliteType.Text);
        var refFile = refCmd.Parameters.Add("$file", SqliteType.Text);
        var refLine = refCmd.Parameters.Add("$line", SqliteType.Integer);
        var refCol = refCmd.Parameters.Add("$col", SqliteType.Integer);
        var refKind = refCmd.Parameters.Add("$kind", SqliteType.Text);
        refCmd.Prepare();

        foreach (var r in result.Refs)
        {
            refUsr.Value = r.SymbolUsr;
            refFile.Value = r.File;
            refLine.Value = r.Line;
            refCol.Value = r.Col;
            refKind.Value = r.RefKind;
            refCmd.ExecuteNonQuery();
        }

        using var callCmd = _connection!.CreateCommand();
        callCmd.CommandText = @"
            INSERT OR IGNORE INTO calls (caller_usr, callee_usr, file, line)
            VALUES ($caller, $callee, $file, $line)";
        var callCaller = callCmd.Parameters.Add("$caller", SqliteType.Text);
        var callCallee = callCmd.Parameters.Add("$callee", SqliteType.Text);
        var callFile = callCmd.Parameters.Add("$file", SqliteType.Text);
        var callLine = callCmd.Parameters.Add("$line", SqliteType.Integer);
        callCmd.Prepare();

        foreach (var c in result.Calls)
        {
            callCaller.Value = c.CallerUsr;
            callCallee.Value = c.CalleeUsr;
            callFile.Value = c.File;
            callLine.Value = c.Line;
            callCmd.ExecuteNonQuery();
        }

        using var relCmd = _connection!.CreateCommand();
        relCmd.CommandText = @"
            INSERT OR IGNORE INTO relations (subject_usr, predicate, object_usr)
            VALUES ($subject, $predicate, $object)";
        var relSubject = relCmd.Parameters.Add("$subject", SqliteType.Text);
        var relPredicate = relCmd.Parameters.Add("$predicate", SqliteType.Text);
        var relObject = relCmd.Parameters.Add("$object", SqliteType.Text);
        relCmd.Prepare();

        foreach (var rel in result.Relations)
        {
            relSubject.Value = rel.SubjectUsr;
            relPredicate.Value = rel.Predicate;
            relObject.Value = rel.ObjectUsr;
            relCmd.ExecuteNonQuery();
        }
    }
}

// --- Record types ---

public record SymbolRecord(
    string Usr, string Name, string? QualifiedName, string Kind,
    string File, int Line, int Col,
    string? ParentUsr, string? Signature);

public record CallRecord(
    string CallerUsr, string CalleeUsr, string File, int Line,
    string? CallerName, string? CallerQualifiedName, string? CalleeName);

public record RefRecord(
    string SymbolUsr, string File, int Line, int Col, string RefKind,
    string? SymbolName, string? QualifiedName);

public record OverrideRecord(
    string Usr, string Name, string? QualifiedName,
    string File, int Line,
    string? ParentName, string? ParentQualifiedName,
    string? BaseName, string? BaseQualifiedName,
    string? BaseFile, int BaseLine);

public record MemberStatRecord(
    string Name, string? QualifiedName, string Kind,
    string File, int Line, int FanIn);

public record FileDependencyRecord(
    string File, int DistinctSymbols, int EdgeCount);

// --- Bulk insert record types (used by parallel indexer) ---

public record SymbolInsert(
    string Usr, string Name, string? QualifiedName, string Kind,
    string File, int Line, int Col,
    string? ParentUsr = null, string? Signature = null);

public record RefInsert(string SymbolUsr, string File, int Line, int Col, string RefKind);

public record CallInsert(string CallerUsr, string CalleeUsr, string File, int Line);

public record RelationInsert(string SubjectUsr, string Predicate, string ObjectUsr);

/// <summary>
/// Collects symbols, refs, and calls from a single TU parse.
/// Thread-safe when used per-visitor (each visitor writes to its own TuResult).
/// </summary>
public sealed class TuResult
{
    public List<SymbolInsert> Symbols { get; } = [];
    public List<RefInsert> Refs { get; } = [];
    public List<CallInsert> Calls { get; } = [];
    public List<RelationInsert> Relations { get; } = [];
}
