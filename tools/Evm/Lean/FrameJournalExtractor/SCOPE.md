# FrameJournalExtractor scope

## Claim

For the byte-pinned standard-mainnet sources and admitted dependency identities,
the generated nested-frame journal transition equals the independently written
Lean specification for every finite admitted operation trace.

The state relation covers accounts, persistent and transient values, warm
accounts and cells, ordered logs, and the destroy set. A child success retains
those mutations. REVERT and exceptional halt restore the top LIFO checkpoint.
Transaction-start persistent originals, `createdThisTx`, and the execution-wide
RIPEMD-160 touch latch are transaction-wide and survive restoration. CREATE
entry adds its destination to `createdThisTx` before the access-tracker
checkpoint; CALL entry does not. A latched rollback first restores the seven
frame-local surfaces and then replays Nethermind's historical zero-balance touch
of an existing empty RIPEMD account. Thus the extensional account value is
restored while the raw account journal can contain a newly replayed touch/cache
entry; the package does not claim byte-for-byte raw journal equality after that
callback.

Production takes the `WorldState` snapshot before `VmState.Initialize` marks a
CREATE destination, while the generated normal form marks first and then takes
one combined abstract snapshot. These orders are extensionally equal in this
projection because frame snapshots deliberately omit persistent originals and
`createdThisTx`; this is not a claim about their physical call order.

No-child CALL/CREATE failure is exact stutter only at this package's
child-composition boundary. Parent-owned effects already performed before that
boundary—including account warming, CREATE destination warming, or a parent
nonce increment on a later collision path—must be present in the input state.

## Admission

The extractor pins twenty production/build files, 53 exact syntax members,
six dependency identities, thirteen theorem signatures, and the imported
WorldJournal generated Lean bytes both directly and through its pinned upstream
manifest. It validates the concrete CALL and
CREATE entry paths, the three nested exit classes, `CommitToParent`/`Dispose`
interaction, world and access snapshot ordering, and the immediate LOG and
SELFDESTRUCT journal leaves. It also pins standard `.std.cs` build selection,
the Amsterdam/mainnet release lineage, normal `WorldState` registration and
access-tracker construction, `ExecutionType.IsAnyCreate`, the RIPEMD latch
predicate and execution-handler dispatch, empty-account test, and post-restore replay order. Generated IR,
source/member/dependency/import aggregate hashes, manifest bytes, and generated
Lean bytes are independently recomputed.

WorldJournal, PersistentStorage, TransientStorage, and LOG dependencies are
production-refinement bindings. CALL/CREATE and SELFDESTRUCT dependencies remain
accepted handwritten references and are labeled as such in the manifest. Their
adapter-to-production obligations are not discharged here.

## Premises

1. `EnableZkEvm` is not true, so the pinned standard-source item selection applies.
2. The executing spec is the pinned Amsterdam instance selected by the pinned mainnet schedule.
3. The injected state uses the pinned main-processing `IWorldState -> WorldState` registration and `tracer.IsTracingAccess` is false.
4. Child body effects satisfy the accepted WorldJournal operation adapter.
5. CALL/CREATE admission facts and exit classification satisfy the pinned boundary reference.
6. SSTORE, TSTORE, LOG, and SELFDESTRUCT results are mapped to the listed world operations by their accepted adapters.
7. Frame and world-snapshot stack depths agree at entry.
8. Each nested child is disposed exactly once on exit.
9. `latchRipemdTouch` is emitted only after the pinned production predicate has set Nethermind's execution-wide latch; the preceding precompile touch itself enters through the WorldJournal adapter.
10. Production `PushTouch` change-kind and consecutive-touch behavior is related extensionally to the abstract same-account update; raw change-kind identity is not claimed.

The refinement proves that every successful admitted transition preserves the
depth relation, and lifts that result to arbitrary successful finite traces.

## Exclusions

Gas and refund settlement, code deposit, `ClearStorage`, precompile bodies other
than the admitted RIPEMD touch/latch boundary,
trie/root/database/provider commit, BAL and tracing-access execution, top-level
transaction/block processing, CLR/JIT behavior, unsafe stacks, pooling, and
tracer correctness are outside the theorem. There is no whole-EVM or
whole-Nethermind claim.
