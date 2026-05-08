/*
 * clang_xref.cpp — IndexDataConsumer-based C++ indexer with C interface.
 *
 * Uses clang::index::indexTopLevelDecls() to visit every symbol, reference,
 * and relation in a translation unit.  This is the same API that clangd uses,
 * giving us complete coverage of template types, dependent names, and implicit
 * instantiations that libclang's cursor visitor misses.
 *
 * Also hooks PPCallbacks for macro definitions/expansions and #include tracking.
 */

#include "clang_xref.h"

// LLVM/Clang headers emit hundreds of warnings (C4244, C4267, C4251, C4275, etc.)
// that are irrelevant since we link statically. Suppress them here only.
#pragma warning(push, 0)

#include <clang/AST/Expr.h>
#include <clang/AST/Type.h>
#include <clang/Frontend/CompilerInstance.h>
#include <clang/Frontend/FrontendAction.h>
#include <clang/Frontend/FrontendActions.h>
#include <clang/Index/IndexDataConsumer.h>
#include <clang/Index/IndexingAction.h>
#include <clang/Index/IndexingOptions.h>
#include <clang/Index/USRGeneration.h>
#include <clang/Lex/PPCallbacks.h>
#include <clang/Lex/PreprocessorOptions.h>
#include <clang/Lex/Preprocessor.h>
#include <clang/Tooling/CompilationDatabase.h>
#include <clang/Tooling/Tooling.h>
#include <llvm/ADT/SmallString.h>
#include <llvm/Support/CommandLine.h>
#include <llvm/Support/FileSystem.h>
#include <llvm/Support/Format.h>
#include <llvm/Support/raw_ostream.h>

#pragma warning(pop)

#include <atomic>
#include <chrono>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>

#ifdef _WIN32
#include <windows.h>
#include <malloc.h>  // _resetstkoflw
#endif

/* -- Helpers -------------------------------------------------------------- */

static std::string getUSR(const clang::Decl* D) {
    llvm::SmallString<128> Buf;
    if (clang::index::generateUSRForDecl(D, Buf))
        return {};  // failed
    return std::string(Buf.str());
}

static int mapSymbolKind(clang::index::SymbolKind K) {
    using SK = clang::index::SymbolKind;
    switch (K) {
    case SK::Function:           return CXREF_SK_FUNCTION;
    case SK::Class:              return CXREF_SK_CLASS;
    case SK::Struct:             return CXREF_SK_STRUCT;
    case SK::Enum:               return CXREF_SK_ENUM;
    case SK::EnumConstant:       return CXREF_SK_ENUM_CONSTANT;
    case SK::TypeAlias:          return CXREF_SK_TYPEDEF;
    case SK::Namespace:          return CXREF_SK_NAMESPACE;
    case SK::Macro:              return CXREF_SK_MACRO;
    case SK::Field:              return CXREF_SK_FIELD;
    case SK::Variable:           return CXREF_SK_VARIABLE;
    case SK::InstanceMethod:     return CXREF_SK_METHOD;
    case SK::ClassMethod:        return CXREF_SK_METHOD;
    case SK::StaticMethod:       return CXREF_SK_METHOD;
    case SK::Constructor:        return CXREF_SK_CONSTRUCTOR;
    case SK::Destructor:         return CXREF_SK_DESTRUCTOR;
    case SK::Using:              return CXREF_SK_USING;
    case SK::Concept:            return CXREF_SK_CONCEPT;
    default:                     return CXREF_SK_UNKNOWN;
    }
}

static int mapRoles(clang::index::SymbolRoleSet Roles) {
    int out = 0;
    using R = clang::index::SymbolRole;
    if (Roles & (unsigned)R::Declaration)  out |= CXREF_RK_DECLARATION;
    if (Roles & (unsigned)R::Definition)   out |= CXREF_RK_DEFINITION;
    if (Roles & (unsigned)R::Reference)    out |= CXREF_RK_REFERENCE;
    if (Roles & (unsigned)R::Call)         out |= CXREF_RK_CALL;
    return out;
}

/* -- PCH group management ------------------------------------------------- */

/// A group of TUs sharing the same precompiled header configuration.
/// Grouped by /Fp path — TUs compiled with the same /Fp have identical
/// PCH settings (same header, same flags) guaranteed by the build system.
struct PchGroup {
    std::string HeaderName;        // from /Yu, e.g. "precomp.h"
    std::string FpPath;            // from /Fp, grouping key
    std::string RepresentativeTU;  // first TU seen — borrow its flags for PCH gen
    std::vector<std::string> Sources; // all source files sharing this PCH
    std::string GeneratedPchPath;  // set after PCH generation by prepare_pch()
};

/// Scans CompilationDatabase entries for /Yu + /Fp flags and groups TUs
/// by their PCH configuration.  Immutable after Scan() — safe to share.
class PchManager {
public:
    void Scan(const clang::tooling::CompilationDatabase& CDB) {
        std::unordered_map<std::string, size_t> FpToGroup;

        auto AllCmds = CDB.getAllCompileCommands();
        TotalTUs_ = AllCmds.size();

        for (const auto& Cmd : AllCmds) {
            std::string YuHeader, FpPath;
            for (const auto& Arg : Cmd.CommandLine) {
                llvm::StringRef A(Arg);
                if (A.size() > 3) {
                    auto Prefix = A.substr(0, 3);
                    if (Prefix.equals_insensitive("/Yu"))
                        YuHeader = stripQuotes(A.substr(3));
                    else if (Prefix.equals_insensitive("/Fp"))
                        FpPath = stripQuotes(A.substr(3));
                }
            }

            if (YuHeader.empty() || FpPath.empty())
                continue;

            auto it = FpToGroup.find(FpPath);
            size_t idx;
            if (it == FpToGroup.end()) {
                idx = Groups_.size();
                Groups_.push_back({YuHeader, FpPath, Cmd.Filename, {}});
                FpToGroup[FpPath] = idx;
            } else {
                idx = it->second;
            }

            Groups_[idx].Sources.push_back(Cmd.Filename);
            SourceToGroup_[Cmd.Filename] = idx;
        }
    }

    const PchGroup* GetGroup(llvm::StringRef SourceFile) const {
        auto it = SourceToGroup_.find(SourceFile.str());
        if (it == SourceToGroup_.end())
            return nullptr;
        return &Groups_[it->second];
    }

