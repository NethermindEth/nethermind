# Source audit

The schema-9/extractor-1.8.0 admission binds the accepted Receipt schema-9/extractor-1.9.2
snapshot through 23 frozen artifacts. The complete upstream validator owns
transitive source, compiler, delegated-kernel and generated-artifact checks.

Compilation support includes the BAL validation-index partials, the prewarmer's internal pooled
policy, and `ExecutionFlags.std.cs`. The pinned `src/Nethermind/Directory.Build.props` is parsed
for its single standard-target `EvmWord = Vector256<byte>` alias before that alias is supplied to
all four compilations. Exact duplicate runtime TPA paths are normalized before checking the
434-member inventory; distinct or ambiguous identities remain rejected. Roslyn lowers a logical
negation into a branch polarity, so negated guards are located by their operand span. Fluent DI
anchors bind both method name and argument list, and lexical method identities prevent a local
`BuildReceipt` from masquerading as the production member.

The generated snapshot is frozen at these SHA-256 values:

| Artifact | SHA-256 |
| --- | --- |
| `Generated/SequentialBlockTransactionFold.ir.json` | `0580b07867d42ffcd210a2044cf15f6e30193ba4170486c97b20caad54cbdda5` |
| `Generated/SequentialBlockTransactionFold.lean` | `e1c7c38d5f90662a649fff77b8fa7bbc2543ea25e8eea1ac845a4d15ffbf5516` |
| `Generated/SequentialBlockTransactionFold.source-manifest.json` | `f531d9ca1bf15382ef74b2c055ad45ca8352163815a35b07e382a20f257c5a56` |

The audit is against the pinned source files and is intentionally limited to the exact-base
direct-inner executor slice under an explicit BAL-disabled route premise and to source
identity and local control flow. SHA-256 values below are the values recorded in
`SOURCE_PINS.json`; extraction recomputes them before accepting a production run.

| Source | Role | SHA-256 |
| --- | --- | --- |
| `BlockProcessor.BlockValidationTransactionsExecutor.cs` | executor loop/helper | `d520090e2adde45bced4b6af6f9add35ea77ed288060d09fdf05cae3b28bdf56` |
| `BlockProcessor.cs` | caller and commit order | `e2db419219184150c979cd6a081c83ba758c5024a15f996b510806bd1303deda` |
| `BlockProcessor.std.cs` | partial-source closure | `7ec6d0f975f36958f83b002ca53404620427a756cab70a037c0988ed75567ed4` |
| `IBlockProcessor.cs` | interface closure | `df79a2a24ca0ad79918cf10bce5b9d74cb4ad1b7542185b41076a0f3758a940e` |
| `ProcessingOptions.cs` | validation-flag closure | `145c5bec2a5e70e01dc448a887cd17bb1b81277605a487e437971882a5b0421a` |

The auxiliary pins add the transaction adapter order, exact-base tracer lifecycle, BAL manager
`Enabled` derivation, parallel decorator fallback, standard-mainnet DI registrations, and the
transaction-adapter interface. Their raw hashes are checked before Roslyn operation binding;
the serialized IR carries their source identities, typed operation anchors, declared-method owner,
reachable CFG block, and reachability bit. Roslyn documentation IDs normalize reduced extension
methods; receiver identities distinguish exact fields, properties, parameters and primary-constructor
captures. Argument references reject rebound nested calls and changed parameter ordinals. Local/lambda relocation is rejected, and the adapter
Start/Execute/End plus tracer delegate/index edges are checked with normal-path dominance and
postdominance.

The common main binding rejects anchors lexically owned by an anonymous or local function before
using span-based CFG lookup. An unused lambda cannot therefore borrow its enclosing assignment's
reachable block. This check covers method, anchor, conditional-signal, and CFG binding paths.
The gas-limit helper's local symbol must belong to `ProcessTransactions`, and its expression body
must directly throw the exact `InvalidBlockException` constructor with the block parameter and
`BlockErrorMessages.ExceededGasLimit` field. The paired false-result helper must directly throw
the exact `InvalidTransactionException` constructor and retain its argument expressions. These
are source-body admission checks, not proofs of CLR exception execution or catch propagation.

## Observed order

Within `ProcessTransactions`, setup is followed by the source-order loop. Each iteration calls
`ProcessTransaction`; a false result is required to be a direct `!result`-guard throw before the
processed event. For a successful result the gas-limit check follows the helper call and its throw
is required to be the direct guard body. The final metrics/receipt array return is outside the
transaction loop.

The loop itself is a direct statement in the admitted method. Its body is a closed ordered
three-statement grammar: `Transaction currentTx = block.Transactions[i]`, the exact direct
`ProcessTransaction` invocation, and the exact gas-limit guard. It cannot conditionally skip or
repeat the call/guard pair, introduce an early `continue`/`break`/`return`, or add an unmodeled
statement. The gas-limit guard must have no else arm, including an empty one. Both freshly
extracted and deserialized IR must retain the exact admitted executor CFG: ten reachable blocks,
eleven edges, the sole normal exit at block 8, and the sole back edge from block 7 to block 3.
The transaction call and first gas-limit condition remain in block 4. Source order and shared
CFG ancestry alone are not used as exactly-once evidence.

