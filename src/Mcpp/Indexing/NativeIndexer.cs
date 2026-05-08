using System.Runtime.InteropServices;

namespace Mcpp.Indexing;

/// <summary>
/// P/Invoke declarations for ClangXref.dll -- the native C++ indexer DLL
/// that wraps clang's IndexDataConsumer API behind a stable C ABI.
///
/// See src/ClangXref/include/clang_xref.h for the C interface contract.
/// </summary>
internal static partial class NativeIndexer
{
    private const string DllName = "ClangXref";

    // --- Enums matching clang_xref.h ---

    /// <summary>
    /// Negative return codes from clang_xref_index_tu.
    /// Positive values = ref count (success).
    /// </summary>
    public enum IndexResult
    {
        ErrorGeneral = -1,       // access violation or other SEH
        ErrorStackOverflow = -2, // needs retry on larger-stack thread
    }

    public enum SymbolKind
    {
        Unknown = 0,
        Function = 1,
        Class = 2,
        Struct = 3,
        Enum = 4,
        EnumConstant = 5,
        Typedef = 6,
        Namespace = 7,
        Macro = 8,
        Field = 9,
        Variable = 10,
        Method = 11,
        Constructor = 12,
        Destructor = 13,
        Using = 14,
        Concept = 15,
    }

    [Flags]
    public enum RefKind
    {
        Declaration = 1,
        Definition = 2,
        Reference = 4,
        Call = 8,
    }

    public enum RelationKind
    {
        BaseOf = 1,
        OverriddenBy = 2,
    }