    /// Get the generated PCH path for a source file, or empty if not generated.
    std::string GetPchPath(llvm::StringRef SourceFile) const {
        auto it = SourceToGroup_.find(SourceFile.str());
        if (it == SourceToGroup_.end())
            return {};
        return Groups_[it->second].GeneratedPchPath;
    }

    std::vector<PchGroup>& Groups() { return Groups_; }
    const std::vector<PchGroup>& GetGroups() const { return Groups_; }
    size_t GroupCount() const { return Groups_.size(); }
    size_t CoveredTUCount() const { return SourceToGroup_.size(); }
    size_t TotalTUCount() const { return TotalTUs_; }

private:
    std::vector<PchGroup> Groups_;
    std::unordered_map<std::string, size_t> SourceToGroup_;
    size_t TotalTUs_ = 0;

    static std::string stripQuotes(llvm::StringRef S) {
        auto Str = S.str();
        if (Str.size() >= 2 && Str.front() == '"' && Str.back() == '"')
            return Str.substr(1, Str.size() - 2);
        return Str;
    }
};

/* -- Per-session state ---------------------------------------------------- */

// Shared CompilationDatabase — loaded once, immutable, safe to share.
// Each XrefSession holds a shared_ptr to avoid redundant 27MB JSON parses.
struct SharedCDB {
    std::shared_ptr<clang::tooling::CompilationDatabase> CDB;
    std::string BaseDir;
    PchManager Pch;
};

struct XrefSession {
    std::shared_ptr<SharedCDB> Shared;
    clang_xref_log_cb OnLog = nullptr;
    void* LogCtx = nullptr;
};

/* -- Logging helper ------------------------------------------------------- */

/// Thread-safe log emitter.  Calls the managed callback if set, otherwise
/// writes to stderr.  Uses a stack buffer to avoid heap allocation.
static void emit_log(const XrefSession* S, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);

    if (S && S->OnLog) {
        S->OnLog(S->LogCtx, buf);
    } else {
        llvm::errs() << buf << "\n";
    }
}

/// Overload for contexts without a session (standalone PoC, etc.)
static void emit_log_raw(clang_xref_log_cb on_log, void* ctx,
                          const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);

    if (on_log) {
        on_log(ctx, buf);
    } else {
        llvm::errs() << buf << "\n";
    }
}

/* -- Path helper (shared by consumer and PP callbacks) -------------------- */

static std::string makeRelative(llvm::StringRef Path, const std::string& BaseDir) {
    if (BaseDir.empty())
        return Path.str();
    llvm::SmallString<256> Normalized(Path);
    llvm::sys::path::native(Normalized);
    llvm::SmallString<256> Base(BaseDir);
    llvm::sys::path::native(Base);
    if (Normalized.starts_with(Base)) {
        auto Rel = Normalized.substr(Base.size());
        if (!Rel.empty() && llvm::sys::path::is_separator(Rel[0]))
            Rel = Rel.substr(1);
        return Rel.str();
    }
    return Path.str();
}

// Returns true if the path is under BaseDir (i.e. a repo-local file).
// System headers (Windows SDK, CRT, STL) return false.
static bool isRepoLocal(const std::string& RelPath) {
    if (RelPath.empty()) return false;
    // makeRelative returns an absolute path for non-repo files
    return !llvm::sys::path::is_absolute(RelPath);
}

/* -- PPCallbacks for macros and includes ---------------------------------- */

class XrefPPCallbacks : public clang::PPCallbacks {
public:
    XrefPPCallbacks(clang_xref_macro_cb onMacro,
                    clang_xref_include_cb onInclude,
                    void* ctx,
                    const clang::SourceManager& SM,
                    const std::string& baseDir)
        : OnMacro(onMacro), OnInclude(onInclude), Ctx(ctx),
          SM(SM), BaseDir(baseDir) {}

    void MacroDefined(const clang::Token& MacroNameTok,
                      const clang::MacroDirective* MD) override {
        if (!OnMacro || !MD)
            return;
        auto Loc = MacroNameTok.getLocation();
        if (Loc.isInvalid() || SM.isInSystemHeader(Loc))
            return;
        auto PLoc = SM.getPresumedLoc(Loc);
        if (PLoc.isInvalid())
            return;
        std::string File = makeRelative(PLoc.getFilename(), BaseDir);
        OnMacro(Ctx, MacroNameTok.getIdentifierInfo()->getName().str().c_str(),
                File.c_str(), PLoc.getLine(), PLoc.getColumn(), /*is_definition=*/1);
    }

    void MacroExpands(const clang::Token& MacroNameTok,
                      const clang::MacroDefinition& MD,
                      clang::SourceRange Range,
                      const clang::MacroArgs* Args) override {
        if (!OnMacro)
            return;
        // Skip if expansion site is in a system header
        auto Loc = MacroNameTok.getLocation();
        if (Loc.isInvalid() || SM.isInSystemHeader(Loc))
            return;
        // Skip if the macro itself was defined in a system header
        // (e.g. _MSC_VER, WINAPI, STDMETHODCALLTYPE)
        auto* MI = MD.getMacroInfo();
        if (MI) {
            auto DefLoc = MI->getDefinitionLoc();
            if (DefLoc.isValid() && SM.isInSystemHeader(DefLoc))
                return;
        }
        auto PLoc = SM.getPresumedLoc(Loc);
        if (PLoc.isInvalid())
            return;
        std::string File = makeRelative(PLoc.getFilename(), BaseDir);
        OnMacro(Ctx, MacroNameTok.getIdentifierInfo()->getName().str().c_str(),
                File.c_str(), PLoc.getLine(), PLoc.getColumn(), /*is_definition=*/0);
    }