The transaction declaration's resolved type must be the metadata `Nethermind.Core.Transaction`
from `Nethermind.Core`. Its initializer must be an array element operation over the exact
`Block.Transactions` property, the admitted `block` parameter, and the loop's `i` local.
The helper's transaction argument must reference that same local through an identity conversion
to the metadata transaction parameter. The IR separately records and validates these identities
and conversion properties. Contextual conversions are inspected on `IArgumentOperation`, including
implicit conversions absent from the argument expression's operation. Main calls require identity
conversions; every admitted argument tree rejects user-defined conversion operators.

The `ProcessTransaction` method and the gas-limit and invalid-result exception helpers each
carry a synchronous-callable identity with their exact symbol, method kind, body kind, return-void
status, and async/iterator/partial/extern/abstract flags. Roslyn must report `IsAsync = false`;
all deferred or bodyless variants are rejected. The serialized inventory must contain exactly
these three identities and validates the same flags. This preserves the source-level synchronous
call/throw boundary without claiming CLR exception execution or propagation.

All three callable identities also record conditional status and their resolved attribute type,
assembly, and argument presence. Symbol-based admission rejects `System.Diagnostics.ConditionalAttribute`
even through an alias; the runtime attribute is sealed, so derived forms cannot compile. The exact
remaining inventory is empty for `ProcessTransaction`, `DebuggerHidden` then `DoesNotReturn` for
the gas helper, and `DoesNotReturn` then `StackTraceHidden` for the invalid-result helper. The pinned
compilation resolves the local gas helper's `DoesNotReturn` to `Microsoft.TestPlatform.Utilities`
and all other admitted attributes to `System.Private.CoreLib`; these exact identities and absence
of arguments are required. This closed inventory rejects added execution
modifiers such as `MethodImpl(Synchronized)` and interop attributes; the existing concrete-body,
extern, and partial checks remain independent guards. The admitted `DoesNotReturn` attribute is
an analyzer annotation, not evidence that a call executes or propagates an exception.

Attribute absence on a declaration alone is insufficient: C# can inherit conditional-call behavior
from an overridden method. The callable identity therefore records static/virtual/override flags,
the complete overridden-method chain and its attributes, declaring-type and base-type identities,
all implemented interfaces, and the interface methods implemented by the callable. Admission fixes
`ProcessTransaction` as virtual but non-override and the two throw helpers as static non-overrides.
All three must remain on the exact executor type from `Nethermind.Consensus`, whose sole base is
`System.Object` from `System.Private.CoreLib` and whose interface closure is exactly the existing
`IBlockProcessor.IBlockTransactionsExecutor`. These three callables implement no interface method.
Any override or inherited attribute is rejected, including conditional attributes on a more distant
base method. Serialized IR validates the same complete lineage.

Within `ProcessBlock`, the relevant source order is:

```text
CommitState(spec)
ProcessTransactions(block, options, ReceiptsTracer, token)
TransactionsExecuted?.Invoke()
CommitState(spec)
```

`ProcessBlock` has a later commit for the excluded withdrawals/requests path. The extractor binds
the nearest commit on each side of the transaction call, requires the expected one-pre/two-post
commit shape, and deliberately does not admit that later call. Its normal CFG route additionally
requires `StartNewBlockTrace` to dominate the pre-fold commit, the pre-fold commit to dominate the
fold, the unconditional `TransactionsExecuted` evaluation to postdominate the fold, and the
post-fold commit to postdominate that evaluation. The optional `Action.Invoke` block retains
its exact event receiver and method identity, but is not required to dominate the commit.
Separate typed evaluation and CFG evidence require the captured event's null branch to skip
the invocation, its non-null branch to invoke the same capture, and both branches to rejoin.
The no-subscriber path therefore remains admitted without weakening callback relocation or
normal-completion checks.

The gas-limit and invalid-result guard throws, together with the BAL-disabled sequential inner
return, are accepted only when the expected action is the sole direct statement in the guard's
true arm. A branch that can bypass the action is rejected even when the expected invocation or
return remains textually present.

The generated fold treats the supplied `OnlyOkTerminal` observation as the settled result of the helper.
It starts after the source-bound pre-transaction commit marker, adopts the terminal state's
receipt/gas history and appends its result in order, projects receipt indices before
`EndTxTrace`, advances the next tracer index, and emits the post-fold commit marker only when the
complete admitted prefix is exhausted normally. Only `.ok` results are admitted as normal settled
observations; a normal return carrying a truthy `EvmException`/revert is not `.ok`, and a false
`TransactionResult` can still end the trace before the executor throws and is therefore rejected as
malformed input. The refinement adapter separately requires each
terminal history to be a one-item extension of the preceding state and makes the tracer index
projection an explicit invariant.

## Remaining source obligations

The typed closure establishes only source relations for the standard-mainnet DI/BAL route. It
does not establish runtime DI activation, virtual dispatch, plugin replacement, CLR execution,
arithmetic semantics, world-state durability, rollback, or the implementation of the transaction
adapter, tracer, callback, or commit method. The source-bound callback order assumes that
`ProcessTransactions`, the `TransactionsExecuted` subscriber, and the post-transaction
`CommitState(spec)` return normally. Establishing those production facts is part of the explicitly
unproved `OpenSourceCompositionObligation`, not a field supplied to a claimed source refinement. A false-result source path may end the trace before throwing,
which is intentionally outside the successful settled-observation relation. The emitted Lean is a
conditional model-to-model claim; its event tail is concluded only for the generated
completed-result shape.
