; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RLP0001 | RLP | Error | Newtype representation requires exactly one primary constructor parameter.
RLP0002 | RLP | Error | Record member is not a primary constructor parameter, so it is not serialized.
RLP0003 | RLP | Error | Record shape is not supported (generic, nested, or no primary constructor).
RLP0004 | RLP | Error | Parameter type is not supported.
RLP0005 | RLP | Error | Fixed length requires a Newtype record over a byte array.
RLP0006 | RLP | Error | Newtype record contains itself without a sequence around it.
