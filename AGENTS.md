# mcpp — Development Agent Instructions

You are working on mcpp, a standalone C++ cross-reference MCP server.
It indexes MSVC-compiled C++ projects into SQLite via Clang's IndexDataConsumer,
then serves semantic queries (callers, refs, call graph) over MCP stdio.

## Origin

Built around Clang's IndexDataConsumer API for C++ cross-referencing.
Key technical decisions around parallel indexing, PCH, SQLite, and
P/Invoke were validated through extensive real-world testing and
should not be revisited without good reason.

## Usage

```
mcpp index path/to/build/                         # xref.db next to compdb
mcpp index compile_commands.json -o xref.db       # explicit output
mcpp serve xref.db
```

## Project layout

```
mcpp/
├── README.md
├── BACKLOG.md
├── AGENTS.md                      # This file
├── Mcpp.slnx                     # Solution (C# project only; vcxproj is standalone)
├── etc/
│   ├── scripts/
│   │   └── setup-deps.ps1        # Downloads LLVM 21.1.1 static libs
│   └── poc-clangxref/
│       └── poc_test.cpp           # Standalone DLL smoke test
├── deps/                          # LLVM/Clang libs (gitignored, auto-downloaded)
├── bin/                           # Build output (gitignored)
└── src/
    ├── ClangXref/                 # Native C++ DLL (IndexDataConsumer)
    │   ├── ClangXref.vcxproj
    │   ├── include/clang_xref.h   # C-ABI: 6 exports, 7 typedefs
    │   └── src/clang_xref.cpp     # ~1350 lines, LLVM 21.1.1
    └── Mcpp/                      # C# MCP server + indexer (.NET 8)
        ├── Mcpp.csproj
        ├── Program.cs             # Subcommand dispatch (System.CommandLine 2.0.5)
        ├── Indexing/
        │   ├── CppIndexer.cs      # Parallel TU dispatch, DB flush, retry
        │   ├── NativeIndexer.cs   # P/Invoke + XrefSession wrapper
        │   ├── XrefDatabase.cs    # SQLite: schema, BulkInsert, per-query connections
        │   ├── CallGraphSearch.cs # In-memory BFS, impact radius
        │   └── McpLogger.cs       # Structured logging to stderr + file
        └── Tools/
            └── CppSemanticTools.cs # 11 MCP tools: callers, refs, call graph, etc.
```

## Two build systems

| Component | Tool | Command |
|-----------|------|---------|
| ClangXref.dll | MSBuild (VS 2022) | `msbuild src\ClangXref\ClangXref.vcxproj /p:Configuration=Release /p:Platform=x64` |
| mcpp.exe (C#) | dotnet CLI | `dotnet build -c Release src\Mcpp` |

Do NOT use `msbuild` for the C# project or `dotnet` for the C++ DLL.

The vcxproj PreBuildEvent auto-runs `etc/scripts/setup-deps.ps1` to download LLVM
libs on first build. Both DLL and exe output to `bin/`.

## Key technical decisions

1. **Per-thread VFS** — `createPhysicalFileSystem()` per thread, not the global singleton
2. **Per-TU DB flush** — flush immediately under lock, don't accumulate
3. **SEH two-tier retry** — DLL retries inline, C# retries failures sequentially
4. **Shared CompilationDatabase** — `shared_ptr` behind mutex, loaded once
5. **MSVC flag whitelist** — only keep `/I`, `/D`, `/std:`, `-std:`, `/TP`, `/Zc:`, `/wd`;
   rewrite `/external:I` → `/I`; drop `@responsefile` args
6. **System header filter** — `SM.isInSystemHeader(Loc)` not path-prefix matching,
   so out-of-tree builds (CMake `build/` vs `src/`) work correctly
7. **Ref/call dedup** — `UNIQUE` constraints + `INSERT OR IGNORE`
8. **Per-query SQLite connections** — WAL mode, new read connection per query
9. **WAL checkpoint on close** — explicit `PRAGMA wal_checkpoint(TRUNCATE)` before
   closing, so `File.Move` in SwapDatabase doesn't orphan the WAL sidecar
10. **GC.KeepAlive** — pin P/Invoke delegate pointers

## MCP SDK

Using `ModelContextProtocol` v1.3.0 from nuget.org (public).
Stdio transport only. Tool discovery via `[McpServerToolType]` attribute.

## Dependencies

- .NET 8 SDK
- Visual Studio 2022 (v143 toolset for ClangXref.dll)
- LLVM 21.1.1 static libs (auto-downloaded by setup-deps.ps1)
- NuGet: ModelContextProtocol 1.3.0, Microsoft.Data.Sqlite 9.0.3,
  System.CommandLine 2.0.5, Microsoft.Extensions.Hosting 9.0.1

## Working conventions

- Baby steps, atomic commits
- Commit style: imperative mood, 50/72 char limits, Co-authored-by trailer
- All diagnostics to stderr (stdout is MCP protocol)
- PoC scripts in `etc/` for standalone validation
- Parked features in BACKLOG.md
- Don't run ahead — discuss before implementing
