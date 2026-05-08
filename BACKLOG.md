# mcpp — Backlog

Parked features and known gaps. Pick these up when the core is working end-to-end.

## Must-do (before first real use)

- [x] **Wire ILogger with McpLogger** — done. `McpLoggerProvider` registered in
  `Program.cs` `RunServe()`. Default providers cleared to protect stdout.

- [x] **Build ClangXref.dll** — done. LLVM 21.1.1 deps downloaded, DLL built
  (28 MB) at `bin/ClangXref.dll`.

- [ ] **End-to-end test** — index a real project with `mcpp index`, then `mcpp serve`.
  Test candidates: `D:\Code\tryouts\ProtoMario` and `D:\Code\tryouts\CG\Partize`.
  These need a `compile_commands.json` first (CMake or manual).

## Should-do (polish)

- [ ] **Colored CLI output** — `--color auto|always|never` for subcommand output
  (index progress, serve status, errors). Apply to McpLogger stderr too.

- [ ] **`--help` text** — verify `mcpp -h`, `mcpp index -h`, `mcpp serve -h` all
  look good and have examples.

## Nice-to-have (future)

- [ ] **GCC/Clang compdb support** — the flag adjuster currently assumes MSVC
  `cl.exe` flags (`--driver-mode=cl`). A pass-through mode for GCC/Clang-native
  compdbs would broaden applicability.

- [ ] **Incremental re-indexing** — detect changed TUs and re-index only those
  instead of full rebuild.

- [ ] **HTTP transport** — add `--transport http` for remote serving (would need
  `ModelContextProtocol.AspNetCore` package and `Sdk.Web`).

- [ ] **LoggingMcpServerTool wrapper** — per-tool invocation logging decorator.
  The old `DelegatingMcpServerTool` from the preview SDK was removed in 1.x.
  Implement equivalent via `ILogger` injection into tools, or a custom middleware
  if the SDK supports it.

- [ ] **Cross-platform ClangXref** — currently Windows x64 only (static-links LLVM).
  Linux would need a separate build with Linux LLVM libs.