    void InclusionDirective(clang::SourceLocation HashLoc,
                            const clang::Token& IncludeTok,
                            llvm::StringRef FileName,
                            bool IsAngled,
                            clang::CharSourceRange FilenameRange,
                            clang::OptionalFileEntryRef File,
                            llvm::StringRef SearchPath,
                            llvm::StringRef RelativePath,
                            const clang::Module* SuggestedModule,
                            bool ModuleImported,
                            clang::SrcMgr::CharacteristicKind FileType) override {
        if (!OnInclude)
            return;
        if (HashLoc.isInvalid() || SM.isInSystemHeader(HashLoc))
            return;
        auto PLoc = SM.getPresumedLoc(HashLoc);
        if (PLoc.isInvalid())
            return;

        // Resolve the included file's actual path
        std::string IncludedPath;
        if (File)
            IncludedPath = makeRelative(
                File->getFileEntry().tryGetRealPathName(), BaseDir);
        else
            IncludedPath = FileName.str();  // fallback to unresolved

        std::string FromFile = makeRelative(PLoc.getFilename(), BaseDir);
        OnInclude(Ctx, IncludedPath.c_str(), FromFile.c_str(),
                  PLoc.getLine(), IsAngled ? 1 : 0);
    }

private:
    clang_xref_macro_cb OnMacro;
    clang_xref_include_cb OnInclude;
    void* Ctx;
    const clang::SourceManager& SM;
    std::string BaseDir;
};

/* -- IndexDataConsumer ---------------------------------------------------- */

class XrefConsumer : public clang::index::IndexDataConsumer {
public:
    XrefConsumer(clang_xref_symbol_cb onSym,
                 clang_xref_ref_cb onRef,
                 clang_xref_relation_cb onRel,
                 void* ctx,
                 const std::string& baseDir)
        : OnSymbol(onSym), OnRef(onRef), OnRelation(onRel),
          Ctx(ctx), BaseDir(baseDir) {}

    void initialize(clang::ASTContext& ASTCtx) override {
        this->ASTCtx = &ASTCtx;
    }

    bool handleDeclOccurrence(
        const clang::Decl* D,
        clang::index::SymbolRoleSet Roles,
        llvm::ArrayRef<clang::index::SymbolRelation> Relations,
        clang::SourceLocation Loc,
        clang::index::IndexDataConsumer::ASTNodeInfo ASTNode) override {

        if (!D || Loc.isInvalid())
            return true;

        auto& SM = ASTCtx->getSourceManager();
        auto PLoc = SM.getPresumedLoc(Loc);
        if (PLoc.isInvalid())
            return true;

        std::string File = makeRelative(PLoc.getFilename(), BaseDir);

        // Skip symbols/refs in system headers (Windows SDK, CRT, STL).
        // Only index repo-local files to keep the DB manageable.
        if (!isRepoLocal(File))
            return true;

        int Line = PLoc.getLine();
        int Col = PLoc.getColumn();

        std::string USR = getUSR(D);
        if (USR.empty())
            return true;

        int RolesBits = mapRoles(Roles);

        // Emit symbol for declarations/definitions
        if (RolesBits & (CXREF_RK_DECLARATION | CXREF_RK_DEFINITION)) {
            if (OnSymbol) {
                auto Info = clang::index::getSymbolInfo(D);
                std::string Name = getShortName(D);
                std::string QualName = getQualifiedName(D);

                // Parent: enclosing class/namespace for member grouping
                const char* ParentUSR = nullptr;
                std::string ParentBuf;
                if (auto* ParentDC = D->getDeclContext()) {
                    if (auto* ParentD = llvm::dyn_cast<clang::Decl>(ParentDC)) {
                        if (llvm::isa<clang::CXXRecordDecl>(ParentD) ||
                            llvm::isa<clang::NamespaceDecl>(ParentD) ||
                            llvm::isa<clang::EnumDecl>(ParentD)) {
                            ParentBuf = getUSR(ParentD);
                            if (!ParentBuf.empty())
                                ParentUSR = ParentBuf.c_str();
                        }
                    }
                }

                OnSymbol(Ctx, USR.c_str(), Name.c_str(), QualName.c_str(),
                         File.c_str(), Line, Col, mapSymbolKind(Info.Kind),
                         ParentUSR);
            }
        }

        // Emit reference
        if (OnRef) {
            // Container: enclosing function/method for call graph
            const char* ContainerUSR = nullptr;
            std::string ContUSRBuf;
            if (ASTNode.ContainerDC) {
                if (auto* ContD = llvm::dyn_cast<clang::Decl>(ASTNode.ContainerDC)) {
                    if (llvm::isa<clang::FunctionDecl>(ContD) ||
                        llvm::isa<clang::ObjCMethodDecl>(ContD)) {
                        ContUSRBuf = getUSR(ContD);
                        if (!ContUSRBuf.empty())
                            ContainerUSR = ContUSRBuf.c_str();
                    }
                }
            }

            // Type spelling at ref site (e.g. "vector<int>")
            const char* TypeSpelling = nullptr;
            std::string TypeBuf;
            if (ASTNode.OrigE) {
                clang::QualType QT = ASTNode.OrigE->getType();
                if (!QT.isNull()) {
                    TypeBuf = QT.getAsString();
                    if (!TypeBuf.empty())
                        TypeSpelling = TypeBuf.c_str();
                }
            }

            OnRef(Ctx, USR.c_str(), File.c_str(), Line, Col,
                  RolesBits, ContainerUSR, TypeSpelling);
            ++RefCount;
        }

        // Emit relations (base-of, override)
        if (OnRelation) {
            for (auto& Rel : Relations) {
                std::string SubjUSR = getUSR(Rel.RelatedSymbol);
                if (SubjUSR.empty())
                    continue;
                if (Rel.Roles & (unsigned)clang::index::SymbolRole::RelationBaseOf) {
                    OnRelation(Ctx, SubjUSR.c_str(), CXREF_REL_BASE_OF, USR.c_str());
                }
                if (Rel.Roles & (unsigned)clang::index::SymbolRole::RelationOverrideOf) {
                    OnRelation(Ctx, USR.c_str(), CXREF_REL_OVERRIDDEN_BY, SubjUSR.c_str());
                }
            }
        }

        return true;  // continue indexing
    }

    int getRefCount() const { return RefCount; }

private:
    clang_xref_symbol_cb OnSymbol;
    clang_xref_ref_cb OnRef;
    clang_xref_relation_cb OnRelation;
    void* Ctx;
    std::string BaseDir;
    clang::ASTContext* ASTCtx = nullptr;
    int RefCount = 0;

    std::string getShortName(const clang::Decl* D) {
        if (auto* ND = llvm::dyn_cast<clang::NamedDecl>(D))
            return ND->getNameAsString();
        return {};
    }

