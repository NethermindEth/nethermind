# Tests & Benchmarks

This is the single rule for all test and benchmark projects. It applies to any `*Test*` or `*Benchmark*` project under `src/Nethermind/`.

## TestBlockchain (`Nethermind.Core.Test/Blockchain/TestBlockchain.cs`)

Legacy wrapper around DI. Prefer direct `ContainerBuilder` + production modules for new tests. If you do use `TestBlockchain`, always dispose with `using`:

```csharp
// Basic usage — always use `using` for disposal
using TestBlockchain chain = await TestBlockchain.ForMainnet().Build();

// With customization
using TestBlockchain chain = await TestBlockchain.ForMainnet()
    .Build(builder => builder
        .AddSingleton<ISpecProvider>(mySpecProvider)
        .AddDecorator<ISpecProvider>((ctx, sp) => WrapSpecProvider(sp)));
```

**Provides**: `BlockTree`, `StateReader`, `TxPool`, `BlockProcessor`, `MainProcessingContext`, and 30+ other components — all wired via `PseudoNethermindModule`.

**Don't mock what TestBlockchain provides.** If you need `IBlockTree`, `IWorldState`, `ITransactionProcessor`, use DI instead of `Substitute.For<>()`.

## E2ESyncTests (`Synchronization.Test/E2ESyncTests.cs`)

Multi-instance setup for sync testing. Reference for setting up full component stacks through Autofac with dynamic container creation.

## Benchmark setup

For benchmarks, use production DI modules with `DiagnosticMode.MemDb` overrides — see the canonical container setup in [di-patterns.md](di-patterns.md) "Test setup pattern". `Nethermind.Evm.Benchmark` uses the `TestNethermindModule` convenience wrapper (wires `PseudoNethermindModule` + `TestEnvironmentModule` in one module). Don't manually construct `WorldState`, `TrieStore`, `BlockProcessor` etc.

## DI anti-pattern — never manually new up infrastructure

```csharp
// WRONG — manual construction makes the setup fragile and hard to refactor
WorldState worldState = new WorldState(new TrieStore(...), new MemDb(), LimboLogs.Instance);
ITransactionProcessor txProcessor = new TransactionProcessor(specProvider, worldState, vm, ...);
IBlockProcessor blockProcessor = new BlockProcessor(..., txProcessor, worldState, ...);
```

**Correct** — direct DI with targeted overrides: `PseudoNethermindModule` for unit tests, full `NethermindModule` for benchmarks; the canonical snippets are in [di-patterns.md](di-patterns.md) "Test setup pattern".

The rule: **if production modules already wire a component, use them — don't construct it yourself**.

## Test guidelines

- Add tests to existing test files rather than creating new ones
- **Do not duplicate test methods that differ only in parameters** — use `[TestCase(...)]` or `[TestCaseSource(...)]` to parameterize a single method
- Before writing a new test, check if an existing test can be extended with another `[TestCase]` or use `[TestCaseSource]`
- **When only parts of tests are similar** — extract the shared parts into helper methods or types instead of copy-pasting:
  - Shared arrange/build steps → a private helper method, an existing builder (`Build.A.Block...`, `Build.A.Transaction...`, `TestItem.*`), or a new builder if the pattern is reused across files
  - Shared assertions → a helper method like `AssertExpectedState(...)` so each test asserts in one line and the failure message stays meaningful
  - Shared scenarios spanning multiple test classes → a base fixture, a shared `static` helper class, or a fixture-level `[SetUp]`
  - Keep each test body focused on what makes the case unique; the helper should not hide behavior that matters for understanding the test
- Use `[TestCaseSource]` (not `[TestCase]`) when cases need non-constant data, named scenarios, or grow beyond a handful — keep the source method or `IEnumerable<TestCaseData>` next to the test it feeds

## DotNetty `IByteBuffer` in tests

- Prefer `using DisposableByteBuffer` via `.AsDisposable()` for releasing `IByteBuffer` in tests
- For leak-detection tests, use `PooledBufferLeakDetector` from `Nethermind.Network.Test`

## `Assert.Multiple` — wrap independent assertions on the same fixture

When a test has multiple `Assert.That` calls that all examine the **same** result/state and are logically independent of each other, wrap them in `using (Assert.EnterMultipleScope()) { ... }` (the NUnit 4 form; prefer this over the older `Assert.Multiple(() => { ... })` lambda). All assertions are evaluated even if earlier ones fail, so one run surfaces every mismatch — without it, you fix the first failure only to discover the next on the following CI cycle.

**Before reaching for `Assert.Multiple`, dedupe first.** Multiple tests doing the same field-by-field comparison are a smell — extract a helper (`AssertX(expected, actual)`) and wrap inside the helper once. Every caller then benefits from the multi-scope automatically, and the per-field assertion messages stay intact for diagnostics.

```csharp
// Field-by-field comparison helper — every caller benefits
private static void AssertReceipt(TxReceipt expected, TxReceipt actual)
{
    using (Assert.EnterMultipleScope())
    {
        Assert.That(actual.TxType, Is.EqualTo(expected.TxType), "tx type");
        Assert.That(actual.Bloom, Is.EqualTo(expected.Bloom), "bloom");
        Assert.That(actual.GasUsed, Is.EqualTo(expected.GasUsed), "gas used");
        // ...
    }
}
```

A custom `IEqualityComparer<T>` (or `Is.EqualTo(expected).Using(comparer)`) is the right tool when you only care **whether** two values are equal, not **which field** differs. Prefer the assertion-helper form when the failure diagnostic should name the field; prefer a comparer when "equal or not" is enough and you want a one-line callsite.

**Wrap when**:
- N independent property/field assertions on the same object with no mutation between them
- Field-by-field comparison helpers (`Compare*`, `Assert*`, `Validate*`) — wrap inside the helper so every caller benefits
- Inner loop body where each iteration's assertions all check independent properties of one result — wrap **per iteration**, not around the whole loop

**Do NOT wrap when**:
- Assertions are interleaved with state-mutating calls (`provider.Restore(...)`, `cache.Set(...)`, `list.TrySet(...)`) — a failure should stop, not run the next assert on broken state
- `Assert.That(x, Is.Not.Null)` followed by `Assert.That(x.Foo, ...)` — the second NREs if the first fails; you lose information rather than gain it
- Each iteration of a loop depends on the previous one's state holding the invariant

When an entire test method qualifies, prefer wrapping the **assertion block** at the end (after setup), not the whole body — that keeps arrange/act outside the scope where exceptions are diagnostic, not "additional failures".
