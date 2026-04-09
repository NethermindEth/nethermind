; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NETH001 | Usage | Warning | Local variable assigned from new expression is never read. Remove or use discard. Suppressed by [ConstructorWithSideEffect].