    // --- Callback delegates (native → managed) ---
    // CallingConvention.Cdecl matches the C ABI of the DLL.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SymbolCallback(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string usr,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string qualifiedName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        int line,
        int col,
        int kind,
        IntPtr parentUsr);  // may be NULL; use Marshal.PtrToStringUTF8

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RefCallback(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string symbolUsr,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        int line,
        int col,
        int refKind,
        IntPtr containerUsr,  // may be NULL; use Marshal.PtrToStringUTF8
        IntPtr typeSpelling); // may be NULL; use Marshal.PtrToStringUTF8

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RelationCallback(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string subjectUsr,
        int predicate,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string objectUsr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MacroCallback(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        int line,
        int col,
        int isDefinition);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void IncludeCallback(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string includedFile,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fromFile,
        int line,
        int isAngled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    // --- P/Invoke declarations ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr clang_xref_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string compileCommandsDir,
        LogCallback? onLog,
        IntPtr logCtx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void clang_xref_destroy(IntPtr idx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int clang_xref_index_tu(
        IntPtr idx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceFile,
        SymbolCallback onSymbol,
        RefCallback onRef,
        RelationCallback onRelation,
        MacroCallback? onMacro,
        IncludeCallback? onInclude,
        IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr clang_xref_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int clang_xref_prepare_pch(
        IntPtr idx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outputDir);

    // --- Helpers ---

    public static string GetVersion()
    {
        var ptr = clang_xref_version();
        return Marshal.PtrToStringUTF8(ptr) ?? "unknown";
    }

    /// <summary>
    /// Registers a DLL import resolver that searches for ClangXref.dll next to
    /// the managed assembly and in the MCP server bin/ directory.
    /// Call once at startup.
    /// </summary>
    public static void RegisterResolver(string? repoRoot = null)
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeIndexer).Assembly, (name, asm, paths) =>
        {
            if (!name.Equals(DllName, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            // 1. Default search (next to the exe, system PATH)
            if (NativeLibrary.TryLoad(name, asm, paths, out var handle))
                return handle;

            // 2. Repo bin/ directory (when launched via `dotnet run` from source)
            if (repoRoot != null)
            {
                var binDir = Path.Combine(repoRoot, "bin", $"{DllName}.dll");
                if (NativeLibrary.TryLoad(binDir, out handle))
                    return handle;
            }

            return IntPtr.Zero;
        });
    }
}

/// <summary>
/// Safe wrapper around a single ClangXref session handle.
/// NOT thread-safe -- create one per thread (e.g. Parallel.ForEach thread-local).
/// Produces <see cref="TuResult"/> objects compatible with <see cref="XrefDatabase.BulkInsert"/>.
/// </summary>
internal sealed class XrefSession : IDisposable
{
    private IntPtr _handle;
    private readonly string _repoRoot;
    private readonly NativeIndexer.LogCallback? _logCallback;

    internal IntPtr Handle => _handle;

    public XrefSession(string compileCommandsDir, string repoRoot)
    {
        _logCallback = (ctx, message) =>
            McpLogger.Log("ClangXref", message);

        _handle = NativeIndexer.clang_xref_create(
            compileCommandsDir, _logCallback, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"clang_xref_create failed for '{compileCommandsDir}'. " +
                "Check that compile_commands.json exists and is valid.");
        _repoRoot = repoRoot;
    }

    /// <summary>
    /// Index a single translation unit, returning collected symbols/refs/calls
    /// and the raw DLL return code.
    /// Returns (null, returnCode) on error (negative returnCode).
    /// </summary>
    public (TuResult? Result, int ReturnCode) IndexTu(string sourceFile)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(XrefSession));

        var result = new TuResult();

        // Callbacks capture `result` via closure.  The ctx parameter is unused
        // (IntPtr.Zero) -- closures are cleaner than GCHandle for this case.
        NativeIndexer.SymbolCallback onSymbol = (ctx, usr, name, qname, file, line, col, kind, parentUsrPtr) =>
        {
            var kindStr = MapSymbolKind((NativeIndexer.SymbolKind)kind);
            var parentUsr = parentUsrPtr != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(parentUsrPtr)
                : null;
            result.Symbols.Add(new SymbolInsert(usr, name, qname, kindStr, file, line, col, parentUsr));
        };

        NativeIndexer.RefCallback onRef = (ctx, symbolUsr, file, line, col, refKind, containerUsrPtr, typeSpellingPtr) =>
        {
            var kindStr = MapRefKind((NativeIndexer.RefKind)refKind);
            result.Refs.Add(new RefInsert(symbolUsr, file, line, col, kindStr));

            // Build call graph edges when we know the enclosing function
            var containerUsr = containerUsrPtr != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(containerUsrPtr)
                : null;
            if (containerUsr != null && ((NativeIndexer.RefKind)refKind & NativeIndexer.RefKind.Call) != 0)
            {
                result.Calls.Add(new CallInsert(containerUsr, symbolUsr, file, line));
            }

            // Capture type spelling for template-typed refs (e.g. "vector<int>")
            // Stored in TuResult for future DB enrichment.
            // TODO: wire into DB schema when refs table gains a type_detail column.
        };

        NativeIndexer.RelationCallback onRelation = (ctx, subjectUsr, predicate, objectUsr) =>
        {
            var relKind = (NativeIndexer.RelationKind)predicate;
            var kindStr = relKind switch
            {
                NativeIndexer.RelationKind.BaseOf => "base_of",
                NativeIndexer.RelationKind.OverriddenBy => "overridden_by",
                _ => "relation",
            };
            // Store both USRs in the relations table for precise queries
            result.Relations.Add(new RelationInsert(subjectUsr, kindStr, objectUsr));
            // Keep ref entry for backward compatibility (search tools use overridden_by refs)
            result.Refs.Add(new RefInsert(subjectUsr, "", 0, 0, kindStr));
        };

        NativeIndexer.MacroCallback onMacro = (ctx, name, file, line, col, isDef) =>
        {
            if (isDef != 0)
            {
                // Macro definition: register as a symbol
                var usr = $"macro@{file}:{line}:{name}";
                result.Symbols.Add(new SymbolInsert(usr, name, name, "macro", file, line, col));
            }
            else
            {
                // Macro expansion: register as a reference
                var usr = $"macro@{name}";
                result.Refs.Add(new RefInsert(usr, file, line, col, "macro_expansion"));
            }
        };

        NativeIndexer.IncludeCallback onInclude = (ctx, includedFile, fromFile, line, isAngled) =>
        {
            // Include directives: store as refs from the including file to the included
            result.Refs.Add(new RefInsert(
                $"file@{includedFile}", fromFile, line, 0,
                isAngled != 0 ? "include_system" : "include_local"));
        };

        var count = NativeIndexer.clang_xref_index_tu(
            _handle, sourceFile, onSymbol, onRef, onRelation, onMacro, onInclude, IntPtr.Zero);

        // Prevent GC from collecting delegates while native code still holds pointers
        GC.KeepAlive(onSymbol);
        GC.KeepAlive(onRef);
        GC.KeepAlive(onRelation);
        GC.KeepAlive(onMacro);
        GC.KeepAlive(onInclude);

        return count >= 0 ? (result, count) : (null, count);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeIndexer.clang_xref_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // --- Kind mapping: C enum → DB string (matches existing CppIndexer/TuVisitor) ---

    private static string MapSymbolKind(NativeIndexer.SymbolKind kind) => kind switch
    {
        NativeIndexer.SymbolKind.Function => "function",
        NativeIndexer.SymbolKind.Class => "class",
        NativeIndexer.SymbolKind.Struct => "struct",
        NativeIndexer.SymbolKind.Enum => "enum",
        NativeIndexer.SymbolKind.EnumConstant => "enum_constant",
        NativeIndexer.SymbolKind.Typedef => "typedef",
        NativeIndexer.SymbolKind.Namespace => "namespace",
        NativeIndexer.SymbolKind.Macro => "macro",
        NativeIndexer.SymbolKind.Field => "field",
        NativeIndexer.SymbolKind.Variable => "variable",
        NativeIndexer.SymbolKind.Method => "method",
        NativeIndexer.SymbolKind.Constructor => "constructor",
        NativeIndexer.SymbolKind.Destructor => "destructor",
        NativeIndexer.SymbolKind.Using => "using",
        NativeIndexer.SymbolKind.Concept => "concept",
        _ => "unknown",
    };

    private static string MapRefKind(NativeIndexer.RefKind kind)
    {
        // Priority: def > decl > call > ref
        if ((kind & NativeIndexer.RefKind.Definition) != 0) return "def";
        if ((kind & NativeIndexer.RefKind.Declaration) != 0) return "decl";
        if ((kind & NativeIndexer.RefKind.Call) != 0) return "call";
        return "ref";
    }
}
