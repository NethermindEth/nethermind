; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NETH001 | Usage | Warning | Local variable assigned from new expression is never read. Remove or use discard. Suppressed by [ConstructorWithSideEffect].
NETH002 | Style | Warning | Multi-line lambda body is indented more than 4 columns past the arrow line. Reindent to use normal block indentation.
NETH003 | Naming | Warning | File name does not match the single contained top-level type. Attribute suffix may be dropped; partial types may use TypeName.Descriptor.cs.
NETH004 | Performance | Warning | ConcurrentDictionary&lt;TKey,TValue&gt;.Keys / .Values allocate a snapshot list. Enumerate the dictionary directly with foreach, or use AcquireLock for a deliberate snapshot.
NETH005 | Performance | Warning | Span<T>.ToArray() / ReadOnlySpan<T>.ToArray() passed to a method that has a Span<T>/ReadOnlySpan<T> overload at the same position. Pass the span directly.
NETH006 | Reliability | Warning | TaskCompletionSource constructed without TaskCreationOptions.RunContinuationsAsynchronously can run continuations synchronously and deadlock.
NETH007 | Performance | Warning | IWriteOnlyKeyValueStore.Set called with a value copied from a byte span (Span&lt;byte&gt;/ReadOnlySpan&lt;byte&gt;.ToArray(), or x.BytesToArray() on a type with a Bytes span). Call PutSpan with the span directly instead of allocating an array.
NETH008 | Design | Warning | A value is force-cast, as-cast, or type-checked against an interface marked [StableApi]. Plugins may decorate or override the interface, so runtime type-branching is fragile. Reflection/OfType discovery is not flagged; test assemblies are exempt.
