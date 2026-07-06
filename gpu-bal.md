# BAL-Driven GPU State Root - Implementation Plan (v2, adversarially reviewed)

Status: PLAN + TRACKER (no code written). Target: one or more implementing agents.
Work from section 6 (tracking checklist) using section 4 (TDD protocol); parallel-agent
assignment in section 5.
Repo: D:\GitHub\nethermind, branch off `master`. All file paths below are relative to `src/Nethermind/` unless stated.
Prior art: branch `gpu-experiments` (fetched locally; merge-base `1318d5287a`). Companion strategy doc: `tasks.md` section 8.
v2: this plan was adversarially reviewed by three independent passes (consensus semantics,
implementability, trie/SIMD/GPU internals) with every claim checked against master; the
corrections are folded in below and summarized in the Review Log at the end. Instructions
that look pedantic are pedantic because a reviewer proved the obvious alternative wrong.

---

## 1. Concept and goal

An EIP-7928 Block Access List (BAL) carries the POST-values of every state change in a block.
Coverage is complete (verified): all mutation flows through the BAL-recording world state -
tx execution, coinbase fee credit (`TransactionProcessor.cs:1499`), withdrawals and execution
requests (post-execution index), EIP-4788/2935 system calls (pre-execution index); recording
sites are `Nethermind.State/TracedAccessWorldState.cs` and
`Nethermind.Consensus/Processing/BlockAccessListManager.SystemContracts.cs`. Therefore the
complete post-block state delta is derivable at block arrival WITHOUT executing anything:
reduce each changed field/slot to its last-indexed change. This mirrors what the production
parallel-validation apply path already does (`BlockAccessListManager.StateChanges.cs:35-84`).

That enables a validation lane INDEPENDENT of execution:

```
            +---------------------------------------------------+
 block+BAL ->  Lane A (CPU): execute block, validate exec == BAL |--\
            +---------------------------------------------------+   >-- valid iff BOTH pass
            +---------------------------------------------------+   |
            |  Lane B: delta = reduce(BAL)                       |--/
            |          root  = apply(delta, parentRoot)          |
            |          check root == header.StateRoot            |
            +---------------------------------------------------+
```

Lane B trusts nothing: a lying BAL fails Lane A (exec != BAL); an honest BAL with a wrong
header root fails Lane B. Lane B is where the batch/AVX/GPU hashing lives, because the whole
dirty-trie workload exists up front as one giant independent batch - the exact property the
old `gpu-experiments` branch lacked (it was fed by sequential execution).

Deliverable: Lane B implemented as a SHADOW validator (computes and compares, logs and
counts, never affects consensus), with interchangeable hashing backends (per-message,
multi-core, experimental vertical-SIMD, ILGPU), benchmarked, and soak-tested on the
eip7928/Amsterdam pyspec suite. Promoting the shadow to a consensus lane is OUT OF SCOPE.

## 2. Ground rules for the implementing agent

1. BEFORE writing any code, read these repo rule files IN FULL:
   `.agents/rules/coding-style.md`, `.agents/rules/robustness.md`,
   `.agents/rules/di-patterns.md`, `.agents/rules/test-infrastructure.md`,
   `.agents/rules/performance.md`, `.agents/rules/package-management.md`.
2. Shadow-first: nothing in this plan may change what blocks are accepted or rejected.
3. NEVER call `Commit()` on any trie in Lane B code. Root computation uses `UpdateRootHash`
   only; nothing is persisted (enforced by test T2.10).
4. LANE B MUST USE THE READ-ONLY TRIE STORE (`IWorldStateManager.CreateReadOnlyTrieStore()`,
   `Nethermind.State/IWorldStateManager.cs:35`). This is a hard safety rule, not a style
   choice: the read-only store CLONES nodes (`ReadOnlyTrieStore.cs:19` ->
   `CloneForReadOnly`, `TrieStore.cs:1745-1761`), while the raw store returns SHARED mutable
   cached `TrieNode` instances - and Lane B's hashing writes `Keccak`/`FullRlp` into nodes
   (`TrieNode.cs:553`), which would corrupt Lane A's tree.
5. Do NOT add the ILGPU package reference to `Nethermind.Core`. ILGPU lives in a new leaf
   project (Phase 7). Package versions go in `Directory.Packages.props` AT THE REPO ROOT
   (`D:\GitHub\nethermind\Directory.Packages.props` - there is no copy under src/).
6. Do NOT pass a `CancellationToken` into `Task.Run(work, token)` for the shadow lane task.
   A pre-start cancellation makes the task itself Canceled and the await throws on fast
   blocks (this exact fault previously broke prewarm code in this repo). Use
   `Task.Run(work)`.
7. Test invocation (NET 10 / MTP): do NOT pass `--nologo` (parsed as a filter token; zero
   tests run). Use:
   `dotnet test --project src/Nethermind/<Proj>.Test/<Proj>.Test.csproj -c release --filter "FullyQualifiedName~<Name>"`
   Run test projects SEQUENTIALLY (DLL-lock collisions), timeouts >= 300s, kill leftover
   `Nethermind.*.Test` processes after aborted runs.
8. Comments: terse, one line, state the invariant, never reference this plan or other
   clients. XML docs on public members per AGENTS.md.
9. Every phase ends with its gate GREEN before the next starts. Commit per phase (no
   amends, no force pushes, no Co-Authored-By). Do not push or open PRs without explicit
   instruction.
10. When a signature or behavior below disagrees with the code you find, THE CODE WINS -
    re-read the cited file and adapt. Signatures here were verified on 2026-07-04 master.
11. Work strictly from the section 6 tracking checklist, in the section 4 TDD order.
    Sections "Phase 0".."Phase 8" below are the SPECS the checklist tasks point into;
    read a phase's spec in full before starting its first task.

## 3. Verified codebase facts (API reference)

BAL data model (all in `Nethermind.Core/BlockAccessLists/`):
- `Block.BlockAccessList` : `ReadOnlyBlockAccessList?` (`Nethermind.Core/Block.cs:129`).
- `ReadOnlyBlockAccessList`: `AccountChanges` (returns `ReadOnlyAccountChangesView`),
  `GetAccountChanges(Address)`, `HasAccount`, `ItemCount`.
- `ReadOnlyAccountChanges`: `Address`, `ReadOnlySlotChanges[] StorageChanges`,
  `UInt256[] ChangedSlots`, `UInt256[] StorageReads`, `BalanceChange[] BalanceChanges`,
  `NonceChange[] NonceChanges`, `CodeChange[] CodeChanges`, `bool HasStateChanges` (~:153).
- `BalanceChange(uint Index, UInt256 Value)` - POST-balance. `NonceChange(uint, ulong)` -
  POST-nonce. `CodeChange(uint index, byte[] code)` with auto `ValueHash256 CodeHash`
  (`ValueKeccak.Compute(code)`; null code -> default, so tests must pass non-null).
- `ReadOnlySlotChanges(UInt256 Key, StorageChange[] Changes)`;
  `StorageChange(uint Index, EvmWord Value)` - POST-value; convenience ctor
  `(uint index, in UInt256 value)` byte-swaps for you (`StorageChange.cs:24`).
- ORDERING GUARANTEE (verified): decoded change arrays are strictly ascending by `Index`
  and duplicate-free - the RLP decoders throw otherwise
  (`AccountChangesDecoder.cs:275-288`, `SlotChangesDecoder.cs:35-44`). `Changes[^1]` is
  therefore the final post-block value. Index conventions: pre-execution system calls
  record at index 0, withdrawals/execution-requests at txCount+1
  (`BlockAccessListManager.TxProcessorPool.cs:45-46,128-130`), so last-element-wins covers
  them. Keep a `Debug.Assert` on sortedness anyway (belt and suspenders).
- `EvmWord` = 32 big-endian bytes, from the Nethermind.Numerics.Int256 package (Core
  already references it, `Nethermind.Core.csproj:16`). Conversion to value bytes requires a
  MUTABLE LOCAL (ref cannot take a readonly field):
  ```csharp
  EvmWord w = slot.Value;
  ReadOnlySpan<byte> value = MemoryMarshal
      .CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref w), 32)
      .WithoutLeadingZeros();
  ```
  (pattern from `Nethermind.State/BlockAccessListBasedWorldState.cs:85-92`).

Trie / state:
- `StateTree` (`Nethermind.State/StateTree.cs`): USE the `IScopedTrieStore` ctor -
  `new StateTree(trieStore.GetTrieStore(null), logManager)` (line 29; it sets
  `TrieType.State`, unlike the `ITrieStore` ctor at line 32 which does NOT - a latent
  root-mismatch suspect). `Account? Get(Address, Hash256? rootHash = null)` (line 38) -
  passing `rootHash` reads that root directly, ignoring `RootRef`; ON A MISSING/EVICTED
  NODE IT THROWS (does not return null). `void Set(Address, Account?)` (line 77; null
  deletes the leaf; delete of a missing leaf is a safe no-op).
  `StateTreeBulkSetter BeginSet(int)` (line 83). `UpdateRootHash()` (line 124), inherited
  `SetRootHash(Hash256?, bool resetObjects)` (`PatriciaTree.cs:334`),
  `UpdateRootHash(bool canBeParallel)` (`PatriciaTree.cs:327`), `Hash256 RootHash` (:75),
  `public TrieNode? RootRef` (:58), `PatriciaTree.EmptyTreeHash` (:34).
- `StorageTree` (`Nethermind.State/StorageTree.cs`): scoping RESOLVED - copy
  `TrieStoreScopeProvider.cs:57` exactly:
  ```csharp
  using Nethermind.Trie.Pruning;   // GetTrieStore(Address) extension
  StorageTree storageTree = new(trieStore.GetTrieStore(ad.Address), preStorageRoot, logManager);
  ```
  The extension (`Nethermind.Trie/Pruning/ITrieStoreExtensions.cs:12-13`) maps to
  `GetTrieStore((Hash256)address.ToAccountPath)` - do NOT hash the address yourself, and
  note `StateReader.cs:37` passes the root to `Get` instead of the ctor; use the ctor form.
  `Set(in UInt256 index, byte[] value)` (line 148): zero/empty value deletes the leaf
  (`SetInternal` -> `value.IsZero()`, lines 171-183); write-zero-to-missing-slot is a no-op
  (`PatriciaTree.cs:580-582`). This exactly matches the production BAL apply
  (`BlockAccessListManager.StateChanges.cs:75` passes `[.. valueBytes.WithoutLeadingZeros()]`).
- Trie store source for Lane B: `IWorldStateManager.CreateReadOnlyTrieStore()`
  (`IWorldStateManager.cs:35`, impl `WorldStateManager.cs:79`) - returns
  `IReadOnlyTrieStore : ITrieStore`. Do not try to reach the private `_readOnlyTrieStore`.
- SCOPE REQUIREMENT (verified; from external review): `ITrieStore.BeginScope(BlockHeader?)`
  is part of the interface (`ITrieStore.cs:25`). The halfpath read-only store implements it
  as a NO-OP (`ReadOnlyTrieStore.cs:30`), but the FLAT read-only store REQUIRES it: it
  gathers the read-only snapshot bundle for that block and initializes its node adapter
  (`Nethermind.State.Flat/ScopeProvider/FlatReadOnlyTrieStore.cs` - `Resolve` throws
  "BeginScope has not been called" otherwise, and `BeginScope` itself throws
  `InvalidOperationException` when the state for that block is not found). Therefore Lane B
  ALWAYS wraps computation in `using IDisposable _ = trieStore.BeginScope(parentHeader);` -
  portable across both backends, and it is what makes flat configurations work. Do NOT use
  `HasRoot` for capability detection: the flat store's `HasRoot(stateRoot)` overload
  returns true unconditionally.