    std::string getQualifiedName(const clang::Decl* D) {
        if (auto* ND = llvm::dyn_cast<clang::NamedDecl>(D)) {
            std::string QN;
            llvm::raw_string_ostream OS(QN);
            ND->printQualifiedName(OS);
            return QN;
        }
        return {};
    }
};

/* -- FrontendAction that drives the indexer ------------------------------- */

class XrefIndexAction : public clang::ASTFrontendAction {
public:
    XrefIndexAction(clang_xref_symbol_cb onSym,
                    clang_xref_ref_cb onRef,
                    clang_xref_relation_cb onRel,
                    clang_xref_macro_cb onMacro,
                    clang_xref_include_cb onInclude,
                    void* ctx,
                    const std::string& baseDir)
        : OnSymbol(onSym), OnRef(onRef), OnRelation(onRel),
          OnMacro(onMacro), OnInclude(onInclude),
          Ctx(ctx), BaseDir(baseDir) {}

    void setPchPath(const std::string& path) { PchPath = path; }

    std::unique_ptr<clang::ASTConsumer>
    CreateASTConsumer(clang::CompilerInstance& CI,
                      llvm::StringRef InFile) override {
        Consumer = std::make_shared<XrefConsumer>(
            OnSymbol, OnRef, OnRelation, Ctx, BaseDir);

        // Install PPCallbacks for macro and include tracking
        if (OnMacro || OnInclude) {
            CI.getPreprocessor().addPPCallbacks(
                std::make_unique<XrefPPCallbacks>(
                    OnMacro, OnInclude, Ctx,
                    CI.getSourceManager(), BaseDir));
        }

        clang::index::IndexingOptions Opts;
        Opts.IndexFunctionLocals = true;
        Opts.IndexImplicitInstantiation = true;
        Opts.IndexParametersInDeclarations = true;
        Opts.IndexTemplateParameters = true;

        return clang::index::createIndexingASTConsumer(
            Consumer, Opts, CI.getPreprocessorPtr());
    }

    int getRefCount() const {
        return Consumer ? Consumer->getRefCount() : 0;
    }

protected:
    // Inject PCH before the preprocessor is created.  Called by
    // FrontendAction::BeginSourceFile (FrontendAction.cpp:760) BEFORE
    // createPreprocessor().  Setting ImplicitPCHInclude here makes the
    // preprocessor deserialize the PCH AST instead of parsing headers.
    bool BeginInvocation(clang::CompilerInstance& CI) override {
        if (!PchPath.empty()) {
            CI.getPreprocessorOpts().ImplicitPCHInclude = PchPath;
            // The PCH may have been generated with suppressed errors
            // (e.g. non-portable includes, redefined macros).  Allow
            // loading it anyway — we're indexing, not compiling.
            CI.getPreprocessorOpts().AllowPCHWithCompilerErrors = true;
        }
        return true;
    }

private:
    clang_xref_symbol_cb OnSymbol;
    clang_xref_ref_cb OnRef;
    clang_xref_relation_cb OnRelation;
    clang_xref_macro_cb OnMacro;
    clang_xref_include_cb OnInclude;
    void* Ctx;
    std::string BaseDir;
    std::string PchPath;
    std::shared_ptr<XrefConsumer> Consumer;
};

class XrefActionFactory : public clang::tooling::FrontendActionFactory {
public:
    XrefActionFactory(clang_xref_symbol_cb onSym,
                      clang_xref_ref_cb onRef,
                      clang_xref_relation_cb onRel,
                      clang_xref_macro_cb onMacro,
                      clang_xref_include_cb onInclude,
                      void* ctx,
                      const std::string& baseDir)
        : OnSymbol(onSym), OnRef(onRef), OnRelation(onRel),
          OnMacro(onMacro), OnInclude(onInclude),
          Ctx(ctx), BaseDir(baseDir) {}

    void setPchPath(const std::string& path) { PchPath = path; }

    std::unique_ptr<clang::FrontendAction> create() override {
        auto* Action = new XrefIndexAction(
            OnSymbol, OnRef, OnRelation, OnMacro, OnInclude, Ctx, BaseDir);
        if (!PchPath.empty())
            Action->setPchPath(PchPath);
        LastAction = Action;
        return std::unique_ptr<clang::FrontendAction>(Action);
    }

    int getRefCount() const {
        return LastAction ? LastAction->getRefCount() : 0;
    }

private:
    clang_xref_symbol_cb OnSymbol;
    clang_xref_ref_cb OnRef;
    clang_xref_relation_cb OnRelation;
    clang_xref_macro_cb OnMacro;
    clang_xref_include_cb OnInclude;
    void* Ctx;
    std::string BaseDir;
    std::string PchPath;
    XrefIndexAction* LastAction = nullptr;
};

/* -- MSVC flag adjuster ---------------------------------------------------
 *
 * compile_commands.json contains raw MSVC flags many of which Clang
 * can't handle (/Yu, /Fp, /d1*, etc.).  We use --driver-mode=cl so
 * Clang's MSVC-compatible driver handles system includes, predefines,
 * and name resolution correctly.  We whitelist only flags relevant
 * for AST parsing.
 *
 * NOTE: We tried replacing this with native clang flag translation
 * but native mode fails
 * on this codebase — errors from system headers, missing platform
 * headers (corerror.h), and MSVC-specific using declarations.
 * --driver-mode=cl is required.
 */

static bool isKeptFlag(llvm::StringRef Flag) {
    auto startsI = [&](llvm::StringRef Prefix) {
        return Flag.size() >= Prefix.size() &&
               Flag.substr(0, Prefix.size()).equals_insensitive(Prefix);
    };

    // Reject undocumented MSVC internals early
    if (startsI("/d1") || startsI("/d2") || startsI("-d1") || startsI("-d2"))
        return false;

    // Include paths
    if (startsI("/I") || startsI("-I"))
        return true;

    // Defines — validate macro name for concatenated form.
    // Rejects malformed defines like -D@@@=1 from MSBuild.
    if (startsI("/D") || startsI("-D")) {
        if (Flag.size() <= 2)
            return true;  // Bare /D — value validated in adjuster
        char first = Flag[2];
        if (first == '"' && Flag.size() > 3)
            first = Flag[3];  // Quoted: /D"NAME=VALUE"
        return first == '_' || (first >= 'A' && first <= 'Z') ||
               (first >= 'a' && first <= 'z');
    }

    return startsI("/std:") ||
           startsI("/TP") || startsI("/TC") ||
           startsI("/Zc:wchar_t") || startsI("/Zc:forScope") ||
           startsI("/Zc:inline") || startsI("/Zc:strictStrings") ||
           startsI("/wd") ||
           startsI("-std=") ||
           Flag == "--driver-mode=cl";
}

