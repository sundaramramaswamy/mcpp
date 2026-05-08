# mcpp

An [MCP](https://modelcontextprotocol.io) server that gives AI agents
**call graphs, cross-references, and impact analysis** for C++ codebases.

Without mcpp, your AI agent greps through source files — it guesses at
call relationships, misses template references, and can't answer "what
breaks if I change this?" With mcpp, it gets precise, Clang-parsed
semantic data: every caller, every call chain, every override.

**What your agent can do with mcpp:**
- *"Who calls `HashGrid::query`?"* → precise callers, not grep matches
- *"Show the call path from `main` to `Renderer::render`"* → full chain
- *"What's the blast radius if I change `Scene::update`?"* → transitive impact
- *"Is `Aabb::intersects` dead code?"* → zero callers = dead

Works with Copilot CLI, VS Code Copilot, Claude Desktop, Cursor, etc.

## Quick start

```
mcpp index path/to/build/          # needs compile_commands.json
mcpp serve path/to/build/xref.db   # start MCP server
```

## Usage

### `index` — Build cross-reference database

Parses every translation unit in a `compile_commands.json`, extracts
symbols, references, call edges, and inheritance relations into a SQLite
database.

```
mcpp index path/to/compile_commands.json          # output: xref.db next to compdb
mcpp index path/to/build/                         # directory works too
mcpp index path/to/build/ -o /tmp/xref.db         # explicit output path
mcpp index compile_commands.json -j 4             # limit threads (default: all cores)
```

After indexing, `mcpp` prints a `.mcp.json` snippet to stdout for easy copy-paste.

### `serve` — Start MCP server

Loads a pre-built `xref.db` and serves semantic queries over stdio.

```
mcpp serve xref.db
```

### MCP client configuration

`mcpp index` prints a ready-to-use JSON snippet after indexing.
Drop it into your MCP client's config:

- **Copilot CLI**: `~/.copilot/mcp-config.json` or `/mcp add`
- **VS Code Copilot**: `.github/copilot/mcp.json` in your repo
- **Claude Desktop**: `claude_desktop_config.json`

The snippet uses the `mcpServers` format supported by most MCP clients:

```json
{
    "mcpServers": {
        "mcpp": {
            "type": "stdio",
            "command": "/path/to/mcpp",
            "args": ["serve", "/path/to/xref.db"]
        }
    }
}
```

### MCP tools (11)

| Tool | Description |
|------|-------------|
| `search_cpp_symbols` | Search symbols by name substring |
| `get_cpp_definition` | Find where a symbol is defined |
| `find_cpp_references` | Find all references to a symbol |
| `find_callers` | Direct callers of a function (1-hop in) |
| `find_callees` | Direct callees of a function (1-hop out) |
| `get_class_members` | List members of a class/struct |
| `get_symbol_stats` | Per-member fan-in counts, dead code detection |
| `find_virtual_overrides` | Find classes overriding a virtual method |
| `find_call_path` | Find call chains between two functions |
| `find_call_path_via` | Call path A→B constrained through waypoint C |
| `get_impact_radius` | Transitive blast radius of a change (N hops) |

## Building

### Prerequisites

- .NET 8 SDK
- Visual Studio 2022 (v143 toolset, for ClangXref.dll)
- LLVM 21.1.1 static libs (auto-downloaded on first build)

### Build steps

```powershell
# 1. Build native DLL (VS 2022 Developer Command Prompt)
#    Auto-downloads ~540 MB of LLVM libs on first run.
msbuild src\ClangXref\ClangXref.vcxproj /p:Configuration=Release /p:Platform=x64

# 2. Build C# server
dotnet build -c Release src\Mcpp
```

Both outputs go to `bin/`.

### Generating compile_commands.json

For CMake projects (most common):
```
cmake -S . -B build -DCMAKE_EXPORT_COMPILE_COMMANDS=ON -G Ninja
```

For MSBuild projects, use [bt](https://github.com/nicknash/bt) to extract
from `.binlog`, or other tools that generate Clang-compatible compdbs.

## Architecture

```
C# (.NET 8)                           C++ (LLVM 21.1.1)
┌────────────────┐                    ┌──────────────────────┐
│ mcpp.exe       │                    │ ClangXref.dll        │
│ ┌────────────┐ │  P/Invoke (Cdecl)  │ ┌──────────────────┐ │
│ │ CppIndexer │◄├───────────────────►│ │IndexDataConsumer │ │
│ └────────────┘ │  7 callbacks       │ │PPCallbacks       │ │
│ ┌────────────┐ │                    │ └──────────────────┘ │
│ │ XrefDB     │ │                    └──────────────────────┘
│ │ (SQLite)   │ │
│ └────────────┘ │
└────────────────┘
```