- `Account(in ulong nonce, in UInt256 balance, Hash256 storageRoot, Hash256 codeHash)`
  (`Nethermind.Core/Account.cs:46`). `Account.IsEmpty` = codeHash null && balance 0 &&
  nonce 0 (line 85) - NOTE it ignores the storage root; this drives the deletion rule in
  Phase 2. TWO constants named `Keccak.OfAnEmptyString` exist: `ValueHash256`
  (`Keccak.cs:18`) and `Hash256` (`Keccak.cs:85`) - the Account ctor needs the `Hash256`
  one; convert `ValueHash256? vh` via `new Hash256(vh)`.
- TrieNode internals (Phase 5 lives in `Nethermind.Trie`; `Keccak` setter is internal,
  `TrieNode.cs:180`): `ResolveKey` (:522), `GenerateKey` (:535) - hashing rule (verified):
  encoded RLP >= 32 bytes OR root (path.Length == 0) -> keccak; else the node is INLINED
  (Keccak stays null) (:538,:559). `GenerateKey` WRITES `FullRlp` into the node (:553).
  `TryGetDirtyChild` (:663-691) is the SAFE descent primitive;
  `GetChild`/`GetChildWithChildPath` can MUTATE the tree (`UnresolveChild`, :761-764) - do
  not use them in wave traversal. RLP encoding does NOT depend on TreePath (leaf/extension
  encode their own stored `Key` HexPrefix; branch encodes children) - the wave code needs
  only a DEPTH counter for the root rule, not path threading.
  The wave encoder must NOT call `TrieNodeDecoder.RlpEncodeBranch`/`EncodeExtension` or
  `ResolveKey` - they RECURSE into children via `ResolveKey`
  (`TrieNode.Decoder.cs:145,150,211-415`) and would serially hash the whole subtree,
  defeating batching. Inline-child copying model: `WriteChildrenRlpBranchNonRlp`
  (`TrieNode.Decoder.cs:369-379`).
- Parallel pattern: `ParallelUnbalancedWork.For(from, to, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16, init, initValue, action, finally)`
  (`Nethermind.Core/Threading/ParallelUnbalancedWork.cs:274-281`); production usage with
  largest-first ordering: `PersistentStorageProvider.std.cs:50-78`.

Keccak (IMPORTANT baseline fact): `KeccakHash.ComputeHash(ReadOnlySpan<byte>, Span<byte>)`
(`KeccakHash.cs:72`) already dispatches the PERMUTATION to an AVX-512 kernel on capable
hardware - `KeccakF1600Avx512F`, a HORIZONTAL single-message design
(`Nethermind.Core/Crypto/KeccakHash.std.cs:33-41,254-`; uses `Avx512F.TernaryLogic`,
`PermuteVar8x64`, `RotateLeft`/`RotateLeftVariable`). So "per-message" is NOT scalar on
AVX-512 boxes, and `Avx512F.RotateLeft(Vector512<ulong>, byte)` is verifiably available
(used at std.cs:293,305). Rate = 136 bytes; padding = 0x01 first pad byte, 0x80 final byte,
coinciding to 0x81 when len % 136 == 135 (verified `KeccakHash.cs:125-126`).

