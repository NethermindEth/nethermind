# C# Coding Style

- Follow the `.editorconfig` rules
- Prefer the latest C# syntax and conventions
- Prefer file-scoped namespaces (for existing files, follow their style)
- Prefer pattern matching and switch expressions over traditional control flow
- Use `nameof` operator instead of string literals for member references
- Use `is null` and `is not null` instead of `== null` and `!= null`
- Use `?.` null-conditional operator where applicable
- Use `ArgumentNullException.ThrowIfNull` for null checks
- Use `ObjectDisposedException.ThrowIf` for disposal checks
- Use documentation comments for all public APIs
- Avoid `var` — spell out types (exception: very long nested generic types)
- Trust null annotations, don't add redundant null checks
- Code comments explain _why_, not _what_
- Keep changes minimal and focused — don't rename variables, reformat surrounding code, or refactor unrelated logic
- Follow DRY — extract repeated blocks (5+ lines) into shared methods, but don't over-extract trivial one-liners
- In generic types, move methods that don't depend on the type parameter to a non-generic base class or static helper
- Do not use `#region` / `#endregion`
- Do not alter `src/bench_precompiles/` or `src/tests/` directories