static clang::tooling::ArgumentsAdjuster getMsvcAdjuster() {
    return [](const clang::tooling::CommandLineArguments& Args,
              llvm::StringRef Filename) {
        clang::tooling::CommandLineArguments Adjusted;
        Adjusted.push_back(Args.empty() ? "clang" : Args[0]);
        Adjusted.push_back("--driver-mode=cl");
        for (size_t i = 1; i < Args.size(); ++i) {
            llvm::StringRef A(Args[i]);
            // Keep the source file that ClangTool appends (matches Filename)
            if (A == Filename) {
                Adjusted.push_back(Args[i]);
                continue;
            }
            // Skip other source files, /c, output flags
            if (A.ends_with(".cpp") || A.ends_with(".cc") ||
                A.ends_with(".cxx") || A.ends_with(".c") ||
                A.ends_with(".obj") || A.ends_with(".exe") ||
                A == "/c" || A == "-c")
                continue;
            if (isKeptFlag(A)) {
                Adjusted.push_back(Args[i]);
                // MSVC allows space-separated /D VALUE and /I VALUE.
                // compile_commands.json tokenizes these as two args.
                // A bare /D or /I (exactly 2 chars) means the next arg is
                // the value — keep it too.
                if (i + 1 < Args.size() && A.size() == 2 &&
                    (A.equals_insensitive("/D") || A.equals_insensitive("/I") ||
                     A == "-D" || A == "-I"))
                {
                    llvm::StringRef NextArg(Args[i + 1]);
                    // For /D, validate macro name (reject -D @@@=1 etc.)
                    bool isDefine = A.equals_insensitive("/D") || A == "-D";
                    if (isDefine && !NextArg.empty()) {
                        char first = NextArg[0];
                        if (first != '_' && !(first >= 'A' && first <= 'Z') &&
                            !(first >= 'a' && first <= 'z')) {
                            ++i; // skip invalid define
                            continue;
                        }
                    }
                    Adjusted.push_back(Args[++i]);
                }
            }
        }
        return Adjusted;
    };
}

/* -- C API implementation ------------------------------------------------- */

// LLVM has process-wide singletons (ManagedStatic) that race on first access.
// Force initialization once before any concurrent use.
static std::once_flag g_llvmInitFlag;
static void ensureLLVMInitialized() {
    std::call_once(g_llvmInitFlag, []() {
        llvm::cl::ResetAllOptionOccurrences();
    });
}

// Cache loaded CompilationDatabases by directory.  Loading a 27MB JSON file
// 16 times (once per thread) is wasteful and may race on LLVM internals.
// The CDB is immutable after construction — safe to share.
static std::mutex g_cdbCacheMutex;
static std::unordered_map<std::string, std::shared_ptr<SharedCDB>> g_cdbCache;

CLANGXREF_API clang_xref_index_t
clang_xref_create(const char* compile_commands_dir,
                  clang_xref_log_cb on_log,
                  void* log_ctx) {
    ensureLLVMInitialized();

    std::string Dir(compile_commands_dir);

    // Lookup or create shared CDB
    std::shared_ptr<SharedCDB> shared;
    {
        std::lock_guard<std::mutex> Lock(g_cdbCacheMutex);
        auto it = g_cdbCache.find(Dir);
        if (it != g_cdbCache.end()) {
            shared = it->second;
        } else {
            std::string ErrMsg;
            auto CDB = clang::tooling::CompilationDatabase::loadFromDirectory(
                Dir, ErrMsg);
            if (!CDB) {
                emit_log_raw(on_log, log_ctx,
                    "Failed to load compile_commands.json: %s",
                    ErrMsg.c_str());
                return nullptr;
            }
            shared = std::make_shared<SharedCDB>();
            shared->CDB = std::move(CDB);
            shared->BaseDir = Dir;
            shared->Pch.Scan(*shared->CDB);
            emit_log_raw(on_log, log_ctx,
                "PCH: %d groups covering %d/%d TUs",
                (int)shared->Pch.GroupCount(),
                (int)shared->Pch.CoveredTUCount(),
                (int)shared->Pch.TotalTUCount());
            g_cdbCache[Dir] = shared;
        }
    }

    auto* S = new XrefSession();
    S->Shared = shared;
    S->OnLog = on_log;
    S->LogCtx = log_ctx;
    return static_cast<clang_xref_index_t>(S);
}

CLANGXREF_API void
clang_xref_destroy(clang_xref_index_t idx) {
    delete static_cast<XrefSession*>(idx);
}

// Inner implementation — called inside SEH __try block.
static int clang_xref_index_tu_impl(
    XrefSession* S,
    const char* source_file,
    clang_xref_symbol_cb on_symbol,
    clang_xref_ref_cb on_ref,
    clang_xref_relation_cb on_relation,
    clang_xref_macro_cb on_macro,
    clang_xref_include_cb on_include,
    void* ctx,
    bool usePch = true) {

    std::vector<std::string> Sources = { source_file };

    // Per-thread VFS with independent working directory.
    // The default getRealFileSystem() shares the process working directory
    // which is thread-hostile — concurrent setCurrentWorkingDirectory() calls
    // in ClangTool::run corrupt each other.  createPhysicalFileSystem() gives
    // each thread its own working directory, matching AllTUsToolExecutor's
    // approach (see clang/lib/Tooling/AllTUsExecution.cpp).
    llvm::IntrusiveRefCntPtr<llvm::vfs::FileSystem> FS =
        llvm::vfs::createPhysicalFileSystem();

    clang::tooling::ClangTool Tool(
        *S->Shared->CDB, Sources,
        std::make_shared<clang::PCHContainerOperations>(), std::move(FS));

    // Strip MSVC flags that Clang can't handle
    Tool.appendArgumentsAdjuster(getMsvcAdjuster());

    // Suppress diagnostics to stderr (we don't need compiler warnings)
    Tool.setDiagnosticConsumer(new clang::IgnoringDiagConsumer());

    XrefActionFactory Factory(
        on_symbol, on_ref, on_relation, on_macro, on_include,
        ctx, S->Shared->BaseDir);

    // Auto-inject PCH if available and requested
    if (usePch) {
        auto PchPath = S->Shared->Pch.GetPchPath(source_file);
        if (!PchPath.empty())
            Factory.setPchPath(PchPath);
    }

    Tool.run(&Factory);
    return Factory.getRefCount();
}

