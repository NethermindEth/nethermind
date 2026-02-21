# Nethermind Code Review Conventions

Derived from AGENTS.md and project patterns. Use this checklist during PR review.

## C# Style Rules (flag violations)

- **No `var`** — explicit types required, except for very long nested generic types
- **`is null` / `is not null`** — never `== null` or `!= null`
- **`nameof()`** — never string literals for member references
- **`?.` null-conditional** — use where applicable instead of explicit null checks
- **`ArgumentNullException.ThrowIfNull`** — for null checks at method boundaries (not manual `if (x == null) throw`)
- **`ObjectDisposedException.ThrowIf`** — for disposal checks
- **No LINQ** — flag any `.Select()`, `.Where()`, `.Any()`, `.FirstOrDefault()`, etc. where a simple `for`/`foreach` would suffice. LINQ has overhead and should only appear for complex queries where declarative syntax adds real clarity
- **No `#region` / `#endregion`** pragmas
- **File-scoped namespaces** — `namespace Foo.Bar;` not `namespace Foo.Bar { ... }`
- **Pattern matching** — prefer switch expressions and pattern matching over traditional if/else chains
- **Documentation comments** — all public APIs need `<summary>` XML doc comments
- **Comments explain why, not what** — flag comments that just restate the code

## Architecture / Design Rules

- **Low allocation** — flag unnecessary allocations: new collections inside hot loops, closures capturing large objects, boxing value types, string concatenation in loops (use `StringBuilder` or interpolation)
- **Generic base class** — methods in generic types that don't depend on the type parameter should be in a non-generic base or static helper (prevents redundant JIT instantiations)
- **DRY** — flag duplicated blocks of 5+ lines that should be extracted. But don't flag one-liner duplications.
- **Minimal changes** — PRs should not rename variables, reformat unrelated code, or refactor beyond what's needed. Flag scope creep.
- **No over-engineering** — flag unnecessary abstractions, helpers for one-time operations, or design for hypothetical future requirements

## Test Rules

- **Regression tests required** — every bug fix must include a test that fails without the fix
- **Add to existing test files** — don't create new test files when an existing one covers the area
- **Parameterized tests** — multiple similar tests should use `[TestCase]` / `[Theory]` rather than copy-pasted test methods
- **Test naming** — should describe the scenario: `MethodName_Condition_ExpectedResult`

## Common Nethermind Pitfalls to Check

- `Hash256` vs `Keccak` — ensure the correct type is used; don't confuse them
- `UInt256` arithmetic — check for overflow and correct ordering (big-endian vs little-endian)
- `RlpStream` / `Rlp.Encode` — mutations must be in the right order; length prefix is computed before encoding children
- Thread safety in `BlockTree`, `TxPool`, `TrieStore` — any shared state changes need lock analysis
- Disposal — `IDisposable` implementations should call `ObjectDisposedException.ThrowIf` before operations
- Logging — avoid string interpolation in log calls; use structured logging with message templates: `_logger.Debug("Value {Value}", val)` not `_logger.Debug($"Value {val}")`

## PR Template Checklist

Verify the PR description includes:
- [ ] Issue reference (Fixes/Closes/Resolves #NNN) or explicitly removed
- [ ] ## Changes section with bullet points listing what changed
- [ ] Type-of-change checkboxes (at least one ticked)
- [ ] Testing section filled in (Requires testing: Yes/No; if Yes, Did you write tests: Yes/No)
- [ ] Documentation section filled in
