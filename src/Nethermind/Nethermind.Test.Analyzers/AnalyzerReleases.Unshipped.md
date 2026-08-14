; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NMT001 | Usage | Info | Replace Substitute.For<T>() with the configured substitute factory call (for example, ReleaseSpecSubstitute.Create() or SpecProviderSubstitute.Create()).