CLANGXREF_API int
clang_xref_index_tu(
    clang_xref_index_t idx,
    const char* source_file,
    clang_xref_symbol_cb on_symbol,
    clang_xref_ref_cb on_ref,
    clang_xref_relation_cb on_relation,
    clang_xref_macro_cb on_macro,
    clang_xref_include_cb on_include,
    void* ctx) {

    if (!idx || !source_file)
        return -1;

    auto* S = static_cast<XrefSession*>(idx);

    // SEH guard with immediate retry: if the first attempt crashes (likely
    // due to LLVM thread-safety issues under parallel load), retry once
    // immediately on the same thread with PCH.  If retry also crashes,
    // return CXREF_ERROR_GENERAL — the C# caller collects these for a
    // deferred sequential retry after all parallel work completes.
    //
    // Stack overflow is a special case: the guard page is consumed, so
    // retrying on the same thread would crash the process.  Restore the
    // guard page via _resetstkoflw() and return CXREF_ERROR_STACK_OVERFLOW
    // so the C# side can retry on a thread with a larger stack.
    // SEH guard with stack overflow detection.
    //
    // The __except filter runs BEFORE stack unwinding, when we still have
    // the exception record but minimal stack.  We capture the code in the
    // filter and branch after the handler.  For stack overflow, we call
    // _resetstkoflw() immediately in the handler and return without retry.
    //
    // For access violations (LLVM race conditions), we retry once inline
    // and then defer to the C# sequential retry.
    DWORD firstExCode = 0;
    __try {
        return clang_xref_index_tu_impl(
            S, source_file, on_symbol, on_ref, on_relation,
            on_macro, on_include, ctx, /*usePch=*/true);
    }
    __except (firstExCode = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        if (firstExCode == EXCEPTION_STACK_OVERFLOW) {
            // Must restore guard page before ANY call that uses stack
            _resetstkoflw();
        }
    }

    if (firstExCode == EXCEPTION_STACK_OVERFLOW) {
        // Log after _resetstkoflw — stack is usable again
        emit_log(S, "Stack overflow indexing %s — needs larger stack",
                 source_file);
        return CXREF_ERROR_STACK_OVERFLOW;
    }

    // Not a stack overflow — log and retry (LLVM race condition)
    emit_log(S, "SEH EXCEPTION code=%lu indexing %s — retrying",
             firstExCode, source_file);

    // Immediate retry (still parallel, still with PCH)
    __try {
        return clang_xref_index_tu_impl(
            S, source_file, on_symbol, on_ref, on_relation,
            on_macro, on_include, ctx, /*usePch=*/true);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        emit_log(S, "SEH EXCEPTION code=%lu on retry — deferring %s",
                 GetExceptionCode(), source_file);
        return CXREF_ERROR_GENERAL;
    }
}

CLANGXREF_API const char*
clang_xref_version(void) {
    return "ClangXref 1.2 (LLVM 21.1.1)";
}

/* -- PCH generation -------------------------------------------------------
 *
 * Uses ClangTool with FixedCompilationDatabase to generate PCH files.
 * The representative TU's adjusted flags provide include paths and defines.
 * ClangTool handles Driver translation internally (no Driver headers needed).
 *
 * The generated PCH is consumed by XrefIndexAction::BeginInvocation() which
 * sets PreprocessorOptions::ImplicitPCHInclude — entirely at the API level,
 * no CLI flags involved.
 */

/// GeneratePCHAction subclass that sets the output path via BeginInvocation.
/// ClangTool doesn't expose FrontendOptions directly, so we override
/// BeginInvocation to set OutputFile before CreateASTConsumer runs.
class PchGenAction : public clang::GeneratePCHAction {
public:
    PchGenAction(const std::string& outputPath) : OutputPath(outputPath) {}

protected:
    bool BeginInvocation(clang::CompilerInstance& CI) override {
        CI.getFrontendOpts().OutputFile = OutputPath;
        // Allow PCH even with (suppressed) compiler errors — otherwise
        // shouldEraseOutputFiles() deletes the output on any diagnostic.
        CI.getPreprocessorOpts().AllowPCHWithCompilerErrors = true;
        return true;
    }

private:
    std::string OutputPath;
};

class PchGenFactory : public clang::tooling::FrontendActionFactory {
public:
    PchGenFactory(const std::string& outputPath) : OutputPath(outputPath) {}

    std::unique_ptr<clang::FrontendAction> create() override {
        return std::make_unique<PchGenAction>(OutputPath);
    }

private:
    std::string OutputPath;
};

/// Resolve a precomp header name (e.g. "precomp.h") to its full path
/// by searching the TU's compile directory and include paths.
static std::string resolveHeaderPath(
    const clang::tooling::CompilationDatabase& CDB,
    const PchGroup& Group)
{
    auto Cmds = CDB.getCompileCommands(Group.RepresentativeTU);
    if (Cmds.empty())
        return {};

    // Check the TU's compile directory first
    llvm::SmallString<256> Candidate(Cmds[0].Directory);
    llvm::sys::path::append(Candidate, Group.HeaderName);
    if (llvm::sys::fs::exists(Candidate))
        return std::string(Candidate);

    // Search /I include paths from the raw command
    for (const auto& Arg : Cmds[0].CommandLine) {
        llvm::StringRef A(Arg);
        llvm::StringRef IncPath;
        if (A.size() > 2) {
            auto Prefix = A.substr(0, 2);
            if (Prefix.equals_insensitive("/I") || Prefix == "-I")
                IncPath = A.substr(2);
        }
        if (IncPath.empty())
            continue;

        // Strip quotes
        if (IncPath.starts_with("\"") && IncPath.ends_with("\""))
            IncPath = IncPath.drop_front().drop_back();

        llvm::SmallString<256> Full(IncPath);
        llvm::sys::path::append(Full, Group.HeaderName);
        if (llvm::sys::fs::exists(Full))
            return std::string(Full);
    }

    return {};
}

