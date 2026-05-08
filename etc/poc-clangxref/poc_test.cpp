/*
 * poc_test.cpp - PoC: Load ClangXref.dll and index a single TU to verify
 * that IndexDataConsumer catches template references are present.
 *
 * Build: cl /EHsc /std:c++17 poc_test.cpp /Fe:poc_test.exe
 * Run:   poc_test.exe <repo_root> [source_file]
 *
 * Default TU: src/main.cpp (uses vector<T>).
 */

#include <cstdio>
#include <cstdlib>
#include <string>
#include <Windows.h>

// Function pointer types matching clang_xref.h
typedef void* clang_xref_index_t;
typedef void (*clang_xref_log_cb)(void*, const char*);
typedef void (*clang_xref_symbol_cb)(void*, const char*, const char*, const char*, const char*, int, int, int, const char*);
typedef void (*clang_xref_ref_cb)(void*, const char*, const char*, int, int, int, const char*, const char*);
typedef void (*clang_xref_relation_cb)(void*, const char*, int, const char*);
typedef void (*clang_xref_macro_cb)(void*, const char*, const char*, int, int, int);
typedef void (*clang_xref_include_cb)(void*, const char*, const char*, int, int);

typedef clang_xref_index_t (*fn_create)(const char*, clang_xref_log_cb, void*);
typedef void (*fn_destroy)(clang_xref_index_t);
typedef int (*fn_index_tu)(clang_xref_index_t, const char*, clang_xref_symbol_cb, clang_xref_ref_cb, clang_xref_relation_cb, clang_xref_macro_cb, clang_xref_include_cb, void*);
typedef const char* (*fn_version)(void);

// Counters
struct Stats {
    int symbols = 0;
    int refs = 0;
    int relations = 0;
    int template_refs = 0;
    int macros_defined = 0;
    int macros_expanded = 0;
    int includes = 0;
    int typed_refs = 0;
};

static void on_log(void* ctx, const char* message) {
    fprintf(stderr, "[DLL] %s\n", message);
}

static void on_symbol(void* ctx, const char* usr, const char* name, const char* qname,
                       const char* file, int line, int col, int kind,
                       const char* parent_usr) {
    auto* s = static_cast<Stats*>(ctx);
    s->symbols++;
}

static void on_ref(void* ctx, const char* symbol_usr, const char* file,
                    int line, int col, int ref_kind, const char* container_usr,
                    const char* type_spelling) {
    auto* s = static_cast<Stats*>(ctx);
    s->refs++;
    if (type_spelling && type_spelling[0])
        s->typed_refs++;
    // Check if this references vector
    if (symbol_usr && strstr(symbol_usr, "vector")) {
        s->template_refs++;
        printf("  vector ref at %s:%d (kind=%d, type=%s)\n",
               file, line, ref_kind, type_spelling ? type_spelling : "(none)");
    }
}

static void on_relation(void* ctx, const char* subj, int pred, const char* obj) {
    auto* s = static_cast<Stats*>(ctx);
    s->relations++;
}

static void on_macro(void* ctx, const char* name, const char* file,
                      int line, int col, int is_definition) {
    auto* s = static_cast<Stats*>(ctx);
    if (is_definition)
        s->macros_defined++;
    else
        s->macros_expanded++;
}

static void on_include(void* ctx, const char* included_file, const char* from_file,
                        int line, int is_angled) {
    auto* s = static_cast<Stats*>(ctx);
    s->includes++;
}

int main(int argc, char* argv[]) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <repo_root> [source_file]\n", argv[0]);
        return 1;
    }

    const char* repo_root = argv[1];
    const char* source_file = argc > 2 ? argv[2] : "dxaml\\xcp\\core\\animation\\timer.cpp";

    // Load DLL
    std::string dllPath = std::string(repo_root) + "\\tools\\cpp-xref-engine\\bin\\ClangXref.dll";
    HMODULE hDll = LoadLibraryA(dllPath.c_str());
    if (!hDll) {
        fprintf(stderr, "Failed to load %s (error %lu)\n", dllPath.c_str(), GetLastError());
        return 1;
    }

    auto create = (fn_create)GetProcAddress(hDll, "clang_xref_create");
    auto destroy = (fn_destroy)GetProcAddress(hDll, "clang_xref_destroy");
    auto index_tu = (fn_index_tu)GetProcAddress(hDll, "clang_xref_index_tu");
    auto version = (fn_version)GetProcAddress(hDll, "clang_xref_version");

    if (!create || !destroy || !index_tu || !version) {
        fprintf(stderr, "Failed to resolve DLL exports\n");
        FreeLibrary(hDll);
        return 1;
    }

    printf("DLL: %s\nVersion: %s\n\n", dllPath.c_str(), version());

    // Create session (loads compile_commands.json)
    printf("Loading compile_commands.json from %s...\n", repo_root);
    auto idx = create(repo_root, on_log, nullptr);
    if (!idx) {
        fprintf(stderr, "clang_xref_create failed\n");
        FreeLibrary(hDll);
        return 1;
    }

    // Index one TU
    Stats stats;
    printf("Indexing %s...\n\n", source_file);
    int rc = index_tu(idx, source_file, on_symbol, on_ref, on_relation,
                      on_macro, on_include, &stats);

    printf("\n--- Results ---\n");
    printf("Return code: %d\n", rc);
    printf("Symbols:     %d\n", stats.symbols);
    printf("References:  %d\n", stats.refs);
    printf("Relations:   %d\n", stats.relations);
    printf("vector refs: %d\n", stats.template_refs);
    printf("Macros defined:  %d\n", stats.macros_defined);
    printf("Macros expanded: %d\n", stats.macros_expanded);
    printf("Includes:        %d\n", stats.includes);
    printf("Typed refs:      %d\n", stats.typed_refs);

    if (stats.template_refs > 0) {
        printf("\n*** SUCCESS: Found vector references that ClangSharp misses! ***\n");
    } else {
        printf("\n*** WARNING: No vector references found. Check source_file path. ***\n");
    }

    destroy(idx);
    FreeLibrary(hDll);
    return stats.template_refs > 0 ? 0 : 1;
}
