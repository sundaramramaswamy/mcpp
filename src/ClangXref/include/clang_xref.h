/*
 * clang_xref.h — C interface for the ClangXref indexer DLL.
 *
 * Wraps clang's C++ IndexDataConsumer API behind a stable C ABI so the
 * C# MCP server can P/Invoke it.  Each handle is single-threaded; the
 * C# side creates one per thread in Parallel.ForEach.
 */

#ifndef CLANG_XREF_H
#define CLANG_XREF_H

#ifdef __cplusplus
extern "C" {
#endif

#ifdef CLANGXREF_EXPORTS
#define CLANGXREF_API __declspec(dllexport)
#else
#define CLANGXREF_API __declspec(dllimport)
#endif

/* Opaque handle wrapping a CompilationDatabase + per-thread state. */
typedef void* clang_xref_index_t;

/* Return codes from clang_xref_index_tu (negative = error). */
enum clang_xref_result {
    CXREF_ERROR_GENERAL        = -1,  /* access violation or other SEH */
    CXREF_ERROR_STACK_OVERFLOW = -2,  /* stack overflow — needs larger stack */
};

/* Symbol kinds — matches XrefDatabase.cs SymbolKind enum. */
enum clang_xref_symbol_kind {
    CXREF_SK_UNKNOWN        = 0,
    CXREF_SK_FUNCTION       = 1,
    CXREF_SK_CLASS          = 2,
    CXREF_SK_STRUCT         = 3,
    CXREF_SK_ENUM           = 4,
    CXREF_SK_ENUM_CONSTANT  = 5,
    CXREF_SK_TYPEDEF        = 6,
    CXREF_SK_NAMESPACE      = 7,
    CXREF_SK_MACRO          = 8,
    CXREF_SK_FIELD          = 9,
    CXREF_SK_VARIABLE       = 10,
    CXREF_SK_METHOD         = 11,
    CXREF_SK_CONSTRUCTOR    = 12,
    CXREF_SK_DESTRUCTOR     = 13,
    CXREF_SK_USING          = 14,
    CXREF_SK_CONCEPT        = 15,
};

/* Reference kinds — bitfield matching clang::index::SymbolRole. */
enum clang_xref_ref_kind {
    CXREF_RK_DECLARATION = 1,
    CXREF_RK_DEFINITION  = 2,
    CXREF_RK_REFERENCE   = 4,
    CXREF_RK_CALL        = 8,
};

/* Relation kinds. */
enum clang_xref_relation_kind {
    CXREF_REL_BASE_OF       = 1,
    CXREF_REL_OVERRIDDEN_BY = 2,
};

/*
 * Callbacks — invoked from C++ for each indexed entity within a TU.
 * All string pointers are valid only for the duration of the callback.
 */

typedef void (*clang_xref_symbol_cb)(
    void* ctx,
    const char* usr,              /* Clang USR (unique symbol reference) */
    const char* name,             /* short name */
    const char* qualified_name,   /* fully-qualified name */
    const char* file,             /* definition file (repo-relative) */
    int line,
    int col,
    int kind,                     /* clang_xref_symbol_kind */
    const char* parent_usr        /* enclosing class/namespace USR, or NULL */
);

typedef void (*clang_xref_ref_cb)(
    void* ctx,
    const char* symbol_usr,       /* what symbol is referenced */
    const char* file,             /* where the reference occurs */
    int line,
    int col,
    int ref_kind,                 /* clang_xref_ref_kind (bitfield) */
    const char* container_usr,    /* enclosing function USR, or NULL */
    const char* type_spelling     /* full type at ref site, or NULL */
);

typedef void (*clang_xref_relation_cb)(
    void* ctx,
    const char* subject_usr,      /* e.g. base class */
    int predicate,                /* clang_xref_relation_kind */
    const char* object_usr        /* e.g. derived class */
);

typedef void (*clang_xref_macro_cb)(
    void* ctx,
    const char* name,             /* macro name */
    const char* file,             /* file (repo-relative) */
    int line,
    int col,
    int is_definition             /* 1 = #define, 0 = expansion */
);

typedef void (*clang_xref_include_cb)(
    void* ctx,
    const char* included_file,    /* resolved path (repo-relative) */
    const char* from_file,        /* file containing #include */
    int line,
    int is_angled                 /* 1 = <>, 0 = "" */
);

/*
 * Optional log callback — routes DLL diagnostics to the caller instead
 * of writing to stderr.  If NULL, falls back to llvm::errs().
 * String pointer valid only for the duration of the call.
 */
typedef void (*clang_xref_log_cb)(
    void* ctx,
    const char* message
);

/*
 * Create an indexing session.  Loads compile_commands.json from the
 * given directory.  Returns NULL on failure.
 *
 * Each returned handle is NOT thread-safe.  Create one per thread.
 */
CLANGXREF_API clang_xref_index_t
clang_xref_create(const char* compile_commands_dir,
                  clang_xref_log_cb on_log,
                  void* log_ctx);

/*
 * Destroy a session and free all resources.
 */
CLANGXREF_API void
clang_xref_destroy(clang_xref_index_t idx);

/*
 * Index a single translation unit.
 *
 * |source_file| must match an entry in compile_commands.json.
 * Callbacks fire synchronously.  |ctx| is passed through to each callback.
 * Any callback may be NULL to skip that category.
 *
 * Returns the number of references found, or a negative clang_xref_result
 * on error: CXREF_ERROR_GENERAL (-1) for access violations (retryable on
 * any thread), CXREF_ERROR_STACK_OVERFLOW (-2) for stack overflow (needs
 * a thread with larger stack to retry).
 */
CLANGXREF_API int
clang_xref_index_tu(
    clang_xref_index_t idx,
    const char* source_file,
    clang_xref_symbol_cb on_symbol,
    clang_xref_ref_cb on_ref,
    clang_xref_relation_cb on_relation,
    clang_xref_macro_cb on_macro,
    clang_xref_include_cb on_include,
    void* ctx
);

/*
 * Return a human-readable version string (e.g. "ClangXref 1.0 (LLVM 21.1.1)").
 * The returned pointer is statically allocated.
 */
CLANGXREF_API const char*
clang_xref_version(void);

/*
 * Test PCH generation and consumption for a single TU.
 * Indexes the TU twice (without PCH, then with PCH) and compares
 * ref counts and timing.  Writes results to stderr.
 *
 * Returns 0 on success, -1 on error.
 */
CLANGXREF_API int
clang_xref_test_pch(
    clang_xref_index_t idx,
    const char* source_file
);

/*
 * Generate PCH files for all TU groups.  Call once after create(),
 * before indexing.  PCH files are written to |output_dir|.
 * Returns the number of groups generated, or -1 on error.
 */
CLANGXREF_API int
clang_xref_prepare_pch(clang_xref_index_t idx, const char* output_dir);

#ifdef __cplusplus
}
#endif

#endif /* CLANG_XREF_H */