/// Generate a PCH file for one groupusing ClangTool + GeneratePCHAction.
/// Returns true on success.  The generated file is written to pchOutputPath.
static bool generatePchForGroup(
    const SharedCDB& Shared,
    const PchGroup& Group,
    const std::string& pchOutputPath,
    clang_xref_log_cb on_log = nullptr,
    void* log_ctx = nullptr)
{
    // Resolve the precomp header to a full path
    std::string HeaderPath = resolveHeaderPath(*Shared.CDB, Group);
    if (HeaderPath.empty()) {
        emit_log_raw(on_log, log_ctx,
            "PCH gen: cannot find \"%s\" in include paths",
            Group.HeaderName.c_str());
        return false;
    }

    // Get compile command for the representative TU
    auto Cmds = Shared.CDB->getCompileCommands(Group.RepresentativeTU);
    if (Cmds.empty()) {
        emit_log_raw(on_log, log_ctx,
            "PCH gen: no compile command for %s",
            Group.RepresentativeTU.c_str());
        return false;
    }

    // Apply MSVC adjuster to get Clang-compatible flags
    auto Adjusted = getMsvcAdjuster()(Cmds[0].CommandLine, Group.RepresentativeTU);
    if (Adjusted.empty())
        return false;

    // Save the compiler path — FixedCompilationDatabase uses "clang-tool"
    // which can't locate the MSVC installation.  We replace it with the
    // real cl.exe path so the CL driver finds system headers and predefines
    // _MSC_VER (needed for Windows SDK headers like DirectXMath.h).
    std::string CompilerPath = Adjusted[0];

    // Extract flags (skip compiler name and source files)
    std::vector<std::string> Flags;
    for (size_t i = 1; i < Adjusted.size(); ++i) {
        llvm::StringRef A(Adjusted[i]);
        if (A == Group.RepresentativeTU)
            continue;
        if (A.ends_with(".cpp") || A.ends_with(".cc") ||
            A.ends_with(".cxx") || A.ends_with(".c"))
            continue;
        Flags.push_back(Adjusted[i]);
    }
    // Force C++ mode for the .h file
    Flags.push_back("/TP");

    std::string Directory = Cmds[0].Directory;

    // Create a FixedCompilationDatabase with the adjusted flags.
    clang::tooling::FixedCompilationDatabase FixedCDB(Directory, Flags);

    // Per-thread VFS (same pattern as clang_xref_index_tu_impl)
    auto FS = llvm::vfs::createPhysicalFileSystem();

    std::vector<std::string> Sources = { HeaderPath };
    clang::tooling::ClangTool Tool(
        FixedCDB, Sources,
        std::make_shared<clang::PCHContainerOperations>(), std::move(FS));
    Tool.setDiagnosticConsumer(new clang::IgnoringDiagConsumer());

    // Replace "clang-tool" (FixedCDB default) with the real compiler path
    // so the MSVC CL driver can locate the installation.
    Tool.appendArgumentsAdjuster(
        [&CompilerPath](const clang::tooling::CommandLineArguments& Args,
                        llvm::StringRef) {
            auto Copy = Args;
            if (!Copy.empty())
                Copy[0] = CompilerPath;
            return Copy;
        });

    // Run GeneratePCHAction — output path set via BeginInvocation override
    PchGenFactory Factory(pchOutputPath);
    int Result = Tool.run(&Factory);

    uint64_t Size = 0;
    llvm::sys::fs::file_size(pchOutputPath, Size);
    emit_log_raw(on_log, log_ctx,
        "PCH gen: Tool.run returned %d, file size = %llu bytes",
        Result, (unsigned long long)Size);

    if (Result == 0 && Size > 0) {
        uint64_t Size = 0;
        llvm::sys::fs::file_size(pchOutputPath, Size);
        emit_log_raw(on_log, log_ctx,
            "PCH gen: \"%s\" -> %s (%llu KB)",
            Group.HeaderName.c_str(), pchOutputPath.c_str(),
            (unsigned long long)(Size / 1024));
    } else {
        emit_log_raw(on_log, log_ctx,
            "PCH gen: FAILED for \"%s\" (result=%d, size=%llu)",
            Group.HeaderName.c_str(), Result, (unsigned long long)Size);
    }

    return Result == 0;
}

/* -- PCH prepare: generate all PCH files ---------------------------------- */

CLANGXREF_API int
clang_xref_prepare_pch(clang_xref_index_t idx, const char* output_dir) {
    if (!idx || !output_dir)
        return -1;

    auto* S = static_cast<XrefSession*>(idx);
    auto& Pch = S->Shared->Pch;
    auto& Groups = Pch.Groups();

    if (Groups.empty())
        return 0;

    // Ensure output directory exists
    if (auto EC = llvm::sys::fs::create_directories(output_dir)) {
        emit_log(S, "PCH prepare: cannot create %s: %s",
                 output_dir, EC.message().c_str());
        return -1;
    }

    std::string OutDir(output_dir);
    std::atomic<int> Successes{0};
    std::atomic<int> Done{0};
    size_t Total = Groups.size();

    // Parallel generation — each group is independent (different header,
    // flags, output path).  Groups[i].GeneratedPchPath is written by
    // exactly one thread (no contention).  SharedCDB is immutable.
    unsigned HwThreads = std::thread::hardware_concurrency();
    unsigned Parallelism = HwThreads > 0 ? HwThreads : 1;
    if (Parallelism > (unsigned)Total)
        Parallelism = (unsigned)Total;

    emit_log(S, "PCH prepare: %zu groups, %u threads",
             Total, Parallelism);

    std::vector<std::thread> Workers;
    std::atomic<size_t> NextGroup{0};

    for (unsigned t = 0; t < Parallelism; ++t) {
        Workers.emplace_back([&]() {
            while (true) {
                size_t i = NextGroup.fetch_add(1);
                if (i >= Total)
                    break;

                llvm::SmallString<256> PchPath(OutDir);
                llvm::sys::path::append(PchPath,
                    "pch_" + std::to_string(i) + ".pch");

                if (generatePchForGroup(*S->Shared, Groups[i],
                                        std::string(PchPath),
                                        S->OnLog, S->LogCtx)) {
                    Groups[i].GeneratedPchPath = std::string(PchPath);
                    Successes.fetch_add(1);
                }

                int done = Done.fetch_add(1) + 1;
                emit_log(S, "PCH prepared: %d/%zu",
                         done, Total);
            }
        });
    }

    for (auto& W : Workers)
        W.join();

    int failures = (int)Total - Successes.load();
    emit_log(S, "PCH done: %d succeeded, %d failed",
             Successes.load(), failures);

    return Successes.load();
}

