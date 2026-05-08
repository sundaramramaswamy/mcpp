# mcpp — C++ Cross-Reference MCP Server

Semantic C++ code analysis for Copilot. Indexes MSVC-compiled C++ projects
into a SQLite database, then serves cross-reference queries over MCP.

## Usage

```
mcpp index compile_commands.json -o xref.db [-j 16]
mcpp serve xref.db [--transport stdio|http] [--port 8080]
```

### `index` — Build cross-reference database

Parses every translation unit in a `compile_commands.json`, extracts
symbols, references, call edges, and inheritance relations into a SQLite
database.

```
mcpp index path/to/compile_commands.json -o xref.db
mcpp index path/to/build/                -o xref.db   # directory works too
mcpp index compile_commands.json -o xref.db -j 8      # limit threads
```

### `serve` — Start MCP server

Loads a pre-built `xref.db` and serves semantic queries over MCP.

```
mcpp serve xref.db                          # stdio transport (default)
mcpp serve xref.db --transport http --port 8080
```

**Available MCP tools:**
- `find_symbol` — search symbols by name
- `get_cpp_definition` — find where a symbol is defined
- `find_cpp_references` — find all references to a symbol
- `find_callers` / `find_callees` — call graph navigation
- `get_class_members` — list members of a class/struct
- `find_overrides` — find virtual method overrides
- `find_call_path` — find call chains between two functions
- `get_impact_radius` — estimate blast radius of a change

## Building

### Prerequisites

- .NET 8 SDK (or later)
- Visual Studio 2022 (for ClangXref.dll — native C++ DLL)
- LLVM 21.1.1 static libs (auto-downloaded by `setup-deps.ps1`)

### Build steps

```powershell
# 1. Download LLVM dependencies
pwsh scripts/setup-deps.ps1

# 2. Build native DLL (needs VS 2022 Developer Command Prompt)
msbuild src\ClangXref\ClangXref.vcxproj /p:Configuration=Release /p:Platform=x64

# 3. Build C# server
dotnet build -c Release src\Mcpp
```

### Generating compile_commands.json

For MSVC projects, common approaches:
- **CMake**: `cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON`
- **MSBuild binlog**: Use [bt](https://github.com/nicknash/bt) to extract from `.binlog`
- **clang-cl**: Clang's compilation database tools

## Architecture

```
C# (.NET 8)                           C++ (LLVM 21.1.1)
┌────────────────┐                    ┌──────────────────────┐
│ Mcpp.exe       │                    │ ClangXref.dll        │
│ ┌────────────┐ │  P/Invoke (Cdecl)  │ ┌──────────────────┐ │
│ │ CppIndexer │◄├───────────────────►│ │IndexDataConsumer │ │
│ └────────────┘ │  7 callbacks       │ │PPCallbacks       │ │
│ ┌────────────┐ │                    │ └──────────────────┘ │
│ │ XrefDB     │ │                    └──────────────────────┘
│ │ (SQLite)   │ │
│ └────────────┘ │
└────────────────┘
```

## License

TBD
