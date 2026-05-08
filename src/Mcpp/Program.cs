using System.CommandLine;
using System.CommandLine.Parsing;
using Mcpp.Indexing;

// --- Root command ---
var root = new RootCommand("mcpp — C++ cross-reference MCP server");

// --- index subcommand ---
var indexFileArg = new Argument<FileInfo>("file");
indexFileArg.Description = "Path to compile_commands.json (or directory containing it)";

var indexOutputOpt = new Option<FileInfo>("-o") { Required = true };
indexOutputOpt.Description = "Output xref.db path";
indexOutputOpt.Aliases.Add("--output");

var indexThreadsOpt = new Option<int>("-j");
indexThreadsOpt.Description = "Parallel thread count";
indexThreadsOpt.Aliases.Add("--threads");
indexThreadsOpt.DefaultValueFactory = (_) => Environment.ProcessorCount;

var indexCmd = new Command("index", "Build cross-reference database from compile_commands.json");
indexCmd.Arguments.Add(indexFileArg);
indexCmd.Options.Add(indexOutputOpt);
indexCmd.Options.Add(indexThreadsOpt);
indexCmd.SetAction(ctx => RunIndex(ctx.GetValue(indexFileArg)!,
    ctx.GetValue(indexOutputOpt)!, ctx.GetValue(indexThreadsOpt)));
root.Subcommands.Add(indexCmd);

// --- serve subcommand ---
var serveDbArg = new Argument<FileInfo>("db");
serveDbArg.Description = "Path to xref.db";

var serveTransportOpt = new Option<string>("--transport");
serveTransportOpt.Description = "MCP transport: stdio (default) or http";
serveTransportOpt.DefaultValueFactory = (_) => "stdio";

var servePortOpt = new Option<int>("--port");
servePortOpt.Description = "HTTP port (only used with --transport http)";
servePortOpt.DefaultValueFactory = (_) => 8080;

var serveCmd = new Command("serve", "Start MCP server with a pre-built xref.db");
serveCmd.Arguments.Add(serveDbArg);
serveCmd.Options.Add(serveTransportOpt);
serveCmd.Options.Add(servePortOpt);
serveCmd.SetAction(ctx => RunServe(ctx.GetValue(serveDbArg)!,
    ctx.GetValue(serveTransportOpt)!, ctx.GetValue(servePortOpt)));
root.Subcommands.Add(serveCmd);

return root.Parse(args).Invoke();

// ──────────────────────────────────────────────────────────────
// index: parse compile_commands.json → build xref.db
// ──────────────────────────────────────────────────────────────
static void RunIndex(FileInfo input, FileInfo output, int threads)
{
    McpLogger.Init();

    // Resolve to directory (Clang's loadFromDirectory expects a dir)
    string compdbDir;
    string compdbPath;
    if (input.Attributes.HasFlag(FileAttributes.Directory))
    {
        compdbDir = input.FullName;
        compdbPath = Path.Combine(compdbDir, "compile_commands.json");
    }
    else
    {
        compdbDir = input.DirectoryName!;
        compdbPath = input.FullName;
    }

    if (!File.Exists(compdbPath))
    {
        McpLogger.Log("Startup", $"compile_commands.json not found at {compdbPath}", "ERROR");
        Environment.Exit(1);
    }

    // Probe ClangXref.dll
    NativeIndexer.RegisterResolver(compdbDir);
    string dllVersion;
    try
    {
        dllVersion = NativeIndexer.GetVersion();
        McpLogger.Log("Startup", $"ClangXref: {dllVersion}");
    }
    catch (DllNotFoundException)
    {
        McpLogger.Log("Startup",
            "ClangXref.dll not found. Build it first:\n" +
            "  msbuild src\\ClangXref\\ClangXref.vcxproj /p:Configuration=Release /p:Platform=x64",
            "ERROR");
        Environment.Exit(1);
        return;
    }

    var dbPath = output.FullName;
    McpLogger.Log("Startup", $"Indexing {compdbPath} → {dbPath} ({threads} threads)");

    var db = new XrefDatabase(dbPath);
    var indexer = new CppIndexer(db, compdbDir, compdbPath);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    indexer.BuildIndex(CancellationToken.None);
    sw.Stop();

    var stats = db.GetStats();
    McpLogger.Log("Startup",
        $"Done in {sw.Elapsed.TotalSeconds:F1}s — " +
        $"{stats.symbols} symbols, {stats.refs} refs, {stats.calls} calls");
}

// ──────────────────────────────────────────────────────────────
// serve: load xref.db → start MCP server
// ──────────────────────────────────────────────────────────────
static void RunServe(FileInfo dbFile, string transport, int port)
{
    McpLogger.Init();

    if (!dbFile.Exists)
    {
        McpLogger.Log("Startup", $"xref.db not found: {dbFile.FullName}", "ERROR");
        Environment.Exit(1);
    }

    McpLogger.Log("Startup", $"Loading {dbFile.FullName}...");
    var db = new XrefDatabase(dbFile.FullName);
    if (!db.TryLoadExisting())
    {
        McpLogger.Log("Startup", "xref.db exists but has no data. Run `mcpp index` first.", "ERROR");
        Environment.Exit(1);
    }

    McpLogger.Log("Startup", "MCP server not yet wired — coming next commit.");
    // TODO: wire MCP host with CppSemanticTools
}

