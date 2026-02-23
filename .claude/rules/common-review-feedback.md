# Common Review Feedback

Patterns that reviewers repeatedly flag. Avoid these:

- **Manual component wiring** instead of DI modules. Don't `new Foo(new Bar(...))` when Autofac modules already register these components.
- **Duplicating existing utilities** without searching first. Always grep for existing implementations before creating helpers.
- **LINQ in hot paths** (`.Select()`, `.Where()`, `.Any()`). Use `for`/`foreach` loops. LINQ is acceptable only in non-hot-path code where declarative syntax significantly improves clarity.
- **Using `var`** for variable declarations. Spell out the type. Only exception: very long nested generic types.
- **Over-mocking in tests** when `TestBlockchain` provides the real components. If you need `IBlockTree`, `IWorldState`, etc., prefer `TestBlockchain` over manual mocking.
- **Not reusing base classes**. Check for abstract base classes before implementing interfaces from scratch (e.g., `BlockProducerBase` for `IBlockProducer`).
- **Missing regression tests** for bug fixes. Every bug fix needs a test that fails without the fix and passes with it.
- **Missing structured logging**. Use `ILogger` with message templates, not string interpolation.
- **Hash256 vs ValueHash256 confusion**. Use `ValueHash256` (stack-allocated) in hot paths. `Hash256` is for data structures like block headers.
- **Adding `Version=` to PackageReference**. This repo uses Central Package Management — versions go only in `Directory.Packages.props`.