/* -- PCH test export ------------------------------------------------------ */

/// Test PCH generation and consumption for a single TU.
/// Indexes the TU twice: once without PCH (baseline), once with PCH.
/// Prints timing and ref counts to stderr for comparison.
/// Returns 0 on success, -1 on error.
static int clang_xref_test_pch_impl(
    XrefSession* S,
    const char* source_file)
{
    const auto& BaseDir = S->Shared->BaseDir;

    // Look up PCH group for this source file
    const auto* Group = S->Shared->Pch.GetGroup(source_file);
    if (!Group) {
        emit_log(S, "[PCH Test] No PCH group for %s", source_file);
        return -1;
    }

    emit_log(S, "[PCH Test] Source: %s", source_file);
    emit_log(S, "[PCH Test] Group: /Yu=\"%s\" (%zu TUs)",
             Group->HeaderName.c_str(), Group->Sources.size());

    // --- Baseline: index WITHOUT PCH ---
    emit_log(S, "[PCH Test] Indexing WITHOUT PCH...");
    int baselineRefs = 0;
    long long baselineMs = 0;
    {
        auto Start = std::chrono::steady_clock::now();

        std::vector<std::string> Sources = { source_file };
        auto FS = llvm::vfs::createPhysicalFileSystem();
        clang::tooling::ClangTool Tool(
            *S->Shared->CDB, Sources,
            std::make_shared<clang::PCHContainerOperations>(), std::move(FS));
        Tool.appendArgumentsAdjuster(getMsvcAdjuster());
        Tool.setDiagnosticConsumer(new clang::IgnoringDiagConsumer());

        // Count refs via a minimal callback
        int refCount = 0;
        auto refCounter = [](void* ctx, const char*, const char*, int, int,
                             int, const char*, const char*) {
            ++(*static_cast<int*>(ctx));
        };

        XrefActionFactory Factory(
            nullptr, refCounter, nullptr, nullptr, nullptr,
            &refCount, BaseDir);
        Tool.run(&Factory);
        baselineRefs = refCount;

        auto End = std::chrono::steady_clock::now();
        baselineMs = std::chrono::duration_cast<std::chrono::milliseconds>(End - Start).count();
        emit_log(S, "[PCH Test] WITHOUT PCH: %d refs in %lld ms",
                 baselineRefs, baselineMs);
    }

    // --- Generate PCH ---
    std::string PchPath = std::string(source_file) + ".test.pch";
    emit_log(S, "[PCH Test] Generating PCH...");

    auto PchStart = std::chrono::steady_clock::now();
    bool PchOk = generatePchForGroup(*S->Shared, *Group, PchPath,
                                     S->OnLog, S->LogCtx);
    auto PchEnd = std::chrono::steady_clock::now();
    auto PchMs = std::chrono::duration_cast<std::chrono::milliseconds>(PchEnd - PchStart).count();

    if (!PchOk) {
        emit_log(S, "[PCH Test] PCH generation FAILED after %lld ms", PchMs);
        return -1;
    }
    emit_log(S, "[PCH Test] PCH generated in %lld ms", PchMs);

    // --- Test: index WITH PCH ---
    emit_log(S, "[PCH Test] Indexing WITH PCH...");
    int pchRefs = 0;
    long long pchMs = 0;
    {
        auto Start = std::chrono::steady_clock::now();

        std::vector<std::string> Sources = { source_file };
        auto FS = llvm::vfs::createPhysicalFileSystem();
        clang::tooling::ClangTool Tool(
            *S->Shared->CDB, Sources,
            std::make_shared<clang::PCHContainerOperations>(), std::move(FS));
        Tool.appendArgumentsAdjuster(getMsvcAdjuster());
        Tool.setDiagnosticConsumer(new clang::IgnoringDiagConsumer());

        int refCount = 0;
        auto refCounter = [](void* ctx, const char*, const char*, int, int,
                             int, const char*, const char*) {
            ++(*static_cast<int*>(ctx));
        };

        XrefActionFactory Factory(
            nullptr, refCounter, nullptr, nullptr, nullptr,
            &refCount, BaseDir);
        Factory.setPchPath(PchPath);
        Tool.run(&Factory);
        pchRefs = refCount;

        auto End = std::chrono::steady_clock::now();
        pchMs = std::chrono::duration_cast<std::chrono::milliseconds>(End - Start).count();
        emit_log(S, "[PCH Test] WITH PCH: %d refs in %lld ms",
                 pchRefs, pchMs);
    }

    // --- Report ---
    double speedup = baselineMs > 0 ? (double)baselineMs / pchMs : 0;
    emit_log(S, "[PCH Test] === RESULTS ===");
    emit_log(S, "[PCH Test] Baseline: %d refs, %lld ms", baselineRefs, baselineMs);
    emit_log(S, "[PCH Test] With PCH: %d refs, %lld ms", pchRefs, pchMs);

    char speedBuf[32];
    snprintf(speedBuf, sizeof(speedBuf), "%.1fx", speedup);
    emit_log(S, "[PCH Test] Speedup:  %s", speedBuf);
    emit_log(S, "[PCH Test] Match:    %s",
             baselineRefs == pchRefs ? "YES" : "NO — MISMATCH!");

    // Cleanup test PCH file
    llvm::sys::fs::remove(PchPath);

    return 0;
}

CLANGXREF_API int
clang_xref_test_pch(
    clang_xref_index_t idx,
    const char* source_file)
{
    if (!idx || !source_file)
        return -1;

    auto* S = static_cast<XrefSession*>(idx);

    __try {
        return clang_xref_test_pch_impl(S, source_file);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        emit_log(S, "[PCH Test] SEH EXCEPTION code=%lu testing %s",
                 GetExceptionCode(), source_file);
        return -1;
    }
}