Old GPU code to port from (do NOT copy into Core):
`git show gpu-experiments:src/Nethermind/Nethermind.Core/Crypto/KeccakHash.Gpu.cs`
- ILGPU 1.5.1-era; kernel `Action<Index1D, ArrayView<int>, ArrayView<byte>, ArrayView<ulong>>`.
- Offsets convention (verified consistent with Phase 4's interface): `offsets[i]` =
  exclusive END of input i; start of input i = `offsets[i-1]` (0 for the first); last
  offset = total length (kernel lines 219-220 index exactly this way).
- Known flaws to FIX in the port: (a) global `lock` around dispatch; (b) lazy accelerator
  creation; (c) unpooled per-call device buffers; (d) `Console.WriteLine` debug helper;
  (e) MANAGED ARRAY ALLOCATIONS INSIDE THE KERNEL - `new ulong[25]` state per thread
  (line 224) and `RC`/`C`/`D`/`B` arrays allocated per PERMUTATION CALL
  (lines 276-289): hoist `RC` to a static/kernel constant, convert state and temporaries
  to fixed-size locals; (f) no sorting by block count -> warp divergence (the per-thread
  block loop at line 230 depends on input length).

Processing / hook points:
- `BranchProcessor` (`Nethermind.Consensus/Processing/BranchProcessor.cs`): PRIMARY
  constructor (lines 19-27) already ends with `IBlockCachePreWarmer? preWarmer = null`.
  The ONLY production construction is DI:
  `.AddScoped<IBranchProcessor, BranchProcessor>()` at
  `Nethermind.Init/Modules/BlockProcessingModule.cs:64`. Adding a trailing optional
  parameter breaks NO call sites (tests use DI decorators/stubs). `Process(BlockHeader?
  baseBlock, ...)` - `baseBlock` is NULL for genesis (:56-62); the parent for block i>0 is
  the previous processed block's header (`preBlockBaseBlock`, :163). Per-block
  `blockProcessor.ProcessOne(...)` at :133.
- Root equality ground truth: `Nethermind.Consensus/Validators/BlockValidator.cs:210-213`.
- Layering (verified - state this so nobody panics): `Nethermind.Consensus` references
  `Nethermind.Blockchain` (`Nethermind.Consensus.csproj:12`) and transitively
  `Nethermind.State`/`Nethermind.Trie`; Consensus code already uses `IWorldStateManager`
  and `ITrieStore`. Config in Consensus + metrics in `Nethermind.Blockchain/Metrics.cs`
  are both reachable. No new ProjectReference needed for Phases 1-3.
- Config pattern: interface + CONCRETE CLASS pair, co-located (see
  `Nethermind.Consensus/IMiningConfig.cs` + `MiningConfig.cs`); auto-registered by
  assembly scan (`ConfigExtensions.cs:70`). `[ConfigItem(Description=..., DefaultValue=...)]`
  on the interface; defaults duplicated as initializers on the impl.
- Metrics pattern: `Nethermind.Blockchain/Metrics.cs` (`[GaugeMetric]` + `[Description]`).

Tests:
- Building an `ITrieStore` in unit tests: DO NOT use `RawScopedTrieStore` (it is an
  `IScopedTrieStore`, NOT an `ITrieStore` - it will not compile against the calculator).
  Use `TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance)` from
  `Nethermind.Core.Test` (`TestRawTrieStore : ... : ITrieStore`,
  `Nethermind.Core.Test/TestRawTrieStore.cs:20`; usage example
  `StorageProviderTests.cs:800`).
- BAL test builders (use these, not raw ctors): `Build.An.AccountChanges`
  (`Nethermind.Core.Test/Builders/Build.AccountChanges.cs:8` -> `.WithAddress`,
  `.WithBalanceChanges`, `.WithNonceChanges`, `.WithCodeChanges`,
  `.WithStorageChanges(key, params StorageChange[])`, `.WithStorageReads`) and
  `Build.A.BlockAccessList` (`Build.BlockAccessList.cs:8` ->
  `.WithAccountChanges(params ReadOnlyAccountChanges[])`). The folder
  `Nethermind.Core.Test/BlockAccessLists/` already exists.
- EIP-7928 pyspec suite: project `Ethereum.Blockchain.Pyspec.Test`; the BAL-era classes
  are `Amsterdam*` in `Tests.cs` (`AmsterdamBlockchainTests` :21,
  `AmsterdamEngineBlockchainTests` :36, and the `AmsterdamParallel*` matrix :52-57).
  Filter: `--filter "FullyQualifiedName~Amsterdam"`. Fixtures auto-download (network
  needed) from the `tests-bal` release tarball (`Constants.cs:8-10`). Heavy fixtures are
  CI-gated to linux-x64 via `CiRunnerGuard.SkipIfNotLinuxX64` (`PyspecTestFixture.cs:29,
  166-172`) - on this Windows box they run as long as the `CI` env var is UNSET.

Destroy-then-recreate: NOT a gap on BAL-era rules (resolved via external review + EIP
check). EIP-6780 is active on every BAL-era fork (`IReleaseSpec.IsEip6780Enabled`), so
SELFDESTRUCT only destroys a contract CREATED IN THE SAME TRANSACTION - a pre-existing
contract (the only kind that could have surviving on-disk slots) cannot be destroyed at
all. The only same-block destroy+recreate shape is an account created within the block:
its pre-state is null (storage correctly seeds from the empty tree) and its destroyed
incarnation's same-tx writes are already unwound to reads by the recording
(`BlockAccessListAtIndex.cs:233-285`). Selfdestruct of a pre-existing contract merely
moves balance - ordinary BAL balance changes. CONSEQUENCE: there is NO recreation
heuristic, NO known-limitation metric, and NO mismatch downgrade anywhere in this plan.
Do not add one: any such heuristic fires on routine EIP-7702 delegation updates
(code-only changes on accounts with existing code/storage are normal -
`TracedAccessWorldState.cs` treats SetCode as an ordinary code change) and would mask the
exact mismatches the shadow exists to surface. Every mismatch is a mismatch.

AuRa caveat: the unconditional EIP-161 deletion in Phase 2 assumes
`Eip158IgnoredAccount == null` (true for Ethereum devnets and the pyspec suite). AuRa
chains set it to `Address.SystemUser` (`AuRaChainSpecEngineParameters.cs:87`); the BAL
recorder already suppresses SystemUser zero-touches, so no collision is expected, but this
is UNVERIFIED on an AuRa BAL chain - re-verify before enabling the shadow there.

---

## 4. TDD protocol (mandatory for every phase)

Each phase is worked strictly RED -> GREEN -> GATE:

1. STUB: create the phase's new types/members exactly per the spec signatures, every body
   `throw new NotImplementedException();`. This exists only so tests COMPILE.
2. RED: write ALL of the phase's tests (the T-numbered list) BEFORE any implementation.
   Expected values come from EXISTING code (`ValueKeccak.Compute`, `UpdateRootHash`,
   directly-built trees, the pyspec fixtures) - they are fully writable up front.
3. RED CHECK: run the phase's test filter. EVERY new test must FAIL as an assertion
   failure or `NotImplementedException` - a compile error or an unexpectedly-passing test
   means the test is wrong; fix the test, not the stub.
4. GREEN: implement until the filter is fully green. Never delete or weaken a failing
   test to get there; if a test looks wrong, re-verify its expected value against master
   code (Appendix A cites the sources) and change it only with a comment citing that code.
5. GATE: run the phase gate, record the result (test counts, timings) in the commit
   message, tick the phase's boxes IN THIS FILE (it is the tracking document), commit as
   `gpu-bal: phase N - <summary>`.

Never tick a gate checkbox without recorded evidence. A ticked box with no commit is a lie.

## 5. Dependency graph and parallel tracks

```
P0 setup (everyone, once)
 |
 +-- TRACK A - consensus core (SINGLE OWNER, strictly sequential)
 |     P1 delta -> P2 calculator -> P3 shadow hook -> G3 pyspec milestone
 |
 +-- TRACK B - hashing infra (independent of Track A)
       P4 batch abstraction + per-message backend
        +-- B1: P6a multi-core backend           (independent after P4)
        +-- B2: P6b vertical SIMD kernel          (independent after P4; its G6
        |        benchmark comparison needs 6a)
        +-- B3: P7 ILGPU backend                  (independent after P4; consumes the
        |        block-count grouping utility - see ownership note)
        +-- TRACK C: P5 wave merkleization        (needs only P4's interface;
                 its T5.6 wiring test needs P2)

MERGE POINTS: P5 calculator-wiring needs P2. P6c (backend wiring + across-tries
parallelism) needs P2 + P5. P8 needs everything.
```

Parallel-agent assignment (max useful parallelism = 3):
- AGENT 1: Track A end to end. Consensus-critical; one owner, no sharing. Projects
  touched: Nethermind.Core (BlockAccessLists), Nethermind.State, Nethermind.Consensus,
  Nethermind.Init, Nethermind.Blockchain (metrics).
- AGENT 2: P4 then Track C (P5). Projects: Nethermind.Core (Crypto), Nethermind.Trie.
- AGENT 3 (after Agent 2 lands P4): P6a, then P6b and/or P7. Projects: Nethermind.Core
  (Crypto), new Nethermind.Crypto.Gpu.
- Disjoint-project rule holds except: (a) the BLOCK-COUNT GROUPING UTILITY is shared by
  P6b and P7 - it is OWNED by P6 (task 6.3); P7 consumes it, so P7's grouping-dependent
  tasks wait for 6.3; (b) `IKeccakBatchHasher` (P4) is the contract everyone codes
  against - land P4 before parallelizing. Use separate worktrees/branches; Track A merges
  first (it is the value; everything else is acceleration).

SINGLE-AGENT ORDER (recommended if not parallelizing):
P0, P1, P2, P3 (G3 milestone - earliest end-to-end consensus signal), P4, P5, P6a, P6b,
P7, P8. Rationale: the shadow's correctness signal is worth more than any speedup;
G3 green on the recursive path de-risks everything downstream.

## 6. Tracking checklist (the working document - tick boxes here)

Tick boxes as completed (per section 4 rule 5). Task numbering: <phase>.<n>.

### Phase 0 - Setup [everyone]
- [x] 0.1 Read the six rule files (Ground rule 1)
- [ ] 0.2 Fetch `gpu-experiments`; extract kernel reference to scratchpad
- [x] 0.3 `dotnet build src/Nethermind/Nethermind.slnx -c release` green
- [x] 0.4 Baseline test runs recorded (Core.Test BlockAccessLists; State.Test StateTreeTests)
- [x] 0.5 GATE G0

### Phase 1 - Delta reduction [Track A]
- [x] 1.1 STUB `BalPostStateDelta` + `AccountDelta` + `SlotWrite` (spec: Phase 1; impl ref: A1)
- [x] 1.2 RED write T1.1-T1.9 (`BalPostStateDeltaTests.cs`, use `Build.An.AccountChanges` /
        `Build.A.BlockAccessList` builders - section 3)
- [x] 1.3 RED CHECK: 9 failures, zero compile errors
- [x] 1.4 GREEN implement `Reduce` per A1
- [x] 1.5 GATE G1 + commit

### Phase 2 - Root calculator [Track A, after P1]
- [x] 2.1 STUB `BalStateRootCalculator` (`ComputeRoot(BlockHeader parent, ...)` incl. the
        `BeginScope(parent)` wrapper - section 3 scope requirement)
- [x] 2.2 RED test fixture helper: build-pre-state + expected-root-by-direct-apply helper
        (`TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance)` - NOT RawScopedTrieStore)
- [x] 2.3 RED write T2.1-T2.14 (incl. T2.10 write-recording-DB guard, T2.13 EIP-161
        residual-storage delete, T2.14 7702-style code-only change on account with storage)
- [x] 2.4 RED CHECK: 14 failures, zero compile errors
- [x] 2.5 GREEN implement three-pass `ComputeRoot` per A2 (pass separation, deletion rule,
        explicit-root reads are non-negotiable)
- [x] 2.6 GATE G2 + commit

### Phase 3 - Shadow integration [Track A, after P2]
- [x] 3.1 STUB `IBalStateRootConfig` + `BalStateRootConfig` (BOTH files) + `BalStateRootShadow`
- [x] 3.2 RED write T3.1-T3.6 (incl. T3.5 slow-calculator non-blocking proof, T3.6
        BeginScope-required fake store)
- [x] 3.3 RED CHECK
- [x] 3.4 GREEN implement shadow: non-blocking `Compare` via `ContinueWith` (values
        captured, no `.Wait()`), bounded in-flight cap 4 with `BalShadowRootSkipped`,
        `Task.Run` WITHOUT token, full try/catch, self-disable after N consecutive errors
- [x] 3.5 Metrics added to `Nethermind.Blockchain/Metrics.cs` (5 counters/gauges per spec)
- [x] 3.6 Hook `BranchProcessor` (trailing optional ctor param; genesis null-parent guard)
- [x] 3.7 DI registration in `BlockProcessingModule.cs` (~line 64), sourced from
        `IWorldStateManager.CreateReadOnlyTrieStore()`
- [x] 3.7a Startup capability log: ONE Info line stating shadow enabled/disabled and the
        hashing capabilities available on this host (spec: Phase 3 "Startup capability log")
- [x] 3.8 Test-only config override so pyspec runs with `Enabled=true`
- [x] 3.9 GATE G3 MILESTONE: Amsterdam pyspec suite green, Mismatches==0, Errors==0
        (no carve-outs); log captured; commit
- [x] 3.10 (conditional) Any G3 mismatch -> root-cause against Phase 2 rules before ANY
        further phase proceeds

### Phase 4 - Batch hashing abstraction [Track B; unblocks B1/B2/B3/C]
- [x] 4.1 STUB `IKeccakBatchHasher` + `PerMessageKeccakBatchHasher`
- [x] 4.2 RED write T4.1 (differential, `[TestCaseSource]` over backends), T4.2 (boundary
        lengths incl. 135), T4.3 (empty-input KAT)
- [x] 4.3 RED CHECK
- [x] 4.4 GREEN implement per-message backend
- [x] 4.5 GATE G4 + commit (this unblocks parallel agents)

### Phase 5 - Wave merkleization [Track C, after P4; wiring task needs P2]
- [x] 5.1 STUB `BatchedTrieCommitter.UpdateRootHashBatched`
- [x] 5.2 RED write T5.1 fuzz (500 seeded random tries vs `UpdateRootHash`) + T5.2-T5.5
        edge tries - ALL writable before implementation (baseline is existing code)
- [x] 5.3 RED CHECK
- [x] 5.4 GREEN: post-order dirty collect (`TryGetDirtyChild` ONLY; depth counter) - A4
- [x] 5.5 GREEN: flat node encoder (A3/A4 ChildRef rules; model
        `WriteChildrenRlpBranchNonRlp`; NO ResolveKey/RlpEncodeBranch; add minimal
        `internal` TrieNode helpers if raw slot access requires them)
- [x] 5.6 GREEN: per-level flatten + `HashBatch` + keccak assignment; <32B inline rule;
        root-always-hashed; `FullRlp` populated on every encoded node
- [x] 5.7 T5.1-T5.5 green
- [x] 5.8 [needs P2] alternate `ComputeRoot(..., IKeccakBatchHasher)` + T5.6 (Phase 2
        suite via batched path, `[TestCaseSource]` both paths)
- [x] 5.9 GATE G5 + commit

### Phase 6 - AVX backends [Track B1/B2, after P4]
- [x] 6.1 RED: register-to-be-written backends into T4.x/T5.1 `TestCaseSource` (skipped
        entries until types exist); write group-dispatch edge tests (9/mixed-4-counts/7)
- [x] 6.2 GREEN 6a `ParallelKeccakBatchHasher` (`ParallelUnbalancedWork` pattern;
        per-worker output slices; threshold ~256)
- [x] 6.3 GREEN block-count grouping utility (SHARED: P7 consumes; land early, keep API
        stable) - uniform groups + sort permutation array
- [x] 6.4 GREEN 6b vertical kernel, uniform-groups strategy first (A5; state-in-scratch
        8-way OR 4-way; `Avx512F.RotateLeft`; constants COPIED from KeccakHash.cs)
- [x] 6.5 GREEN 6b run-to-max-with-snapshots variant (A5 strategy 2 - digest snapshot at
        each message's own final block; NEVER absorb past a message's end)
- [x] 6.6 All differential + edge tests green on every supported ISA (Assert.Ignore where
        unsupported)
- [x] 6.7 Benchmarks (`KeccakBatchBenchmarks.cs`): N x length-mix matrix; record table;
        ALSO compare 6a static-slice partition vs per-index work-stealing (review
        finding: one-index-per-worker defeats ParallelUnbalancedWork stealing on
        non-uniform lengths and causes boundary false sharing; per-index fixes both -
        adopt whichever measures better on the trie-level mix, report P99 worker time)
- [ ] 6.8 JitAsm inspection of 6b round function; record spill behavior (measure, don't assume)
- [x] 6.9 [needs P2+P5] 6c wiring: backend chain into level hashing + across-storage-tries
        parallelism (largest-first); level barrier preserved; Phase 2 + T5 suites green
- [x] 6.10 GATE G6: 6a near-linear scaling shown; 6b adopt/reject DECISION recorded with
        numbers + commit

### Phase 7 - ILGPU backend [Track B3, after P4; grouping from 6.3]
- [x] 7.1 Verify current ILGPU release + API surface (incl. page-locked buffer API);
        add `PackageVersion` to REPO-ROOT `Directory.Packages.props`
- [x] 7.2 New leaf project `Nethermind.Crypto.Gpu` + `Nethermind.Crypto.Gpu.Test`
- [x] 7.3 RED: plug GPU backend into T4.1/T4.2/T4.3/T5.1 suites behind
        `TryCreate` + `Assert.Ignore("no GPU")`; write T7.1 (100k-message stress)
- [x] 7.4 GREEN kernel port with ALL flaws fixed (a-f in section 3: ctor-time
        context/accelerator, pooled pinned staging, persistent device buffers, no
        in-kernel `new[]`, RC as constant, sort-by-block-count via 6.3) - A6
- [x] 7.5 GREEN `ThresholdKeccakBatchHasher` routing + permanent-fallback containment
- [x] 7.6 DI/backend selection wiring (`UseGpu` + `TryCreate`) incl. the single
        `Nethermind.Init -> Nethermind.Crypto.Gpu` ProjectReference (agreed boundary: see
        non-goals) + extend the 3.7a startup capability line with accelerator name/memory
        or the unavailability reason
- [x] 7.7 Tests green on GPU box; skipped-green on CI
- [x] 7.8 GATE G7: measured crossover recorded; `GpuMinBatch` default updated to it + commit

### Phase 8 - Validation and soak [after everything]
- [x] 8.1 Consolidated benchmark table (per-message / 6a / 6b / GPU) in PR description.
        GPU numbers MUST be reported for BOTH device classes measured: the CUDA discrete
        card (RTX PRO 6000: 0.49x @4k, 4.59x @64k, 3.47x @262k vs 6a) AND the OpenCL
        AMD iGPU (gfx1036: 0.71x @4k, 2.28x @64k) - the iGPU class is NOT a throwaway:
        the reproducible-benchmark (benchmarkoor) runners are AMD Ryzen 7 PRO 8700GE
        boxes with Radeon 780M iGPUs (OpenCL only), so the OpenCL-iGPU row is what that
        infrastructure would exhibit with UseGpu enabled; actual CI runners are assumed
        to have NO GPU (GPU tests skip-green via TryCreate + Assert.Ignore - verify the
        skip path in the CI run); state both crossovers and the per-environment caveat
- [x] 8.2 Amsterdam pyspec with shadow + chosen backend: Mismatches==0, Errors==0;
        median + p95 `BalShadowRootLastMicros` vs execution time reported
- [ ] 8.3 Devnet soak >= 1000 blocks (if a BAL devnet is live); metrics captured
- [x] 8.4 `gpu-bal-results.md` written (measurements, hardware, crossovers, promotion
        recommendation)
- [x] 8.5 GATE G8: zero consensus-path changes confirmed by diff review + commit

### Phase 9 - Merged cross-trie wave dispatch [extension, post-plan, 2026-07-05]
Motivation: the batched path parallelizes ACROSS storage tries but issues per-trie
HashBatch calls, so no single dispatch reaches GPU-scale width on a normal block
(GpuMinBatch=65536 vs tiny per-trie levels). Tries are independent and the level barrier
is per-tree only, so each wave step can CONCATENATE every tree's next-deepest unprocessed
level into ONE dispatch - per-tree bottom-up order preserved, cross-tree alignment
arbitrary. Step 0 (all leaf levels together) is the only realistic per-block GPU feed.
ADOPTION GATE (asymmetric): all correctness suites green AND not slower than the
committed across-tries path on CPU hashers AND materially wider dispatches (GPU
enablement is the point even if CPU-neutral); if step-0 widths on realistic shapes stay
under the threshold band, record that as the finding (GPU remains a bulk-context story).
- [x] 9.1 Refactor BatchedTrieCommitter internals into shared helpers (single-tree API
        and all existing tests stay green, behavior-identical)
- [x] 9.2 STUB multi-tree API (UpdateRootHashesBatched(trees, hasher) or justified shape)
- [x] 9.3 RED: multi-tree differential fuzz (mixed fresh/committed/re-resolved tries,
        varied depths) + edges (empty list, single tree == single-tree path, clean root,
        all-inline tiny trees, retained-RLP mutation shape across multiple trees)
- [x] 9.4 GREEN merged wave: per step, concatenate every tree's next-deepest level; one
        HashBatch per step; encode-parallel across trees allowed; per-tree root rule
- [x] 9.5 Wave-width observability seam (internal stats/callback) for the measure gate
- [x] 9.6 Wire as the batched-path storage-root stage in BalStateRootCalculator (caller
        hasher drives the merged wave; recursive overload untouched; justify what happens
        to StorageTrieParallelThreshold); full calculator suite green on both paths
- [x] 9.7 Benchmark: realistic skewed block shapes ({100,400,1600} accounts, slot counts
        mostly 1-4 with a heavy tail); width distribution per step + end-to-end vs the
        committed path across per-message/6a/CUDA-threshold hashers
- [x] 9.8 GATE G9: adoption verdict per the asymmetric gate, decision + numbers recorded
        either way; reviews (Claude + codex) + commit

### Phase 10 - Device-resident MPT flow with changed-node return [extension, 2026-07-05]
Idea (user): move the whole per-level encode+hash+splice onto the GPU (chained kernels in
device memory, no per-level round trips) and have the GPU return the complete changed-node
set as (keccak, RLP) pairs in ONE D2H (~2.5MB per 19k-node wave) - that list is exactly
what Commit persists. Maximal split: CPU keeps trie restructuring (Set() determines the
DAG) + DB I/O; GPU produces the persistable state update, not just the root.
NON-BAL RELEVANCE (the larger prize): the machinery is BAL-independent once a dirty node
set exists - which is EVERY block commit. BALs only move when the delta is knowable
(arrival vs post-execution). Lane A''s standard root computation is on the critical path
of every block and has the identical wave shape at commit time, so this would accelerate
ordinary block processing on every node with no BAL anywhere - at the cost of touching
the consensus-critical commit path (live TrieStore/pruning) instead of the shadow''s
cloned read-only world.
MEASURE FIRST (results go to gpu-bal-results.md):
- [x] 10.1 Measure the baseline: root-computation share of block processing time on the
        STANDARD (non-BAL) commit path for realistic blocks (state + storage tries;
        expb/benchmarkoor payload data or a representative replay) - if the share is
        small, stop here and record it
- [x] 10.2 Measure the offloadable fraction: within that share, how much is per-node
        encode+hash+splice (GPU-movable) vs traversal/restructure/IO (CPU-bound)
- [x] 10.3 Estimate transfer/launch budget: dirty-set sizes and (keccak, RLP) return
        volumes per mainnet-shape block; compare against the measured ~300us/dispatch
        floor and H2D/D2H bandwidth - net win bound
- [x] 10.4 DECISION: record go/no-go in gpu-bal-results.md with the numbers; a go
        requires a second byte-exact MPT encoder in kernel code with mandatory
        differential fuzzing against the CPU encoder (the same authority that caught
        the null-sibling splice divergence)
- [ ] 10.5 (only on go) prototype device-resident kernel chain on the SHADOW lane first
        (consensus-safe proving ground), then evaluate Lane A integration separately

---

Everything from here to Appendix A is SPEC (reference detail for the tracker tasks above).

## Phase 0 - Setup and baseline

1. Read the rule files (Ground rule 1).
2. `git fetch origin gpu-experiments:gpu-experiments` (if absent) and extract the old
   kernel to scratch for reference (do not commit):
   `git show gpu-experiments:src/Nethermind/Nethermind.Core/Crypto/KeccakHash.Gpu.cs > <scratchpad>/keccak-gpu-reference.cs`
3. Build baseline: `dotnet build src/Nethermind/Nethermind.slnx -c release` green.
4. Run baseline tests you will touch: `Nethermind.Core.Test` filter `BlockAccessLists`;
   `Nethermind.State.Test` filter `StateTreeTests`.

Gate G0: clean build, baseline tests green.

## Phase 1 - BAL -> post-state delta reduction (pure, no trie)

New file: `Nethermind.Core/BlockAccessLists/BalPostStateDelta.cs`

```csharp
namespace Nethermind.Core.BlockAccessLists;

/// <summary>Final post-block state changes reduced from a block access list.</summary>
public sealed class BalPostStateDelta
{
    public readonly struct AccountDelta
    {
        public Address Address { get; init; }
        public UInt256? Balance { get; init; }        // last BalanceChange.Value, else null (unchanged)
        public ulong? Nonce { get; init; }            // last NonceChange.Value, else null
        public ValueHash256? CodeHash { get; init; }  // last CodeChange.CodeHash, else null
        public SlotWrite[] Storage { get; init; }     // one entry per changed slot, final value (zeros included)
    }

    public readonly record struct SlotWrite(UInt256 Slot, EvmWord Value);

    public AccountDelta[] Accounts { get; }           // only accounts with HasStateChanges

    public static BalPostStateDelta Reduce(ReadOnlyBlockAccessList bal) { ... }
}
```

Full implementation: Appendix A1.

Reduction rules:
- Skip accounts where `HasStateChanges` is false (touched-but-unchanged accounts appear in
  BALs for hash equality but must not dirty the trie - verified consistent with the real
  commit's zero-touch handling).
- Per field: LAST element of each ordered array (`BalanceChanges[^1]` etc.);
  `Debug.Assert` ascending indices (decoder already guarantees it in Release).
- Storage: for each `ReadOnlySlotChanges sc`, emit `new SlotWrite(sc.Key, sc.Changes[^1].Value)`;
  skip `sc.Changes.Length == 0` defensively. Do NOT drop zero values (zero = trie delete).

Tests: `Nethermind.Core.Test/BlockAccessLists/BalPostStateDeltaTests.cs`, constructed with
`Build.An.AccountChanges` / `Build.A.BlockAccessList` (see section 3). Cases:
- T1.1 empty BAL -> zero accounts.
- T1.2 read-only account (only StorageReads) -> excluded.
- T1.3 single balance change -> Balance set, Nonce/CodeHash null.
- T1.4 balance changes at indices 0,3,7 -> value at 7 wins.
- T1.5 slot written at indices 1 and 4 -> index-4 value.
- T1.6 slot written non-zero then zero -> SlotWrite kept with zero value.
- T1.7 code change (non-null code!) -> CodeHash == ValueKeccak.Compute(code).
- T1.8 mixed account (balance+nonce+2 slots).
- T1.9 multiple accounts.

Gate G1: `dotnet test --project src/Nethermind/Nethermind.Core.Test/Nethermind.Core.Test.csproj -c release --filter "FullyQualifiedName~BalPostStateDelta"` green.

## Phase 2 - CPU root-from-delta calculator (correctness core)

New file: `Nethermind.State/BalStateRootCalculator.cs`

```csharp
namespace Nethermind.State;

/// <summary>Computes the post-block state root from a BAL-derived delta without executing.</summary>
/// <remarks>Read-only: resolves pre-state through a read-only trie store, never commits.</remarks>
public sealed class BalStateRootCalculator(ITrieStore trieStore, ILogManager logManager)
{
    public Hash256 ComputeRoot(BlockHeader parent, BalPostStateDelta delta) { ... }
}
```

The injected `trieStore` MUST be the read-only store (Ground rule 4). The FIRST statement
of `ComputeRoot` is `using IDisposable _ = trieStore.BeginScope(parent);` (section 3 scope
requirement - no-op on halfpath, mandatory on flat), then
`Hash256 parentStateRoot = parent.StateRoot!;`. Node eviction under pruning and
missing-state-at-parent both surface as THROWS (`Get` / `BeginScope`) - callers (Phase 3)
wrap the entire `ComputeRoot`.

Full skeleton: Appendix A2 (MPT/RLP background: A3).

Algorithm - three strict passes (the pass separation is load-bearing: pre-state reads must
never interleave with writes, or a later read through the mutating `RootRef` would observe
a partially-updated tree):

PASS A - pre-state reads (no mutation yet):
1. `StateTree stateTree = new(trieStore.GetTrieStore(null), logManager);`
   (the IScopedTrieStore ctor - it sets `TrieType.State`; the ITrieStore ctor does not).
2. For each `AccountDelta ad`: `Account? pre = stateTree.Get(ad.Address, parentStateRoot);`
   Always pass `parentStateRoot` explicitly.

PASS B - storage roots and account composition:
3. For each `ad`, compose final scalar fields FIRST:
   ```csharp
   ulong nonce = ad.Nonce ?? /* pre nonce, converted to ulong as the Account ctor needs */ 0;
   UInt256 balance = ad.Balance ?? pre?.Balance ?? UInt256.Zero;
   Hash256 codeHash = ad.CodeHash is { } vh ? new Hash256(vh)
                                            : (pre?.CodeHash ?? Keccak.OfAnEmptyString); // Hash256 overload, Keccak.cs:85
   ```
   (Check `Account.Nonce`'s actual property type and convert accordingly - the ctor takes
   `in ulong`.)
4. DELETION RULE (verified against `StateProvider.cs:572` + `Account.cs:85` - the real
   commit deletes on `Account.IsEmpty`, which IGNORES the storage root):
   `bool isEmpty = nonce == 0 && balance.IsZero && codeHash == Keccak.OfAnEmptyString;`
   If `isEmpty`: mark for deletion and SKIP storage-root computation entirely (a deleted
   leaf orphans its storage subtree; this is exactly how selfdestruct of a contract with
   on-disk storage stays consensus-correct). Do NOT add a storage-root condition to this
   check - that was a reviewed-and-rejected bug (false mismatches on drained accounts).
5. Else compute the storage root:
   - No storage writes: `storageRoot = pre?.StorageRoot ?? PatriciaTree.EmptyTreeHash`.
   - Writes present:
     ```csharp
     Hash256 preStorageRoot = pre?.StorageRoot ?? PatriciaTree.EmptyTreeHash;
     StorageTree storageTree = new(trieStore.GetTrieStore(ad.Address), preStorageRoot, logManager);
     foreach (SlotWrite slot in ad.Storage)
     {
         EvmWord w = slot.Value;                       // mutable local: ref needs an lvalue
         ReadOnlySpan<byte> value = MemoryMarshal
             .CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref w), 32)
             .WithoutLeadingZeros();
         storageTree.Set(in slot.Slot, value.ToArray()); // zero -> [] -> delete (StorageTree.cs:171-183)
     }
     storageTree.UpdateRootHash(canBeParallel: false);
     Hash256 storageRoot = storageTree.RootHash;
     ```
   (No recreation flag - see the destroy-then-recreate note in section 3: the scenario
   is impossible under EIP-6780 and any heuristic false-positives on EIP-7702 updates.)

PASS C - state-tree writes and root:
6. `stateTree.SetRootHash(parentStateRoot, true);`
7. Using `stateTree.BeginSet(delta.Accounts.Length)` (or plain `Set` calls):
   deletion-marked -> `Set(ad.Address, null)`; else
   `Set(ad.Address, new Account(nonce, balance, storageRoot, codeHash))`.
8. `stateTree.UpdateRootHash(canBeParallel: false); return stateTree.RootHash;`
9. NEVER call `Commit` on either tree.

Tests: `Nethermind.State.Test/BalStateRootCalculatorTests.cs`. Store construction:
```csharp
using Nethermind.Core.Test;   // TestTrieStoreFactory
ITrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);
```
(NOT `RawScopedTrieStore` - wrong interface, will not compile against the calculator.)
Skeleton: build a PRE state (accounts + storage) and COMMIT it as fixture setup (fixture
commits are fine; only Lane B production code must not commit); compute the EXPECTED post
root by applying the same changes directly with StateTree/StorageTree; build the equivalent
BAL via the builders; assert `ComputeRoot(preRoot, Reduce(bal)) == expectedRoot`.
Cases:
- T2.1 balance-only change on existing account.
- T2.2 nonce-only change.
- T2.3 new account creation (not in pre-state).
- T2.4 contract creation: code change + storage writes on a fresh account.
- T2.5 storage write on existing contract with existing storage.
- T2.6 write-to-zero deleting the only slot -> storage root returns to empty.
- T2.7 write-to-zero of one of several slots.
- T2.8 account drained to empty (balance->0, nonce 0, no code), NO storage -> leaf deleted.
- T2.9 multi-account block: create + storage change + delete in one delta.
- T2.10 no-persistence guard: wrap the MemDb in a write-recording decorator AFTER fixture
        setup; assert ZERO writes during `ComputeRoot`.
- T2.11 storage slot key > 2^64.
- T2.12 unchanged-storage account keeps `pre.StorageRoot`.
- T2.13 empty-reduced account WITH residual pre-state storage (selfdestruct shape) ->
        leaf DELETED (this is the reviewed EIP-161 rule: storage root must not block
        deletion; the expected tree simply removes the account).
- T2.14 EIP-7702-style code-only change on an account with existing storage: delta has a
        code change (and nothing else) on a pre-state account with slots {A,B}; assert
        slots survive, storage root unchanged, only codeHash differs, and the computed
        root equals the directly-built expected tree (guards against anyone re-adding a
        recreation heuristic that misfires on delegation updates).

Gate G2: all Phase 2 tests green.

## Phase 3 - Shadow-mode integration (config, hook, metrics)

New files (4): in `Nethermind.Consensus/Processing/`:
- `IBalStateRootConfig.cs` AND `BalStateRootConfig.cs` - BOTH are required; configs are
  found by assembly scan and need the concrete class (pattern: `IMiningConfig.cs` +
  `MiningConfig.cs` in the same project). Items:
  `Enabled` (bool, "false"), `UseGpu` (bool, "false"), `GpuMinBatch` (int, "4096").
- `BalStateRootShadow.cs` - owns the calculator; API (NON-BLOCKING by design - the shadow
  must never add latency to block processing):
  - `Task<Hash256?> Start(BlockHeader parent, Block suggestedBlock)` - completed-null task
    when disabled / BAL null / in-flight cap reached (see below); otherwise `Task.Run`
    (NO token - Ground rule 6) wrapping the ENTIRE computation in try/catch: the
    computation is `calculator.ComputeRoot(parent, delta)` (which internally does
    `BeginScope(parent)` - works on halfpath AND flat, section 3). Missing parent state /
    pruning eviction THROW; log Debug, count `BalShadowRootErrors`, return null; after N
    consecutive errors self-disable with a single Warn.
  - `void Compare(Task<Hash256?> lane, Block processedBlock)` - MUST NOT BLOCK. Capture
    `Hash256 expected = processedBlock.Header.StateRoot!` and the block hash as VALUES
    (never the Block reference), then attach `lane.ContinueWith(...)` that compares,
    updates counters, and `IsWarn`-logs mismatches with both roots + block hash; wrap the
    continuation body in try/catch (count as error). No `.Wait()`, no
    `GetAwaiter().GetResult()`, anywhere.
  - Bounded backlog: an `_inFlight` counter (Interlocked), cap 4; `Start` returns the
    completed-null task and increments `BalShadowRootSkipped` when at cap; the
    continuation decrements. Process exit may drop pending comparisons - acceptable for a
    shadow.
- Metrics: add to `Nethermind.Blockchain/Metrics.cs` (Consensus references Blockchain -
  verified): `BalShadowRootMatches`, `BalShadowRootMismatches`, `BalShadowRootErrors`,
  `BalShadowRootSkipped`, `BalShadowRootLastMicros`.

Hook in `BranchProcessor`:
- Add `BalStateRootShadow? balStateRootShadow = null` as the LAST primary-constructor
  parameter (after `preWarmer`, `BranchProcessor.cs:19-27`). No other construction site
  changes - the only production construction is DI (`BlockProcessingModule.cs:64`); tests
  use DI decorators.
- In the per-block loop: skip when the parent header is unavailable (genesis:
  `baseBlock == null` for i=0; for i>0 the parent is the previous processed block's header,
  see `preBlockBaseBlock` at :163). Start the lane BEFORE `ProcessOne`, `Compare` after.
- Register `BalStateRootShadow` in `Nethermind.Init/Modules/BlockProcessingModule.cs`
  (near line 64), constructed from `IWorldStateManager.CreateReadOnlyTrieStore()` +
  `IBalStateRootConfig` + `ILogManager`. Follow di-patterns.md for the registration style.

Startup capability log (task 3.7a): in the `BalStateRootShadow` constructor, emit exactly
ONE Info line summarizing what the parallel state root lane can use on this host, e.g.:
`BAL shadow state root: enabled, hashing: AVX-512F yes, Vector512 accelerated, Vector256 accelerated, physical cores 16, GPU backend not built`
- Sources: `System.Runtime.Intrinsics.X86.Avx512F.IsSupported`,
  `Vector512.IsHardwareAccelerated`, `Vector256.IsHardwareAccelerated`, and the physical
  core count already used by the `ParallelUnbalancedWork` pattern
  (`RuntimeInformation.PhysicalCoreCount` - verify the exact member in
  `Nethermind.Core/RuntimeInformation` and use whatever the repo exposes).
- When the shadow is disabled, log the same line at Info with `disabled` (cheap, one line,
  tells operators the feature exists and what it would use).
- Phase 7 EXTENDS this line (does not add a second one) with the GPU accelerator name and
  memory when `TryCreate` succeeds, or `GPU requested but unavailable (<reason>)` when
  `UseGpu=true` and creation fails (task 7.6).

Tests:
- T3.1 shadow disabled -> completed null task, no work.
- T3.2 block without BAL -> null task.
- T3.3 integration: extend an existing BAL-processing blockchain test (grep test projects
  for `BlockAccessList` usage on `TestBlockchain`-style fixtures and pick the closest;
  per test-infrastructure.md) - process a BAL block with shadow enabled, assert
  Matches == 1, Mismatches == 0.
- T3.4 calculator-level: corrupt one storage post-value in a BAL copy -> root differs.
- T3.5 slow-calculator: inject an artificial delay into the calculator (test seam or fake);
  assert the `Start`/`Compare` path returns without waiting (block processing not
  blocked), and the comparison is still recorded eventually (poll counters with timeout).
- T3.6 scope-required: fake `ITrieStore` whose `Get`-path throws unless `BeginScope` was
  called first; assert `ComputeRoot` succeeds (proves the scope wrapping is present -
  this is the flat-configuration contract).

Gate G3 (milestone): the Amsterdam pyspec suite with shadow ENABLED:
`dotnet test --project src/Nethermind/Ethereum.Blockchain.Pyspec.Test/Ethereum.Blockchain.Pyspec.Test.csproj -c release --filter "FullyQualifiedName~Amsterdam"`
- The pyspec harness builds its DI from `TestNethermindModule` with default config
  (`BlockchainTestBase.cs:130-163`), and the production default is `Enabled=false` - you
  MUST provide a test-only override that registers the shadow with `Enabled=true` for this
  run (config override or module registration in the test setup; find where
  `TestNethermindModule` accepts config and follow that route).
- Environment: fixtures auto-download on first run (network); on this Windows box ensure
  the `CI` env var is UNSET or heavy fixtures skip.
- PASS = all tests green AND `BalShadowRootMismatches == 0` AND `BalShadowRootErrors == 0`
  (no carve-outs: every mismatch is a bug in Phase 1/2 until proven otherwise). Run ONCE
  per milestone (the full set takes on the order of an hour); capture the log.

## Phase 4 - Batch hashing abstraction + per-message backend

New file: `Nethermind.Core/Crypto/IKeccakBatchHasher.cs`

```csharp
/// <summary>Hashes many independent inputs; implementations may vectorize or offload.</summary>
public interface IKeccakBatchHasher
{
    /// <param name="flat">Concatenated inputs.</param>
    /// <param name="offsets">offsets[i] = exclusive end of input i (start = offsets[i-1]; first starts at 0).</param>
    void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs);
}
```
(Convention matches the old GPU kernel exactly - verified against its indexing math - so
the Phase 7 port is mechanical.)

Add `PerMessageKeccakBatchHasher` looping `KeccakHash.ComputeHash` per input. NAMING NOTE:
this is deliberately not called "scalar" - on AVX-512 hardware `ComputeHash` already runs
the horizontal AVX-512 permutation (section 3); this backend is the per-message baseline
that all later backends are measured against.

Tests (`Nethermind.Core.Test/Crypto/KeccakBatchHasherTests.cs`):
- T4.1 randomized differential: 1000 inputs, lengths 0..600, batch == `ValueKeccak.Compute`
  per input. `[TestCaseSource]` over all registered backends (later phases plug in).
- T4.2 boundary lengths: 0, 1, 135, 136, 137, 271, 272, 273 (rate boundaries; the 135
  case exercises the 0x81 combined pad byte).
- T4.3 known-answer: keccak256 of empty input ==
  `c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470` (fast unambiguous
  signal for endianness/marshalling bugs in later backends).

Gate G4: T4.x green with the per-message backend.

## Phase 5 - Wave merkleization (level-ordered batch hashing inside the trie)

Goal: for Lane B only, replace recursive per-node hashing with: collect dirty nodes by
depth -> for depth = deepest..0: flat-encode all nodes at that depth (children already
resolved) -> ONE `HashBatch` per level -> assign keys. This creates the wide batches the
SIMD/GPU backends need.

Location: `Nethermind.Trie/BatchedTrieCommitter.cs` (must be in `Nethermind.Trie`:
`TrieNode.Keccak` setter is internal). Entry:
`static void UpdateRootHashBatched(PatriciaTree tree, IKeccakBatchHasher hasher)`.

Full algorithm and the flat node encoder: Appendix A4 (RLP formats: A3).

Implementation requirements (each verified against the code - see section 3):
1. Traverse from `tree.RootRef` post-order. Descend ONLY via `TryGetDirtyChild`
   (`TrieNode.cs:663-691`) - `GetChild`/`GetChildWithChildPath` can evict child refs
   (`UnresolveChild`) and mutate the tree. Children that are `Hash256` refs or clean nodes
   are leaves of the wave DAG: they contribute their existing keccak, zero work.
   Track only DEPTH (root rule) - RLP does not depend on the path.
2. Per level, bottom-up, write a FLAT encoder - do NOT call `RlpEncodeBranch` /
   `EncodeExtension` / `ResolveKey` (they recurse and re-hash entire subtrees, silently
   destroying the batching). For each child slot of a branch: emit the RLP of
   `child.Keccak` when set; else copy the child's `FullRlp` inline (model:
   `WriteChildrenRlpBranchNonRlp`, `TrieNode.Decoder.cs:369-379`), asserting the child was
   processed at a deeper level. Leaf/extension encode their stored `Key` + value/child.
3. Hashing rule: encoded RLP >= 32 bytes -> into the batch; < 32 bytes -> inline, `Keccak`
   stays null; the ROOT is always hashed regardless (replicate `GenerateKey`,
   `TrieNode.cs:538,559`). Also populate `FullRlp` on encoded nodes the way `GenerateKey`
   does (:553) - parents read inline children's `FullRlp`.
4. Flatten each level into pooled buffers (`ArrayPool<byte>`), one `HashBatch`, then
   assign `node.Keccak`.
5. Levels are strict barriers: all hashing of level d completes (workers joined) before
   level d-1 encoding starts; no worker reads another worker's output within a level.
6. This code runs ONLY on Lane B's cloned nodes (Ground rule 4). Add a doc remark saying
   exactly that and why.

Wire into `BalStateRootCalculator` as an alternate
`ComputeRoot(..., IKeccakBatchHasher hasher)`.

Tests (`Nethermind.Trie.Test/BatchedTrieCommitterTests.cs`):
- T5.1 differential fuzz: 500 random tries (1..3000 keys, values 1..64 bytes including
  many that produce <32-byte leaf RLP), `UpdateRootHashBatched` root == `UpdateRootHash`
  root; seeded RNG, seed printed on failure. This is the correctness authority for every
  encoding subtlety - when in doubt, add the doubtful shape to the fuzz generator.
- T5.2 single-leaf trie; T5.3 deep extension chains; T5.4 branch with all-inline (<32B)
  children; T5.5 empty trie.
- T5.6 all Phase 2 calculator tests re-run through the batched path (`[TestCaseSource]`
  over { recursive, batched-per-message }).

Gate G5: T5.x green; Phase 2 suite green on the batched path.

## Phase 6 - Parallel AVX multi-buffer keccak (REQUIRED)

Baseline honesty (from review): the repo ALREADY has an AVX-512 keccak permutation
(horizontal, single-message - `KeccakF1600Avx512F`, `KeccakHash.std.cs:254`). Phase 6 is
therefore two backends with an experiment gate, not one mandatory kernel:

Kernel structure, constants, and the mixed-length correctness rule: Appendix A5.

6a. REQUIRED - multi-core per-message backend (`ParallelKeccakBatchHasher`):
partition the batch across physical cores with `ParallelUnbalancedWork.For` +
`RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16` (copy the usage shape from
`PersistentStorageProvider.std.cs:50-78`); each worker loops `KeccakHash.ComputeHash`
(which is already AVX-512-per-message on capable hardware). Per-worker slices of the
output span; no shared mutable state; engage only when the batch exceeds a threshold
(start at 256; tune by benchmark). This is the guaranteed, low-risk production CPU backend.

6b. EXPERIMENTAL - vertical multi-buffer kernel (`MultiBufferKeccakBatchHasher`),
adopted ONLY if it beats 6a in the Phase 6 benchmark:
- Layout: N-way vertical - element j of every vector belongs to message j. REGISTER
  PRESSURE IS THE DESIGN CONSTRAINT (reviewed): 25 live `Vector512<ulong>` (1600 B state)
  + theta/chi temporaries exceeds the 32 zmm registers and WILL spill. Choose one:
  (i) 8-way with the state in a `stackalloc`/fixed scratch (25 x Vector512), streaming
  5 lanes at a time through registers per theta/rho-pi/chi plane (the discipline the
  horizontal kernel uses with its 5 vectors); or (ii) 4-way on `Vector256<ulong>` where
  the working set fits more comfortably. Implement (i) first; fall back to (ii) if the
  JitAsm inspection shows the round function dominated by spill traffic. There is NO
  "zero spills" success criterion for the 8-way state - that is unachievable by
  construction; the criterion is measured throughput.
- Rotations: `Avx512F.RotateLeft(vec, constByte)` per lane (verified available -
  `KeccakHash.std.cs:293,305`); AVX2 variant uses shift-pair.
- Grouping: inputs sorted/grouped by block count (`ceil((len+1)/136)`). REVIEWED HAZARD:
  real trie-node lengths span 1-4 blocks within a level (account leaves ~1, branches up
  to 4), so naive grouping produces degenerate <N-wide groups, and small storage tries
  rarely fill a group at all. Two CORRECT strategies are specified in Appendix A5
  (uniform groups with per-message remainder; run-to-max with per-element digest
  snapshots) - implement uniform groups first, benchmark both. WARNING: "padding" short
  messages to more blocks changes the digest - the snapshot rule in A5 is what makes the
  mixed-length variant correct; do not improvise here. Expectation to encode in the
  benchmark: the vertical win concentrates at state-tree top levels and large storage
  tries; per-small-storage-trie batches will mostly take 6a.
- Padding per message during flatten: 0x01/0x80 with the 0x81 coincidence case (T4.2/T4.3
  guard it).

6c. Wire the selected backend chain into `BatchedTrieCommitter` level hashing, and
parallelize ACROSS storage tries in `BalStateRootCalculator` (independent tries,
largest-first by slot count - same `ParallelUnbalancedWork` pattern). Keep the level
barrier (Phase 5 req 5).

Tests: register 6a and 6b into T4.1/T4.2/T4.3 and T5.1 (`Assert.Ignore` when ISA
unsupported). Group-dispatch edges: batch of 9 equal-length messages (one full group + 1),
batch spanning 4 block counts, batch of 7 (all remainder).

Benchmarks: `Nethermind.Benchmark/Core/KeccakBatchBenchmarks.cs` (follow
`Keccak256Benchmarks.cs` conventions): N in {64, 1k, 16k}; length mixes {32B, 136B, 532B,
and a "trie level" mix of 70/110/532B}; backends per-message / 6a / 6b(8-way) / 6b(4-way).
Report the table in the PR description with hardware noted.

Gate G6: differential tests green on every supported ISA; 6a shows near-linear scaling to
physical cores on the 16k mix; 6b is adopted only if it beats 6a on the trie-level mix -
record the decision and numbers either way. Inspect 6b's round function with the JitAsm
tool (tools/JitAsm, local-only) and record spill behavior - measure, don't assume.

## Phase 7 - ILGPU backend

New project: `Nethermind.Crypto.Gpu` (leaf; referenced only by DI wiring and its tests):
- Repo-root `Directory.Packages.props`: add the ILGPU `PackageVersion` (check the current
  ILGPU release and its API surface FIRST - the reference kernel is 1.5.1-era; verify
  `Context`, `GetPreferredDevice`, `LoadAutoGroupedStreamKernel`, `Allocate1D`,
  `CopyFromCPU/CopyToCPU`, and whether a page-locked/pinned host buffer API such as
  `AllocatePageLocked1D` exists in the version you pick).
Kernel pseudocode and host marshalling algorithm: Appendix A6.
- `GpuKeccakBatchHasher : IKeccakBatchHasher, IDisposable`, ported from the reference with
  ALL section-3 flaws fixed - in particular (e): no `new ulong[...]` inside the kernel;
  `RC` as a static/kernel constant; state and temporaries as fixed-size locals; and (f):
  SORT THE GPU BATCH BY BLOCK COUNT before flatten (warp divergence: the per-thread
  permutation-count loop depends on input length; reuse the Phase 6 grouping code).
- Context/accelerator created once in the constructor; persistent device buffers grown
  geometrically; pinned host staging if the API exists. Single-owner blocking dispatch
  (`Synchronize()` then copy-back) is CORRECT for the shadow lane; note in a remark that
  transfer/compute overlap via dedicated streams is a possible follow-up, and do not quote
  GPU throughput without accounting for the serialized transfer.
- `static bool TryCreate(out GpuKeccakBatchHasher?)` - false when no non-CPU device.
- Selection wiring: `UseGpu && TryCreate` -> `ThresholdKeccakBatchHasher` routing batches
  smaller than `GpuMinBatch` to the CPU backend chain, larger to GPU. Default 4096 is a
  deliberate LOWER bound; the measured crossover for short keccak messages is likely
  16k-64k - set the config default to the measured G7 crossover.
- Failure containment: any GPU exception -> Warn once, permanent process-lifetime fallback
  to the CPU chain.

Tests (`Nethermind.Crypto.Gpu.Test`): plug into T4.1/T4.2/T4.3 and T5.1 suites guarded by
`TryCreate` + `Assert.Ignore("no GPU")` (CI has no GPU; runs real on the dev box). T7.1:
100k-message batch (transfer-path stress). The T4.3 known-answer test is the fast guard
for the kernel's output-endianness assumption (state words copied straight into
`ValueHash256` - correct on little-endian hosts, verified equivalent to the scalar path,
but one wrong shift transposes every hash; KAT catches it instantly).

Gate G7: differential green (or skipped) everywhere; on the GPU box, measured crossover
batch size recorded and the config default updated to it.

## Phase 8 - Performance validation and soak

1. Micro: benchmark table (per-message / 6a / 6b / GPU) from Phases 6-7 in the PR
   description, hardware noted.
2. Macro: Amsterdam pyspec suite with shadow + chosen backend on the dev box:
   mismatches == 0, errors == 0; report median and p95 `BalShadowRootLastMicros` vs block
   execution time (Lane B must sit well under execution time), plus the Skipped count
   (in-flight cap pressure).
3. Devnet soak (if a BAL devnet is live): >= 1000 blocks, zero mismatches/errors; capture
   metrics.
4. Write `gpu-bal-results.md` (repo root, uncommitted unless asked): measurements,
   hardware, match statistics, crossover points, recommendation on promoting Lane B.

Gate G8 (done): all captured; zero consensus-path changes anywhere.

---

## Explicit non-goals

- No accept/reject or early-reject wiring of Lane B.
- (removed in v5: flat configurations are SUPPORTED via `BeginScope(parent)` - no adapter
  code needed; see section 3 scope requirement.)
- No modification of `PatriciaTree.UpdateRootHash`, `TrieNode.ResolveKey`, or any Lane A
  commit path.
- No changes to BAL recording/validation code.
- GPU boundary (precise, replacing the earlier absolute wording): ILGPU and all GPU code
  are confined to the new `Nethermind.Crypto.Gpu` leaf project; the ONLY change to an
  existing project is one `ProjectReference` from `Nethermind.Init` (the DI wiring point),
  consistent with how Runner/Init already reference optional components. The ILGPU package
  restores in every build but no GPU code executes unless `UseGpu=true`. No GPU types leak
  into Core/State/Trie/Consensus.

## Order of work and expected sizes

(Size estimates only - execution order and parallelism live in sections 5-6.)

| Phase | New files | ~LOC | Risk |
|---|---|---|---|
| 1 delta reduction | 2 | 250 | low |
| 2 CPU calculator | 2 | 450 | medium (trie semantics; reviewed rules encoded above) |
| 3 shadow hook | 4-5 | 450 | medium (DI + test-config override for G3) |
| 4 batch abstraction | 2 | 150 | low |
| 5 wave merkleization | 2 | 500 | high (flat encoder + <32B rule; T5.1 fuzz is the net) |
| 6 AVX backends | 2-3 | 700 | high (6b kernel); 6a alone is low |
| 7 ILGPU backend | 4 | 500 | medium (port with listed fixes) |
| 8 validation | 0 | - | - |

Phases 1-4 sequential. 5 before 6; 7 needs 4 (benefits from 5/6 grouping code). If context
runs short, STOP at a green gate and report - a half-finished phase is worse than a
missing one.

---

## Appendix A - Algorithms in full

These are normative for the implementing agent. Where a table of constants appears, COPY
the constants from the existing scalar implementation in
`Nethermind.Core/Crypto/KeccakHash.cs` (they are all there) - do not retype from this
document; the inline tables are for cross-checking only.

### A1. Phase 1 - Reduce (complete)

```csharp
public static BalPostStateDelta Reduce(ReadOnlyBlockAccessList bal)
{
    List<AccountDelta> accounts = new();
    foreach (ReadOnlyAccountChanges ac in bal.AccountChanges)
    {
        if (!ac.HasStateChanges) continue;               // read-only entries never dirty the trie

        AssertAscending(ac.BalanceChanges);              // Debug.Assert only; decoder guarantees it
        AssertAscending(ac.NonceChanges);
        AssertAscending(ac.CodeChanges);

        SlotWrite[] storage = new SlotWrite[CountNonEmpty(ac.StorageChanges)];
        int w = 0;
        foreach (ReadOnlySlotChanges sc in ac.StorageChanges)
        {
            if (sc.Changes.Length == 0) continue;        // defensive; decoder rejects empty
            AssertAscending(sc.Changes);
            storage[w++] = new SlotWrite(sc.Key, sc.Changes[^1].Value);
        }

        accounts.Add(new AccountDelta
        {
            Address = ac.Address,
            Balance = ac.BalanceChanges.Length > 0 ? ac.BalanceChanges[^1].Value : null,
            Nonce   = ac.NonceChanges.Length   > 0 ? ac.NonceChanges[^1].Value   : null,
            CodeHash = ac.CodeChanges.Length   > 0 ? ac.CodeChanges[^1].CodeHash : null,
            Storage = storage,
        });
    }
    return new BalPostStateDelta(accounts.ToArray());
}
```

### A2. Phase 2 - ComputeRoot (complete three-pass skeleton)

```csharp
public Hash256 ComputeRoot(BlockHeader parent, BalPostStateDelta delta)
{
    using IDisposable _ = _trieStore.BeginScope(parent);   // no-op on halfpath; REQUIRED on flat (section 3)
    Hash256 parentStateRoot = parent.StateRoot!;
    StateTree stateTree = new(_trieStore.GetTrieStore(null), _logManager); // IScopedTrieStore ctor (sets TrieType.State)

    int n = delta.Accounts.Length;
    Account?[] pre = new Account?[n];
    // PASS A: all pre-state reads BEFORE any mutation. Get with explicit root ignores
    // RootRef; interleaving reads with Sets would observe a partially-updated tree.
    for (int i = 0; i < n; i++)
        pre[i] = stateTree.Get(delta.Accounts[i].Address, parentStateRoot); // THROWS on evicted node - caller catches

    // PASS B: compose accounts; storage roots only for non-empty survivors.
    Account?[] composed = new Account?[n];
    for (int i = 0; i < n; i++)
    {
        AccountDelta ad = delta.Accounts[i];
        Account? p = pre[i];

        ulong nonce = ad.Nonce ?? (p is null ? 0UL : /* convert p.Nonce to ulong per its actual type */);
        UInt256 balance = ad.Balance ?? p?.Balance ?? UInt256.Zero;
        Hash256 codeHash = ad.CodeHash is { } vh ? new Hash256(vh)
                                                 : (p?.CodeHash ?? Keccak.OfAnEmptyString);

        // EIP-161: matches Account.IsEmpty / StateProvider.cs:572 - storage root NOT consulted.
        if (nonce == 0 && balance.IsZero && codeHash == Keccak.OfAnEmptyString)
        {
            composed[i] = null;                          // delete leaf; orphans any storage subtree
            continue;
        }

        Hash256 storageRoot;
        if (ad.Storage.Length == 0)
        {
            storageRoot = p?.StorageRoot ?? PatriciaTree.EmptyTreeHash;
        }
        else
        {
            Hash256 preStorageRoot = p?.StorageRoot ?? PatriciaTree.EmptyTreeHash;
            StorageTree storageTree = new(_trieStore.GetTrieStore(ad.Address), preStorageRoot, _logManager);
            foreach (SlotWrite slot in ad.Storage)
            {
                EvmWord wv = slot.Value;                 // mutable local: ref needs an lvalue
                ReadOnlySpan<byte> value = MemoryMarshal
                    .CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref wv), 32)
                    .WithoutLeadingZeros();
                storageTree.Set(in slot.Slot, value.ToArray()); // all-zero -> [] -> leaf delete
            }
            storageTree.UpdateRootHash(canBeParallel: false);
            storageRoot = storageTree.RootHash;
        }

        composed[i] = new Account(nonce, balance, storageRoot, codeHash);
    }

    // PASS C: writes, then one root computation. Never Commit.
    stateTree.SetRootHash(parentStateRoot, true);
    using (StateTree.StateTreeBulkSetter setter = stateTree.BeginSet(n))
        for (int i = 0; i < n; i++)
            setter.Set(delta.Accounts[i].Address, composed[i]);
    stateTree.UpdateRootHash(canBeParallel: false);
    return stateTree.RootHash;
}
```
(Adapt mechanics to what compiles - e.g. BulkSetter's exact shape - but keep the pass
separation, the deletion rule, and the explicit-root reads exactly as above.)

### A3. MPT node RLP formats (background for Phase 5)

Every trie node encodes as an RLP list; the wave encoder rebuilds these WITHOUT recursion:

- LEAF:      list of 2: [ hexPrefix(Key nibbles, isLeaf=true), value bytes ]
- EXTENSION: list of 2: [ hexPrefix(Key nibbles, isLeaf=false), childRef ]
- BRANCH:    list of 17: [ childRef_0 .. childRef_15, value bytes (empty for state/storage) ]

childRef rule (THE core rule): if the child's encoded RLP is >= 32 bytes, childRef is the
RLP of its keccak (33 bytes: 0xa0 prefix + 32); if < 32 bytes, childRef is the child's raw
RLP bytes spliced inline; an absent child is the RLP empty string (0x80).

Hex-prefix (compact) encoding of a nibble path: use the existing helpers - do not hand-roll:
`HexPrefix.ByteLength(path)`, `HexPrefix.CopyToSpan(path, isLeaf, output)`,
`HexPrefix.ToBytes(path, isLeaf)` (`Nethermind.Trie/HexPrefix.cs:17,19,38`). `node.Key` is
the stored nibble array (`TrieNode.cs:204`).

Verified span-encoding primitives (used by the existing encoder, reuse them):
- `Rlp.LengthOf(ReadOnlySpan<byte>)`, `Rlp.LengthOfSequence(contentLength)`,
  `Rlp.LengthOfKeccakRlp == 33` (`Nethermind.Serialization.Rlp/Rlp.cs:33`).
- `int pos = Rlp.StartSequence(dest, 0, contentLength)` (:537);
  `pos = Rlp.Encode(dest, pos, ReadOnlySpan<byte>)` (:373);
  `pos = Rlp.Encode(dest, pos, Hash256 keccak)` (:400, writes the 33-byte form).
Length-then-encode is a two-pass pattern per node: compute contentLength from parts, size
the buffer with `LengthOfSequence`, then encode. Model code: leaf at
`TrieNode.Decoder.cs:119-132`, extension at :62-92, branch child sizing at :204-212.

### A4. Phase 5 - wave merkleization (complete)

```
UpdateRootHashBatched(tree, hasher):
  if tree.RootRef is null or not dirty: tree.UpdateRootHash(); return   // nothing to batch

  // ---- COLLECT: iterative post-order DFS over DIRTY nodes only ----
  byDepth = list of lists                      // byDepth[d] = dirty nodes at node-depth d
  stack = [(tree.RootRef, depth: 0)]
  while stack not empty:
      (node, d) = pop
      ensure byDepth has index d; byDepth[d].Add(node)
      childCount = node.IsBranch ? 16 : (node.IsExtension ? 1 : 0)
      for i in 0..childCount-1:
          if node.TryGetDirtyChild(i, out child):      // ONLY safe descent primitive
              push (child, d + 1)
      // children that are Hash256 refs / clean nodes / null: wave-DAG leaves, no work

  // ---- HASH: bottom-up level barriers ----
  for d from byDepth.Count-1 down to 0:
      nodes = byDepth[d]
      // pass 1: encode every node into its own FullRlp (children below d are done:
      //         dirty ones have Keccak or FullRlp set; clean ones had them already)
      for node in nodes:                                // parallelizable (6c), join before pass 2
          rlp = FlatEncode(node)                        // A3 rules; NO ResolveKey, NO recursion
          node.SetFullRlp(rlp)                          // same effect as GenerateKey's WriteRlp
      // pass 2: batch-hash the ones that need keys
      toHash = [ node in nodes where node.FullRlp.Length >= 32 or (d == 0) ]
      flatten toHash FullRlps into (flat, offsets) using pooled buffers
      hasher.HashBatch(flat, offsets, outputs)
      for j, node in toHash: node.Keccak = outputs[j]
      // nodes with FullRlp < 32 and d > 0: Keccak stays null (parents splice FullRlp inline)

  tree.RootHash = byDepth[0][0].Keccak (via SetRootHash with resetObjects: false - check
                  how UpdateRootHash publishes the root and mirror it)
```

FlatEncode(node) - per A3:
```
if leaf:      keyBytes = hexPrefix(node.Key, true);  parts = [keyBytes, node.Value]
if extension: keyBytes = hexPrefix(node.Key, false); parts = [keyBytes, ChildRef(child_0)]
if branch:    parts = [ChildRef(child_0) .. ChildRef(child_15), emptyValue]

ChildRef(child):
  null child                -> RLP empty string (0x80)
  child is Hash256 ref      -> Rlp.Encode(dest, pos, hash)            // 33 bytes
  child.Keccak is not null  -> Rlp.Encode(dest, pos, child.Keccak)    // 33 bytes
  else                      -> copy child.FullRlp inline               // < 32 bytes, set at deeper level
                               (assert child.FullRlp is set: it was encoded at depth d+1)
```
CRITICAL: how a branch's raw child slots are read without triggering resolution/mutation -
study `WriteChildrenRlpBranchNonRlp` (`TrieNode.Decoder.cs:369-379`) and use the same
low-level child accessors it uses; never `GetChild`/`GetChildWithChildPath` (they call
`UnresolveChild` and mutate). If `SetFullRlp`/raw-slot access needs an internal helper on
TrieNode, add a minimal `internal` method next to the existing ones rather than reflecting.

Depth note: "depth" = node depth (edges from root), NOT nibble depth; extensions make
nibble depth irrelevant here. The only depth-dependent rule is d == 0 (root always hashed).

### A5. Phase 6 - keccak-f[1600] and the vertical kernel

Scalar structure (all constants live in KeccakHash.cs - copy from there):
```
state: 25 ulong lanes A[x,y], x,y in 0..4; lane index l = x + 5y
absorb one 136-byte block: for l in 0..16: A[l] ^= littleEndianUlong(block, 8*l); permute
after last full block, final partial block of len r (0 <= r < 136):
   XOR message bytes into state bytes [0..r)
   state byte[r]   ^= 0x01              // pad start (coincides with 0x80 when r == 135 -> 0x81)
   state byte[135] ^= 0x80              // pad end
   permute
digest = first 32 bytes of state (lanes 0..3, little-endian)

permute = 24 rounds of:
  theta: C[x] = A[x,0]^A[x,1]^A[x,2]^A[x,3]^A[x,4]
         D[x] = C[(x+4)%5] ^ rotl(C[(x+1)%5], 1)
         A[x,y] ^= D[x]                                  for all x,y
  rho+pi: B[y, (2x+3y)%5] = rotl(A[x,y], RHO[x,y])       for all x,y
  chi:   A[x,y] = B[x,y] ^ ((~B[(x+1)%5,y]) & B[(x+2)%5,y])
  iota:  A[0,0] ^= RC[round]
```
Cross-check tables (copy the authoritative ones from KeccakHash.cs):
RHO offsets r[x][y]: x=0: 0,36,3,41,18; x=1: 1,44,10,45,2; x=2: 62,6,43,15,61;
x=3: 28,55,25,21,56; x=4: 27,20,39,8,14.
RC[0..23]: 0x0000000000000001, 0x0000000000008082, 0x800000000000808A,
0x8000000080008000, 0x000000000000808B, 0x0000000080000001, 0x8000000080008081,
0x8000000000008009, 0x000000000000008A, 0x0000000000000088, 0x0000000080008009,
0x000000008000000A, 0x000000008000808B, 0x800000000000008B, 0x8000000000008089,
0x8000000000008003, 0x8000000000008002, 0x8000000000000080, 0x000000000000800A,
0x800000008000000A, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001,
0x8000000080008008.

Vertical N-way kernel (N=8 on Vector512<ulong>, N=4 on Vector256<ulong>): element j of
every vector belongs to message j; the permutation is the scalar algorithm with every
ulong op replaced by the vector op (XOR/AndNot/`Avx512F.RotateLeft(v, imm)`); NO
cross-element shuffles anywhere.

Absorb transpose: building `state[l] ^= Vector512.Create(m0[l], m1[l], ..., m7[l])` is a
gather/transpose cost - start with the straightforward `Vector512.Create` from 8 scalar
loads per lane (17 lanes per block); optimize the transpose only if the benchmark says so.

MIXED BLOCK COUNTS - CORRECTNESS RULE (this is where a naive implementation silently
produces WRONG HASHES): you cannot "pad" a short message to more blocks - extra
absorb/permute steps change the digest. Two correct strategies; implement (1) first:
1. UNIFORM GROUPS: sort the batch by block count; form N-wide groups ONLY from messages
   with the SAME block count; the < N remainder of each count goes to the per-message
   backend. Simple, always correct, but degenerate for small levels (see 6b hazard note).
2. RUN-TO-MAX WITH SNAPSHOTS: fill a group with mixed block counts, run to the group max;
   after permute p (1-based), for every element j whose blockCount == p, EXTRACT lanes
   0..3 of element j as its digest IMMEDIATELY; elements past their end absorb NOTHING
   (skip their XOR entirely - do not absorb zeros, do not pad again) and their state
   becomes garbage that is never read. Digest-at-own-boundary is what makes this correct.
Strategy (2) buys occupancy at the cost of wasted permute work on finished elements;
benchmark both under the trie-level length mix.

### A6. Phase 7 - GPU kernel and host marshalling

Kernel (per GPU thread = one message; port of the reference with the mandatory fixes):
```
Keccak256Kernel(index, offsets, data, outputs):
  start = index == 0 ? 0 : offsets[index-1]
  end   = offsets[index]
  len   = end - start
  ulong state[25] = {0}                    // fixed-size local, NOT new ulong[25]
  p = start
  while (end - p) >= 136:
      for l in 0..16: state[l] ^= readLittleEndianUlong(data, p + 8*l)
      permute(state)                       // RC as a static/kernel constant; C/D/B as locals
      p += 136
  // final partial block
  rem = end - p
  for i in 0..rem-1: stateByte(state, i) ^= data[p+i]
  stateByte(state, rem) ^= 0x01
  stateByte(state, 135) ^= 0x80
  permute(state)
  outputs[index*4 + 0..3] = state[0..3]
```
Host algorithm:
1. Sort message indices by block count (reuses Phase 6 grouping) - contiguous
   near-uniform lengths minimize warp divergence.
2. Flatten in sorted order into a pinned/pooled staging buffer with running end-offsets
   (the A4/Phase 4 convention).
3. Copy to persistent device buffers (grow geometrically, never shrink), launch,
   `Synchronize()`, copy back, then UNPERMUTE the outputs back to the caller's original
   order (keep the sort permutation array).
4. Wrap the whole call in try/catch -> permanent CPU fallback (Phase 7 containment).

## Review Log (v1 -> v2)

Three independent adversarial passes (consensus semantics; implementability; trie/SIMD/GPU
internals) verified every v1 claim against master. Material corrections folded in above:

1. EIP-161 deletion rule fixed: delete on `Account.IsEmpty` (nonce/balance/code), NOT on
   a 4-condition check including the storage root (`StateProvider.cs:572`, `Account.cs:85`).
   The v1 rule would have false-mismatched drained accounts with residual storage and
   selfdestructed contracts with on-disk storage.
2. Destroy+recreate same-block identified as a genuine BAL-expressiveness gap; handled as
   a flagged known-limitation category with its own metric, not silently wrong.
3. Verified-safe: `Changes[^1]` finality (decoder-enforced ordering/uniqueness; system
   ops at index 0 / txCount+1), full BAL coverage of block state changes, zero-write
   delete parity with the production apply path.
4. `RawScopedTrieStore` test pattern replaced with `TestTrieStoreFactory.Build`
   (`IScopedTrieStore` vs `ITrieStore` compile trap).
5. StorageTree scoping inlined (`GetTrieStore(Address)` extension; ctor-root form of
   `TrieStoreScopeProvider.cs:57`); v1's "grep and copy" pointed at two disagreeing sites.
6. Read-only trie store promoted to a hard ground rule: raw store shares mutable cached
   TrieNodes; the wave hasher writes `Keccak`/`FullRlp` into nodes.
7. Wave encoder must not reuse `RlpEncodeBranch`/`EncodeExtension`/`ResolveKey` (they
   recurse and re-hash); descent must use `TryGetDirtyChild` (others mutate via
   `UnresolveChild`); TreePath threading dropped (depth counter suffices).
8. Phase 6 reframed: the repo already ships a horizontal AVX-512 permutation, so the
   baseline is per-message-AVX-512, 6a (multi-core per-message) is the required backend,
   the vertical kernel is experiment-gated, the impossible "no spills" criterion removed,
   and block-count-grouping degeneracy addressed via pad-to-level-max.
9. GPU port flaw list extended (in-kernel managed allocations, warp-divergence sort);
   endianness guarded by a known-answer test; offsets convention verified matching.
10. Phase 3 wiring made concrete: config interface + concrete class pair, trailing
    optional primary-ctor param on BranchProcessor, registration at
    `BlockProcessingModule.cs:64`, trie store via `CreateReadOnlyTrieStore()`, genesis
    null-parent guard, layering verified (Consensus -> Blockchain/State/Trie all fine).
11. G3 gate made runnable: `FullyQualifiedName~Amsterdam` filter, fixture auto-download,
    CI-unset requirement on Windows, and the required test-only `Enabled=true` override
    (pyspec harness uses default config).
12. Sundry literal-agent traps: EvmWord ref-lvalue conversion, dual `Keccak.OfAnEmptyString`
    constants (`ValueHash256` vs `Hash256`), `StateTree` ctor choice (`TrieType` not set by
    the `ITrieStore` overload), pass-separated pre-reads before any `Set`.

v3 (this revision): Appendix A added with complete algorithms - A1 Reduce implementation,
A2 three-pass ComputeRoot skeleton, A3 MPT node RLP formats + verified span-encoding
primitives (`Rlp.StartSequence/Encode/LengthOf*`, `HexPrefix.*`), A4 wave-merkleization
pseudocode with the flat ChildRef encoder, A5 keccak-f[1600] structure + vertical-kernel
absorb/squeeze + the mixed-block-count correctness rule (the reviewer-suggested
"pad-to-max" is WRONG as literally stated - extra absorb/permutes change the digest; the
correct form is run-to-max with per-element digest snapshots at each message's own final
block, now specified in A5 and referenced from Phase 6), A6 GPU kernel pseudocode + host
marshalling with sort-then-unpermute. Each phase cross-references its appendix section.

v5 (this revision): assessed the external Codex review (`gpu-bal-review.md`) against code;
all four findings confirmed, two with corrections beyond the review:
1. CONFIRMED+UPGRADED - flat read-only trie store requires `BeginScope(baseBlock)`
   (`FlatReadOnlyTrieStore.cs`; interface `ITrieStore.cs:25`; halfpath no-op
   `ReadOnlyTrieStore.cs:30`). Fix adopted is stronger than the review's either/or:
   `ComputeRoot` ALWAYS wraps in `BeginScope(parent)` - portable, and it makes flat
   configurations fully supported (the v2 "self-disable on flat" non-goal is deleted).
   Also noted: flat `HasRoot(stateRoot)` returns true unconditionally - never use it for
   capability detection. New T3.6 guards the contract.
2. CONFIRMED+EXTENDED - the `RecreationSuspected` downgrade was worse than the review
   said: under EIP-6780 (active on all BAL-era forks) a pre-existing contract cannot be
   destroyed, so the destroy+recreate-with-surviving-slots gap is IMPOSSIBLE, while the
   heuristic false-positives on routine EIP-7702 delegation updates. The entire
   mechanism (flag, metric, downgrade) is REMOVED - every mismatch is a mismatch. T2.14
   rewritten as the 7702 code-only-change regression test.
3. CONFIRMED - the "no GPU dependency in existing projects" non-goal contradicted the DI
   wiring. Resolved: accept the single `Nethermind.Init -> Nethermind.Crypto.Gpu`
   ProjectReference (consistent with existing Runner/Init optional-component references);
   non-goal reworded to the precise boundary (ILGPU restores everywhere, executes nowhere
   unless UseGpu=true; no GPU types in Core/State/Trie/Consensus).
4. CONFIRMED - `Compare` blocking semantics were unspecified. Specified as non-blocking:
   value-captured `ContinueWith`, try/catch-counted continuation, bounded in-flight (cap
   4, skip + `BalShadowRootSkipped` metric when saturated), no `.Wait()` anywhere; new
   T3.5 proves block processing is never delayed by a slow calculator.

v4 (this revision): converted to a completable tracking plan - section 4 TDD protocol
(stub -> failing tests -> red check -> green -> evidence-backed gate; C# nuance: stubs
throw NotImplementedException so tests compile and fail as assertions, never as compile
errors), section 5 dependency graph with parallel tracks (Track A consensus core =
single owner; P4 unblocks three independent hashing tracks; the block-count grouping
utility is owned by task 6.3 and consumed by P7; single-agent order runs G3 earliest for
the consensus signal), section 6 checkbox tracker with ~60 tasks/subtasks pointing into
the phase specs. Phase sections demoted to specs; the tracker is the working document.
