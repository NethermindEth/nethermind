; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NETH001 | Usage | Warning | Local variable assigned from new expression is never read. Remove or use discard. Suppressed by [ConstructorWithSideEffect].
NETH002 | Style | Warning | Multi-line lambda body is indented more than 24 columns past its containing statement. Extract to a local function or variable.
