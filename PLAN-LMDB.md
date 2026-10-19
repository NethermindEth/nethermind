# Experiment: LMDB-backed FlatDb Account/Storage columns

## Context

This is the second iteration of the bitcask-model state-backend experiment. The first iteration
(this repo, branch `experiment/bitcask`, see `PLAN.md`/`RESULTS.md`) implemented a BTDB-backed
(Bitcask-inspired) `IDb`/`IColumnsDb<FlatDbColumns>` for FlatDb's `Account`/`Storage` columns. It was
a real, working implementation — 40+ passing unit tests, six genuine BTDB-side concurrency/correctness
bugs found and fixed against live Sepolia snap-sync, and one unrelated critical native-RocksDB
stack-buffer-overrun bug found and mitigated along the way (worth its own upstream issue). But the
central measurement it set out to make — RAM cost at real Ethereum scale — came back unfavorable:
BTDB's Bitcask keydir model requires its **entire key index to be RAM-resident** (measured ~62
bytes/key: 20–52-byte key + 12-byte value pointer, since real account/storage values are all above
BTDB's 7-byte inline threshold and so land on disk with only a pointer kept in memory). Extrapolated,
a full Sepolia sync alone would need on the order of ~69GB of RAM for the index; mainnet (roughly an
order of magnitude more keys) would be worse. Not viable on typical node hardware.

The structural problem is that a classic Bitcask keydir — and BTDB's implementation of one — has a
**hard RAM ceiling that scales linearly with key count**, with no degradation path: once the index no
longer fits, there is no "partially resident, rest on disk" mode; it simply doesn't fit. What's needed
instead is a store that *prefers* to keep as much as available RAM allows resident (for speed) but
degrades gracefully — extra disk reads on a miss — rather than having a hard requirement. FASTER/
Tsavorite was considered and ruled out: it was evaluated once before (a small, inconclusive 2024 spike
in a separate `FasterKVTests` scratch project) and, more decisively, the standalone
`Microsoft.FASTER.Core` package is stale (last published 2024-05-07) with Microsoft steering users to
**Tsavorite** (Garnet's evolved fork) — which has no standalone NuGet package (bundled only inside the
full `Microsoft.Garnet` server package, or vendored from the Garnet monorepo). Tsavorite's hash index
is also fully RAM-resident anyway (smaller per-key constant than BTDB's, but the same structural
ceiling, just further out).

**LMDB is architecturally the right fit, and it is not a variant of RocksDB.** RocksDB is Facebook's
fork of Google's LevelDB — an LSM-tree store (memtable + WAL, background compaction across sorted
levels). LMDB is an independent design (Howard Chu/Symas, originally replacing Berkeley DB in
OpenLDAP) — a copy-on-write B+tree over a memory-mapped file, no compaction, no WAL. Critically, LMDB
has **no separate resident index structure at all**: B+tree pages live in the mmap'd file, and the OS
page cache — not the library — decides what stays resident, based on actual access patterns. Given
ample RAM (128–256GB), the hot working set naturally stays cached; cold, rarely-touched keys just cost
a page fault on miss. This removes the hard wall BTDB hit, rather than just pushing it further out.
The chosen binding is **LightningDB** (`CoreyKaylor/Lightning.NET`) — actively maintained (latest
`0.23.0`, 2026-08-08), targets net8.0+ (net10 fine), published on NuGet (normal CPM dependency).

This remains a learning/benchmarking spike, not a mergeable production backend.

## Location and branch strategy

This file is committed on `experiment/bitcask` (alongside the existing `PLAN.md`/`RESULTS.md`) so it
travels with the repo. **Start implementation on a new branch cut from `experiment/bitcask`** (not
`master`) — this inherits the one genuine, unrelated bug fix already found and verified there (the
RocksDB `Get(key, Span<byte>)` C-style-fast-path stack-buffer-overrun in `BasePersistence.cs`/
`HistoryReader.cs`, stack buffers widened 256→1024 bytes, plus the `StateUnavailableException`
`RlpException`-wrapping fix), which applies to any FlatDb backend regardless of which columns are
BTDB- or LMDB- or RocksDB-backed. Name it `experiment/lmdb`.

**Keep the existing BTDB plugin in place** rather than removing it — it's a working, already-measured
baseline, and having both plugins available makes a real three-way benchmark (RocksDB / BTDB / LMDB)
possible instead of only having BTDB's numbers to compare against from memory. Both plugins default to
`Enabled: false` and both, if enabled, would independently decorate `IDbFactory` to wrap the same
FlatDb `Account`/`Storage` columns — add an explicit startup guard (in whichever plugin's module loads
second, or a small check in `BtdbDbFactoryDecorator`/`LmdbDbFactoryDecorator`) that throws a clear
config error if both `Btdb.Enabled` and `Lmdb.Enabled` are true at once, rather than silently
double-wrapping.

## Scope (unchanged from the BTDB experiment)

Same as `PLAN.md`'s original scoping, still valid:
- Swap only FlatDb's `Account` and `Storage` columns (`FlatDbColumns`). Trie-node columns
  (`StateTopNodes`/`StateNodes`/`StorageNodes`/`FallbackNodes`) and `Metadata` stay on RocksDB, as do
  all other named databases.
- Implement range support (`GetViewBetween`/`ISortedKeyValueStore`) so `TrieReassembler` and
  `SnapFlatStateServer` keep working — LMDB's B+tree makes this a natural fit via cursor
  seek/next/prev, arguably simpler than BTDB's cursor-based equivalent.
- Validate on a bounded dataset (devnet/testnet) before ever considering mainnet scale — same
  reasoning as before, though the *measurement question changes* (see "Validation methodology" below).

## Dependency

- Add `<PackageVersion Include="LightningDB" Version="0.23.0" />` to `Directory.Packages.props`
  (verify latest at implementation time — check for anything newer than 0.23.0, published
  2026-08-08).
- New project `src/Nethermind/Nethermind.Db.Lmdb/Nethermind.Db.Lmdb.csproj`, mirroring
  `Nethermind.Db.Btdb.csproj`'s shape (references `LightningDB`, `Nethermind.Api`, `Nethermind.Db`).
  Add to `Nethermind.slnx` under a new `/Plugins/Lmdb/` folder, alongside the existing `/Plugins/Btdb/`.

## Spike findings (2026-09-28, LightningDB 0.23.0, net10.0)

A throwaway console spike (not committed — built against the installed `LightningDB` 0.23.0
package under `net10.0`) empirically verified the semantics the "LMDB-specific, needing
verification" list below called out, rather than trusting documentation excerpts. Two results
contradict what the package's own shipped XML doc comments claim; the rest confirm the plan's
assumptions. All six are settled enough to design against now:

1. **Write-transaction acquire semantics: blocks, does not throw.** With one write transaction
   held open on thread A, `env.BeginTransaction()` (no `ReadOnly` flag) called from thread B
   blocked for the full duration thread A's transaction stayed open (measured: thread A held its
   transaction 500ms, thread B's call returned at the 501ms mark) and then succeeded — no
   busy-error, no exception, no polling needed. This mirrors BTDB's `StartWritingTransaction`
   (the *correct* one of BTDB's two options, per BTDB bug #2) — the group-commit coordinator's
   `DrainIfLeader` can call `BeginTransaction()` directly and rely on the runtime to serialize
   writers; no retry-on-busy loop is needed.

2. **Cross-thread transaction/cursor safety is real but not caught by an exception —
   `NoThreadLocalStorage` matters for the *pool handoff*, not for whether it throws.** Calling
   `.Get()` on the *same* read transaction object from a thread other than the one that created it
   did not throw, with or without `EnvironmentOpenFlags.NoThreadLocalStorage` — contradicting the
   shipped doc's warning ("a transaction must only be used by a single thread"). This is **not**
   evidence that it's safe: it only proves the library doesn't guard against it with an exception,
   which is exactly the shape of BTDB bug #7 (silent corruption under concurrent multi-threaded
   cursor use, not a thrown error). Do not read anything into the absence of an exception here.
   What *is* confirmed as the right pattern: two **independently-opened** read transactions, each
   used from its own thread concurrently, worked cleanly. This validates the plan's design
   as-is — a pool of independently-opened read transactions (not one shared instance handed
   across threads) — and `NoThreadLocalStorage` should still be set on the column's environment,
   because the pool's rent/return pattern means a given pooled transaction's *owning* thread can
   legitimately change between rentals, which the default TLS-slot-per-thread binding is not
   designed for even though this narrow test didn't surface an immediate failure from it.

3. **Per-transaction `NoSync`/`NoMetaSync` — the plan's environment-wide-only assumption was
   wrong; LightningDB 0.23.0 supports a genuine per-commit durability toggle.** The shipped XML
   doc states plainly: "As of LMDB 1.0 this flag is honored per-transaction when passed to
   BeginTransaction" — and empirically, `env.BeginTransaction(TransactionBeginFlags.NoSync)` and
   `env.BeginTransaction(TransactionBeginFlags.None)` both succeeded against the same
   environment (opened either with or without the env-wide `NoSync` flag), each committing fine.
   This directly mirrors BTDB's per-commit durability knob (bug #3's fix,
   `NextCommitTemporaryCloseTransactionLog`) — **no manual-sync-after-the-fact strategy is
   needed**. Revised design: open the environment *without* the env-wide `NoSync` flag (safe
   default), and have `DrainIfLeader` pass `TransactionBeginFlags.NoSync` on the physical write
   transaction for any drained wave where *none* of the coalesced writes requested durability, or
   `TransactionBeginFlags.None` when at least one does — the same per-wave decision BTDB made,
   just via a begin-flag instead of a pre/post-commit call. `LightningEnvironment.Flush(force:
   bool)` (maps to `mdb_env_sync`) is also confirmed present and callable on demand, for a manual
   sync point if one is ever needed outside the per-transaction mechanism.

4. **`MapSize` resize: the shipped doc comment ("may only be called before the environment is
   opened") is wrong; the real constraint is LMDB's native one (no active write transaction).**
   Empirically: setting `env.MapSize` after `Open()` succeeded with no transactions active, and
   *also* succeeded with an active **read** transaction open. It threw
   `LightningException: "Invalid argument"` only when a **write** transaction was active at the
   time — a clean, catchable native rejection, not corruption. Data written before and after a
   resize both read back correctly afterward. This confirms the plan's proposed design point
   exactly: perform the resize inside the group-commit coordinator's leader-exclusive section
   (`DrainIfLeader`, where no write transaction can be active by construction) and it's safe to do
   so even in the presence of concurrent readers from the snapshot pool. (`LightningDB.Native.Lmdb`
   is a public type exposing `mdb_env_set_mapsize` directly too, but the managed `MapSize` property
   already does the right thing — no need to drop to the native P/Invoke.)

5. **Delete API confirmed**: `LightningTransaction.Delete(LightningDatabase, byte[])` and the
   `ReadOnlySpan<byte>` overload both exist, return `MDBResultCode.Success` on a real delete, and a
   subsequent `Get` correctly returns `MDBResultCode.NotFound`.

6. **Cursor navigation confirmed, with one signature gotcha**: `First()`/`Next()`/`Previous()`/
   `Last()` all return `(MDBResultCode, MDBValue key, MDBValue value)` tuples directly. The
   seek-to-greater-or-equal primitive `GetViewBetween`'s lower bound needs is
   `LightningCursor.SetRange(key)` — but unlike the others, **it returns only an `MDBResultCode`**,
   not a tuple; the key/value the cursor landed on must be read back with a follow-up
   `cursor.GetCurrent()` call. Verified this lands on the correct next key and returns
   `NotFound` cleanly when seeking past the end of the keyspace.

Not spiked (deliberately out of scope for this pass, needs real snap-sync write volume to mean
anything): `WriteMap` mode's actual corruption-blast-radius/performance tradeoff under load — still
treat with the suspicion the plan describes; a lightweight spike wouldn't have caught BTDB's
equivalent issue (bug #5) either.

## Architecture — reuse the proven BTDB shapes, adapted to LMDB's API and semantics

The class structure that worked for BTDB carries over almost unchanged in shape (`LmdbDb`,
`LmdbColumnsDbAdapter`, `LmdbDbFactoryDecorator`, `LmdbPlugin`/`ILmdbConfig`/`LmdbConfig`) — same
plugin-only, zero-core-files-touched approach via `IDbFactory` decoration
(`builder.AddDecorator<IDbFactory, LmdbDbFactoryDecorator>()`). The differences that matter are in the
concurrency/durability model, detailed below — carrying forward what the BTDB experiment learned the
hard way, rather than rediscovering each one via a live-sync crash again.

### `LmdbDb` — `IDb` implementation

One `LightningEnvironment` per column (`<flatDbPath>/account.lmdb/`, `<flatDbPath>/storage.lmdb/`),
each with one `LightningDatabase` opened inside it.

**Reused unchanged from the BTDB experience (general lessons, not BTDB-specific):**
- **`CreateSnapshot()` must be real from day one**, not stubbed — `RocksDbPersistence.CreateReader`/
  `CreateWriteBatch` call it on every read/write-batch open, including at startup via
  `FlatStateBoundary`. This is *simpler* for LMDB than it was for BTDB: an LMDB read transaction
  (`env.BeginTransaction(TransactionBeginFlags.ReadOnly)`) is, by construction, already a stable
  point-in-time MVCC view of the last-committed state as of when it began — no extra machinery needed
  for a *single* consistent view.
- **A pool of independently-opened read transactions, not one shared transaction, for
  `CreateSnapshot()`'s multi-call/multi-thread lifetime.** BTDB bug #7 (`BtdbDbSnapshot` sharing one
  transaction across `ReadOnlySnapshotBundle`'s concurrent worker threads corrupted BTDB's
  unsynchronized per-transaction cursor list) is very likely a real risk for LMDB too — a read
  transaction and its cursors are not documented as safe for concurrent multi-threaded use in the
  underlying LMDB C library either. Design this in from the start: `CreateSnapshot()` opens a pool of
  `Environment.ProcessorCount` read transactions, all opened back-to-back under the same
  write-commit-serialization point (so they all observe the identical committed version — mirrors
  `BtdbDb._writeCommitGate`), with a rent/return pattern per accessor call (`Get`) or per view lifetime
  (`GetViewBetween`). This should be built this way *from the first version*, not discovered via a
  restart crash on a live node the way it was for BTDB.
- **A group-commit coordinator for the single-writer constraint.** LMDB, like BTDB, allows only one
  write transaction per environment at a time. Nethermind's snap-sync dispatcher writes from many
  concurrent tasks; giving each its own physical transaction reproduces BTDB bug #4 (~92% of wall-clock
  time blocked waiting for a turn) regardless of which underlying store enforces the single-writer
  rule. Port `SubmitWrite`/`DrainIfLeader` (queue pending writes, one leader drains everything queued
  into one physical transaction per wave, signals every coalesced caller via `ManualResetEventSlim` +
  `ExceptionDispatchInfo`, not `TaskCompletionSource` — see BTDB bug #6 for why) essentially unchanged;
  this logic isn't BTDB-specific.
- **Do not implement the C-style unsafe `Get(key, Span<byte> output)` fast-path override** unless
  there's a measured, specific need — rely on the interface's default (`GetSpan` + `CopyTo`), which
  bounds-checks safely. The one crash in the BTDB experiment that wasn't BTDB's fault came from
  exactly this unsafe fast path on the *RocksDB* side (`DbOnTheRocks.GetCStyleWithColumnFamily`,
  no bounds check against the destination span) — a reminder not to introduce the same class of bug
  in a new backend.

**LMDB-specific, needing verification during an early implementation spike (do this before wiring
anything into `IDb` — the BTDB experiment's costliest bugs came from unverified assumptions about the
library's exact transaction-acquire semantics):**
- **Write-transaction acquire semantics**: confirm whether `LightningEnvironment.BeginTransaction()`
  (no `ReadOnly` flag) blocks/queues when another write transaction is already active, or throws
  immediately (BTDB had both an immediately-throwing API and a properly-queuing one —
  `StartTransaction` vs. `StartWritingTransaction` — and picking the wrong one was bug #2). Real LMDB's
  C semantics use a mutex/lockfile for the single writer and block; verify Lightning.NET's managed
  wrapper preserves that (queues) rather than surfacing a busy-error.
- **Durability / `WriteFlags.DisableWAL` mapping**: BTDB's fix (bug #3) forced a full fsync-equivalent
  per physical commit only when at least one coalesced write did *not* request `DisableWAL`, via a
  per-transaction call (`NextCommitTemporaryCloseTransactionLog`). LMDB's `NoSync`/`NoMetaSync` are
  documented as **environment-wide** open flags, not obviously a per-transaction/per-commit toggle —
  confirm whether LightningDB exposes an equivalent to LMDB C's `mdb_env_sync(force)` (explicit,
  on-demand sync independent of the environment's automatic-sync setting). If so, the mirror strategy
  is: open the environment with `NoSync` (fast, non-durable per-commit) and call the explicit sync
  after any drained wave that contained at least one durability-wanting write — same shape as BTDB's
  fix, adapted to an environment-wide-flag-plus-manual-sync model instead of BTDB's per-commit knob.
- **`WriteMap` mode**: treat with the same suspicion the BTDB experiment learned to apply to
  `inBatch: true` (bug #5 — a native `AccessViolationException` from an unverified "reuse predecessor
  state" performance hint used under real write volume without understanding its exact repeated-call
  contract). `WriteMap` maps the database file directly as writable, which can be faster but changes
  the corruption blast radius of any application bug (a stray write can corrupt the file directly
  rather than being caught by the OS's copy-on-write protection of a read-only mapping). Do not enable
  it without first understanding the exact risk tradeoff, and test under real snap-sync write volume
  before trusting it — not just lightweight unit tests, which never surfaced BTDB's equivalent issue.
- **`MapSize` sizing and growth**: LMDB requires an upfront maximum-size reservation
  (`EnvironmentConfiguration.MapSize`), and growing it later (`LightningEnvironment.MapSize` setter →
  `mdb_env_set_mapsize`) has real constraints (LMDB's own docs: only safe with no active transactions
  in the process; the comment `Lightning.NET`'s own XML doc gives — "may only be called before the
  environment is opened" — needs to be reconciled against the actual runtime behavior of calling the
  setter later, since the underlying native call is real either way). Start with a generously
  overprovisioned `MapSize` for the devnet-scale validation runs (sparse files mean this doesn't cost
  real disk until pages are actually written), and design a resize path that coordinates with the
  group-commit coordinator's exclusive-writer gate (resize must happen with no transactions active —
  the natural place is inside `DrainIfLeader`'s leader-exclusive section, similar to how
  `_writeCommitGate` already serializes commits against snapshot-pool creation).
- **Delete API and cursor navigation method names**: confirm exact signatures against the installed
  `LightningDB 0.23.0` package (`LightningTransaction.Delete(...)`, cursor `First`/`Last`/`Next`/
  `Previous`/seek-to-greater-or-equal) in the spike rather than assuming from documentation excerpts,
  which were incomplete during planning research.

### `LmdbColumnsDbAdapter` / `LmdbDbFactoryDecorator` / `LmdbPlugin`/`ILmdbConfig`

Structurally identical to `BtdbColumnsDbAdapter`/`BtdbDbFactoryDecorator`/`BtdbPlugin`/`IBtdbConfig` —
same column-routing logic (`Account`/`Storage` → LMDB, everything else → delegate to the inner
RocksDB-backed columns DB unchanged), same case-insensitive `DbNames.Flat` comparison (the DSL
title-cases `dbSettings.DbName` before it reaches the factory — this tripped nothing in the BTDB
experiment specifically because it was handled correctly from the start; keep doing so), same
periodic key-count/size diagnostic logging pattern (adapt `BtdbDb.NativeMemoryBytes` — LMDB has no
equivalent concept, since there's no separate off-heap index allocator to query; see "Validation
methodology" below for what replaces it).

## Known issues (found after implementation, not during the spike)

**`MDB_READERS_FULL` (`MDBResultCode.ReadersFull`, -30790) is process-fatal in LightningDB 0.23.0, not a
recoverable error — sizing `MaxReaders` generously is the only real defense.** Reported in practice as
`Error when handling response LightningDB -30790: MDB_READERS_FULL: Environment maxreaders limit reached`.
Root cause: `CreateSnapshot()` opens a whole pool of `Environment.ProcessorCount` read-only transactions per
snapshot and holds them for the snapshot's entire lifetime (by design — see `LmdbDbSnapshot`'s remarks) —
LMDB's own default reader-lock table is only 126 slots, which a handful of concurrently-alive snapshots on a
many-core machine exhausts easily. The fix that matters is `ILmdbConfig.MaxReaders`, now defaulted to 8192
(previously unconfigurable, implicitly 126).

What made this worse than a normal bug to track down: **a *failed* `LightningEnvironment.BeginTransaction`
call leaks an internal transaction handle that the library itself still tracks against that environment —
one the caller never receives a reference to and so can never dispose.** That handle is only discovered
later, when the GC gets around to finalizing it, at which point it throws `InvalidOperationException: "A
transaction must be disposed before closing the environment"` **from inside the finalizer**, which is fatal
to the whole process (unhandled finalizer exceptions terminate .NET processes by design). Verified
empirically (throwaway spike, not committed) with `MaxReaders` deliberately set too low:
- The crash reproduces even when every failed `BeginTransaction` call is caught and handled correctly.
- It reproduces even when a **subsequent retry on the same call succeeds** (e.g. after freeing capacity) —
  the damage from the one failed attempt is already permanent by then.
- It is not tied to any specific call site — it was first found via a *test* (`CreateSnapshot_throws_a_clear_
  MDB_READERS_FULL_error_when_the_reader_table_is_genuinely_undersized`, written to confirm the exhaustion
  path failed cleanly) that itself reliably crashed the test process, and was only removed once its actual
  purpose (validating the `MaxReaders` fix) could be served by a test that never trips `ReadersFull` at all
  (`CreateSnapshot_supports_many_concurrent_snapshots_without_exhausting_the_reader_table`).

Consequence for the design: an earlier version of `LmdbDb.BeginReadOnlyTransaction` caught `ReadersFull` and
retried once via `LightningEnvironment.CheckStaleReaders()` (a real, documented recovery mechanism for
slots stuck because their owning thread died). That retry is not wrong on its own, but it cannot prevent the
process-fatal outcome above — at best it avoids surfacing an exception to *this* caller while leaving the
process just as compromised, which is arguably worse: the eventual crash then has no visible connection to
its real cause. Removed in favor of a plain `BeginTransaction` call with a loud doc comment: if this ever
throws in practice, the fix is raising `MaxReaders`, not handling the exception better.

Two smaller, related leaks were fixed alongside this (both are cases where a *real*, valid transaction —
not the phantom kind above — was left undisposed on an exception path): `CreateSnapshot()` failing partway
through its pool loop now disposes whatever it already opened before rethrowing, and `GetViewBetween`
(both the top-level and the pooled-snapshot version) now disposes/returns its transaction if creating the
cursor fails partway through.

This is arguably worth reporting upstream to `CoreyKaylor/Lightning.NET` — a failed `BeginTransaction` should
not leave unmanaged/tracked state that only a later GC can (fatally) discover.

**Rejected fix: `ReaderWriterLockSlim` for `_writeCommitGate` made concurrent `CreateSnapshot` *slower*, not
faster — reverted after benchmarking, kept here so it isn't tried again without re-measuring.** Reported
symptom: a real snap-sync run felt "quite slow." Hypothesis: `_writeCommitGate` is one plain `Lock` shared by
`CreateSnapshot`'s pool-opening loop and `DrainIfLeader`'s commit, so concurrent `CreateSnapshot` calls (snap
sync issues many, each bypassing `CachedReaderPersistence`'s cache via `ReaderFlags.Sync`) fully serialize
against *each other*, not just against commits - even though read-only LMDB transactions never need to block
one another. Fix tried: `ReaderWriterLockSlim`, with `CreateSnapshot` taking a shared read lock and
commit/map-growth taking the exclusive write lock. Benchmarked directly against the real `LmdbDb` class
(throwaway console benchmark, not committed; 200k `CreateSnapshot`+`Dispose` calls, serial vs. 2/4/8/16
concurrent threads): **the original plain `Lock` scaled flat (~350k calls/sec regardless of thread count,
1.0-1.1x "speedup" at every thread count - i.e. no degradation), while the `ReaderWriterLockSlim` version
degraded steadily with more threads (350k → 143k calls/sec at 16 threads, dropping to 0.4x of serial
throughput)**. `ReaderWriterLockSlim`'s per-acquisition bookkeeping overhead is worse than a plain mutex when
critical sections are this short (a handful of `BeginTransaction` calls) and there's no real writer contention
to justify the extra complexity. Reverted to the plain `Lock`, matching BTDB's original design. The
lock-granularity theory for the reported slowness is therefore disproven, at least in this synthetic,
single-machine, tiny-dataset benchmark - real production Account/Storage data with deep B+tree pages and
actual disk I/O could behave differently, but that needs live profiling to establish, not more speculative
locking changes. Added lightweight built-in diagnostics instead (`LmdbDb.TakeAndResetDiagnostics`, logged
every ~30s by `LmdbColumnsDbAdapter` alongside the existing key-count line) - call counts and average
latencies for `Get`, `CreateSnapshot`, and write-wave commits, specifically so a future "quite slow" report
comes with real numbers instead of requiring a fresh round of guessing.

**Follow-up from a second live snap-sync check-in, and the lock-granularity theory re-tested and re-rejected
under a *mixed* read+write workload.** A later diagnostics sample (same live run, further along) showed the
Account/Storage `CreateSnapshot` latency gap narrowing (231.5→522.6us vs 1244.5→587.5us) while write-wave
counts grew asymmetrically (Account 1→16, Storage 7,597→12,406) - crucially, Account's *now-16-sample* average
write-wave latency held steady at ~200ms (160383.3→202386.8us), ruling out the earlier single sample as a
fluke. Two hypotheses were checked and rejected before landing on the real one:
1. *Lock granularity, re-tested with concurrent writes this time.* The earlier `ReaderWriterLockSlim` rejection
   only benchmarked pure concurrent reads (no writes in flight). Re-benchmarked with a mixed workload (12
   reader threads calling `CreateSnapshot`+`Dispose`, 4 writer threads calling `Set`, roughly matching the
   observed Storage-column read:write ratio) against the real `LmdbDb` class: the plain `Lock` again beat
   `ReaderWriterLockSlim` outright - 92,612 `CreateSnapshot`/sec and 2,886 writes/sec vs. 27,217/sec and
   1,041/sec respectively. Confirms the first benchmark's conclusion held even under realistic contention;
   reverted again (working tree returned to the exact prior commit).
2. *Durability difference (Account fsync'd, Storage not).* Traced every `WriteFlags.DisableWAL` call site
   across `Nethermind.State.Flat` (an Explore-agent pass, then confirmed directly): `FlatSnapStateTree.Commit`
   (`Sync/Snap/FlatSnapStateTree.cs`) and `FlatSnapStorageTree.Commit` (`Sync/Snap/FlatSnapStorageTree.cs`)
   both pass `WriteFlags.DisableWAL` identically - normal chain-following persistence
   (`PersistenceManager.PersistSnapshot`/`PersistPersistedSnapshot`) also treats both columns identically
   (neither sets `DisableWAL`, both default to `WriteFlags.None` via one shared `RocksDbPersistence.CreateWriteBatch`
   call). Rejected: both columns hit the same `TransactionBeginFlags` branch in
   `CommitWaveWithMapGrowthRetry` during snap sync.
3. **Leading hypothesis, not yet confirmed: wave size, not durability or locking.** Account has ~775x fewer
   write waves than Storage in the same window (16 vs 12,406) - if snap sync coalesces vastly more individual
   key writes into each of those rare Account waves (there are far fewer total accounts than storage slots in
   real Ethereum state, so account-range sync responses plausibly batch more before a wave's `IWriteBatch` is
   disposed and flushed), then ~200ms for one large B+tree-mutating commit vs ~2.3ms for many small ones would
   be proportional real work, not a bug. Added `AvgWriteWaveOps` to `LmdbDb.DiagnosticsSnapshot` (average
   operations coalesced per physical write-wave commit) specifically to confirm or kill this on the next
   check-in - a large ops/wave figure for Account and a small one for Storage would confirm it directly.

**A separate, more concrete symptom surfaced independently of the ops/wave check-in: consensus-layer Engine API
timeouts (`newPayload`/`forkchoiceUpdated`) and a visible "runs fine, stalls, resumes" pattern during live sync.**
This matters regardless of which of the above throughput theories is ultimately correct, because of a real
correctness requirement that was already documented but whose consequence hadn't been drawn out: `_writeCommitGate`
must be held for a commit's *entire* duration (not just acquired and released quickly), and every concurrent
`CreateSnapshot` call needs that same lock to build its reader pool safely - so **one slow or arbitrarily large
physical commit blocks every concurrent reader across the whole column, not just other writers**, for as long as
that commit takes. `DrainIfLeader`'s wave-building had no size limit - it coalesced everything currently queued
into one physical transaction, so a write backlog could produce an arbitrarily large, arbitrarily slow single
commit with no cap. Since typical CL implementations time out `newPayload` around 8s, and the node was observed
running "in two speeds" (matching periods where everything - not just the write path - would stall for a slow
commit's duration), this is a plausible direct explanation independent of root-causing the Account/Storage
ops/wave asymmetry above.

**Fix**: added `ILmdbConfig.MaxOperationsPerWave` (default 4096), enforced in `DrainIfLeader`'s wave-building
loop - it stops coalescing *additional, separately-queued* `PendingWrite`s into the current wave once the
running operation count reaches the cap. Deliberately scoped narrowly: it does **not** split a single caller's
own write batch (e.g. one `IWriteBatch` accumulated across many `PutSpan` calls before one `Dispose()`) across
multiple physical commits, since doing so would sacrifice that batch's current all-or-nothing atomicity - a
call whose own batch already exceeds the cap still commits as one uncapped physical transaction. This means the
fix only helps the "many small writes coalesced together" case, not the "one write batch is itself huge" case -
if the ops/wave diagnostic confirms Account's slow waves are each essentially one already-huge write batch (not
many small ones getting coalesced), this cap alone won't reduce Account's per-commit latency, and a further,
more invasive change (splitting a single batch across commits, accepting a partial-atomicity trade-off that
snap-synced data's re-fetchability may make tolerable - similar reasoning to why `DisableWAL` is already used)
would be the next thing to consider, informed by that data rather than guessed at up front. Two new tests
(`MaxOperationsPerWave_splits_many_separate_write_batches_across_multiple_physical_commits`,
`MaxOperationsPerWave_does_not_split_a_single_callers_own_write_batch`) pin down exactly this scope boundary.

**Confirmed on the next check-in: it's the "one write batch is itself huge" case, not coalescing.** A fresh
sync run (same live network, restarted with the `MaxOperationsPerWave` build) showed Account's `newPayload`
timeouts still recurring, and diagnostics settled the open question directly: `Account: ... WriteWave x8 (avg
157274.3us, avg 42206 ops/wave) ... Storage: ... WriteWave x8,663 (avg 3200.0us, avg 342 ops/wave)`. 42,206
ops/wave is ~10x the 4096 cap - confirming each slow Account wave is one single write batch (matching
`FlatSnapStateTree.Commit`'s pattern of accumulating many thousands of account entries into one `IWriteBatch`
before a single `Dispose()`), not many small ones getting coalesced. The cap as first scoped genuinely could
not help this case, exactly as flagged above.

**Follow-up fix: `CommitLargeWriteInChunks` now splits a single oversized write batch into sequential physical
commits**, accepting the partial-atomicity trade-off that was deliberately deferred until confirmed necessary.
`DrainIfLeader` routes any dequeued `PendingWrite` whose own operation count exceeds `MaxOperationsPerWave` to
this new path instead of the coalescing path (an oversized item encountered *while* coalescing others is
deferred and handled the same way immediately after, preserving overall queue order). Internals were
refactored to decouple "apply and commit operations" from `PendingWrite`/wave-of-`PendingWrite`s: the core
(`CommitOperationsWithMapGrowthRetry`/`ApplyOperations`) now takes a flat `List<(byte[] Key, byte[]? Value)>`
plus a `durable` bool directly, so both the coalesced-wave path and the chunked-single-batch path share it.
Each chunk becomes its own `WriteWave` for diagnostics purposes, so the next check-in should show Account's
wave count rise roughly 10x (42,206/4096 ≈ 11 chunks per what used to be one wave) with `AvgWriteWaveOps`
dropping to at most the cap - if it doesn't, something in this fix isn't taking effect and needs re-checking
before trusting it. Two tests updated/added to match the new semantics: a single oversized batch now *does*
split into `ceil(ops/cap)` physical commits with correct data in every chunk
(`MaxOperationsPerWave_splits_a_single_callers_oversized_write_batch_into_sequential_chunks`), while a batch
under the cap still commits as exactly one (`MaxOperationsPerWave_does_not_split_a_single_callers_write_batch_under_the_cap`).

**Confirmed working, then confirmed unrelated to the `newPayload` timeouts after all.** A further live check-in
showed the chunking fix doing exactly what it was built to do: Account's `avg ops/wave` dropped from 42,206 to
3,767 (right under the 4096 cap) and average commit latency dropped from ~157ms to ~18ms - an ~8.7x reduction,
in line with B+tree mutation cost scaling with batch size. But the consensus-client `newPayload` timeouts kept
recurring regardless, and at ~18ms a single commit cannot plausibly account for an ~8-second timeout even under
back-to-back queuing. Decisive test: **the same run reproduced identically on stock `master` (RocksDB, no LMDB
plugin at all)** - definitively ruling out this plugin, and the DB backend generally, as the cause. Root cause
is elsewhere (networking, the specific consensus-client version in use, or something else entirely) and is out
of scope for this experiment.

Net effect on this experiment: the two write-path fixes above (`MaxOperationsPerWave` coalescing cap and
`CommitLargeWriteInChunks`'s chunking) remain worth keeping - they are real, measured, correct improvements to
a genuine design gap (an unbounded physical commit really can block every concurrent reader for its whole
duration, and that duration really was unbounded before these changes) - they simply turned out not to be the
explanation for the specific symptom that prompted looking for them. This is a useful, if slightly deflating,
reminder for validation runs going forward: a plausible-sounding, testable hypothesis that the data appears to
support is still worth checking against a same-symptom-without-the-change control (here, `master`) before
concluding the fix addressed the reported problem, not just improved a real number.

**Reopened: a later, more careful A/B comparison found sync itself running ~2x slower on the LMDB build than
on `master`, with the *same* `newPayload` timeout signature just less frequent on `master`.** This is a
different claim than the one just closed above (a single commit blocking long enough to cause one timeout) -
it's about aggregate load: if LMDB's write path is genuinely and significantly slower overall for snap sync
(plausible on architectural grounds - see "Architecture" above: a copy-on-write B+tree has to touch and
rewrite whole pages for every write, including page splits, with no compaction to amortize it later, versus
RocksDB's LSM-tree design which is built to absorb high-volume small-random-key writes cheaply; Ethereum
account/storage keys are hashed paths, so snap-sync insertion order is close to LMDB's worst case), that adds
real system-wide load and could make an already-marginal, DB-independent timing issue trip into an actual
timeout more often, without LMDB being that issue's root cause. Both things can be true at once: `master`
reproducing the *same* error rules out LMDB causing it, while a 2x slowdown could still be making it *more
frequent* by leaving less scheduling headroom for everything else the node is doing at the same time.

**Added finer-grained diagnostics to test this directly, rather than reasoning about it further from 30-second
aggregates.** `LmdbDb` now takes an `ILogManager` and logs, per individual event rather than only as a rolling
average:
- Every `SubmitWrite` call (one account-range or storage-range commit request from the snap-sync caller's
  perspective, however many physical commits it ends up chunked into) - at `Debug`, unconditionally, with
  operation count, durability, and elapsed milliseconds; at `Warn` when it exceeds `SlowOperationThresholdMs`
  (100ms) and `Debug` isn't already enabled, so a genuine outlier is visible without raising log verbosity
  first.
- Every `CreateSnapshot` call that exceeds the same threshold, at `Warn` - this is the read side every
  concurrent caller (including whatever `newPayload`'s own state reads go through) shares the same
  `_writeCommitGate` lock with, so a slow one here is direct evidence of the blocking theory, independent of
  the write side's own reported duration.

To see the per-request `Debug` line for every write (not just outliers), add a rule to `NLog.config`'s
`<rules>` section (logger names strip the `Nethermind.` prefix - see `ILogManager.GetLoggerName`):
```xml
<logger name="Db.Lmdb.*" minlevel="Debug" writeTo="all" final="true" />
```
The `Warn`-level outlier lines for both writes and slow snapshots are visible at the default log level with no
config change. Both are timestamped by the ambient logger, so they can be matched directly against a
consensus client's own logged timestamp for a specific `newPayload` timeout.

**Found via the new per-event logs: GC pauses are a minor contributor (mostly 0ms, occasionally ~64ms), not
the dominant cause - but the log pattern revealed a different, real bug.** A live run's `Warn` lines showed
clusters of `LmdbStorage` writes with wildly different, tiny operation counts (2, 3, 8, 9, 10 ops) all
reporting **nearly identical durations within each cluster** (e.g. eight separate writes all at ~103.3-103.5ms,
a later batch all at ~317.5-326.4ms) - alongside one huge Account or Storage write (42,000+ or 49,780
operations) in the same window. A 3-operation write cannot organically take over 100ms; many unrelated writes
finishing at the *same* moment despite unrelated operation counts is the signature of all of them queuing
behind one shared blocking event and being released together once it clears.

Root cause: `DrainIfLeader`'s single leader thread processed one dequeued `PendingWrite` to full completion
before ever re-checking the queue. For an oversized write routed to `CommitLargeWriteInChunks`, "full
completion" meant running its *entire* multi-chunk sequence (~11 chunks for a 42,000-operation write) before
looking at the queue again - so every other write submitted during that whole sequence, however trivial,
queued behind the entire thing rather than just behind one chunk. This was a real fairness bug introduced by
the chunking fix itself: bounding *physical commit size* (done, and confirmed working via `AvgWriteWaveOps`)
is not the same as bounding how long one *logical write request* can monopolize the single-writer role once it
needs multiple chunks.

**Fix**: `CommitLargeWriteInChunks` now calls a new, reentrant `DrainPendingWrites` between each chunk, so a
large write's chunking yields to whatever else is queued instead of running to completion uninterrupted.
`DrainIfLeader`'s own dequeue loop was extracted into this same method, since both need identical
coalesce-or-chunk logic; nesting depth is bounded by how many oversized writes happen to be paused mid-chunk
at the same moment (naturally small in practice), not by total volume processed, since each nested call fully
drains before returning. A new test
(`CommitLargeWriteInChunks_yields_to_other_queued_writes_between_chunks`) drives a large write with 200 chunks
concurrently against 32 small writers and asserts at least one small write completes before the large write's
entire sequence finishes - with the pre-fix monopolizing behavior, every small write would necessarily show up
strictly after it. All 52 tests pass, repeated 5x for this specific test given its concurrency-timing nature.

This does not, on its own, explain why reads (`CreateSnapshot`) would block on writes - that remains the
separate, already-understood mechanism (`_writeCommitGate`'s consistency requirement for the reader pool, see
above). What it does explain is why *other writes* - and anything synchronously waiting on one, including
whatever in `newPayload`'s own path submits a write through this same coordinator - could stall for the
duration of an entire unrelated oversized batch rather than just one bounded chunk of it.

**The stalls persisted even after the chunking-fairness fix - and the pattern, plus one decisive piece of
external evidence, points at .NET ThreadPool starvation from this plugin's own coordinator design, not LMDB
itself.** A further live run showed the same signature (a 10-op and a 36,233-op write both measuring
~9453.6ms, with an *identical* 134.1ms GC pause during both) - strong confirmation the two measurement windows
genuinely overlapped with the same real stall, not a logging artifact (each `SubmitWrite` call measures its own
local start-time and GC-pause baseline; there is no shared mutable state that could cross-contaminate one
call's log line with another's data - two independent calls reporting an identical GC-pause delta down to
0.1ms is only possible if their windows truly overlapped). Decisive: **SSD activity was confirmed at ~0% during
a stall**, ruling out the disk-I/O/sparse-file-growth theory raised earlier.

`LmdbDb.SubmitWrite` blocks synchronously (`ManualResetEventSlim.Wait()`, deliberately - profiling showed this
faster than a `TaskCompletionSource` for this exact case) for every caller that does not win the group-commit
leader race. If snap sync's dispatcher fans out many concurrent write-submitting tasks at once, most of them
occupy a real ThreadPool thread while doing nothing (no CPU, no I/O, matching the ~0% SSD activity observation)
- and .NET's ThreadPool only grows by roughly one thread per 500ms-1s under sustained demand, by design,
specifically to avoid oversubscription. A burst of concurrent writers exceeding currently-available threads can
turn into a multi-second stall for everyone, regardless of how little work any individual one has to do - which
fits every piece of evidence gathered so far: no disk I/O, minimal GC, and wildly different operation counts
reporting identical stall durations (all just waiting on the same scarce resource, released together). It would
also explain the ~2x overall slowdown against `master`: if RocksDB's write path never makes a snap-sync task
block on an internal coordinator the way this plugin's does, `master` simply never hits this starvation pattern
at all, independent of anything about the two storage engines' own I/O performance.

**Two changes**, both narrowly scoped to confirm-or-refute this before trusting it: (1) `FormatThreadPoolState`
adds ThreadPool thread counts (current/available/max, both worker and I/O) and the pending-work-item count to
every slow-event `Warn` line, so the next occurrence shows this directly instead of requiring inference; (2)
`LmdbDbFactoryDecorator`'s static constructor raises the process-wide ThreadPool minimum to at least 512 (only
if currently lower - never lowers whatever the host process already configured), the standard, low-risk
mitigation for exactly this class of problem. Both are gated behind the LMDB plugin actually being enabled
(the static constructor only runs once the decorator type is first used, i.e. only when `Lmdb.Enabled` causes
Autofac to construct it), so neither affects a `master`/RocksDB run at all.

**Refuted by the very diagnostic added to confirm it.** The next live run's slow-event logs showed the
ThreadPool in a completely healthy state during a ~1820ms stall affecting two independent writes (3 ops and
49,094 ops, again with an identical GC-pause delta, again confirming genuine overlap not a logging artifact):
`277 threads, 32750/32767 worker avail, 1000/1000 IO avail, 0 queued`. That is the *opposite* of starvation -
tens of thousands of available threads, essentially nothing queued. `SetMinThreads(512, ...)` is left in place
(it is harmless, and `ThreadPool.ThreadCount` staying below it simply reflects that nothing has demanded more
threads than the pool already has, which is consistent with there being no queueing pressure) but it is **not**
the fix for these stalls, and should not be mistaken for one by a future reader of this file.

**Current leading hypothesis, not yet confirmed: LMDB's documented write-commit cost scaling with the number of
concurrently active read transactions**, independent of the committing write's own size. On every write commit,
LMDB must determine the oldest active reader transaction, to know which old (now-superseded) pages are safe to
reclaim for copy-on-write reuse versus which might still be visible to some live reader - standard LMDB
behavior, not a LightningDB-specific quirk, and one documented to scale with how many readers are concurrently
open. `CreateSnapshot`'s pool holds `SnapshotReaderPoolSize` (`Environment.ProcessorCount`) read transactions
alive for however long the *caller* takes to dispose the whole snapshot, not just for one quick read - if many
snapshots are alive at once during heavy snap-sync activity (e.g. a caller doing further processing before
disposing), the reader table could have hundreds-to-thousands of active entries at any moment, independent of
disk I/O, GC, or ThreadPool state - fitting every remaining piece of evidence: write latency uncorrelated with
the write's own op count, correlated instead with ambient conditions at the moment.

Added `LmdbDb._aliveSnapshotPoolReaders` - a live gauge (not a reset-on-read counter, unlike the other
`DiagnosticsSnapshot` fields) of pooled read transactions currently alive across every not-yet-disposed
snapshot on a given column, incremented in `CreateSnapshot` and decremented in `LmdbDbSnapshot.Dispose`. Logged
alongside every slow-event `Warn` line and in the periodic 30-second diagnostics line
(`AlivePooledReaders N`). A high count during the next slow-write occurrence would confirm this directly; a low
one would rule it out just as clearly as the ThreadPool numbers ruled out starvation.

**A much larger, separate phenomenon: a total process freeze (~60-90s), not just a slow LMDB operation.** A
live run showed *zero* `Get`/`CreateSnapshot`/`WriteWave` activity on both columns for 60+ seconds, with
`AlivePooledReaders` frozen (not fluctuating) the whole time, timed almost exactly with a
"Changing sync StateNodes to StateNodes, FastSync..." phase-transition log line. Independently confirmed as a
genuine *whole-process* stall, not an LMDB-internal one: four simultaneous JWT Engine API "Token expired"
errors fired at the exact moment the freeze ended, with `iat`-to-`now` gaps of 62-84 seconds (all exceeding the
60s `JwtTokenTtl`) - meaning Nethermind was not processing *any* incoming authenticated request during the
freeze, not just DB operations. This is almost certainly the real mechanism behind the CL-visible `newPayload`
timeouts, not the smaller-scale per-write latency investigated above.

An `Explore` trace of the "Changing sync StateNodes to StateNodes" log line
(`MultiSyncModeSelector.Update()`, `Nethermind.Synchronization/ParallelSync/MultiSyncModeSelector.cs:191`) found
a strong candidate mechanism: `Synchronizer.GCOnFeedFinished` (`Synchronizer.cs:165-171`, wired to
`ISyncModeSelector.Changed` and invoked *inline* on the selector's own serial loop thread) calls
`GC.Collect(2, GCCollectionMode.Aggressive, true, true)` - a **blocking, compacting Gen2 collect**, which
suspends every managed thread for its duration. `SyncConfig.GCOnFeedFinished` defaults to `true`
(`SyncConfig.cs:88`), so this is active on a real node unless explicitly disabled. On a heap full of snap-sync
buffers, a forced compacting Gen2 collect can legitimately run for tens of seconds to over a minute - matching
the observed symptom and its "worse with a larger pending queue" behavior. Caveat noted by the trace: for the
*exact* two log lines captured, the watched mode-finished bit set (`StateNodes`/`FastReceipts`/
`FastBlockAccessLists`/`FastBlocks`) doesn't literally toggle between them (only `FastSync` does), so this
specific pair of lines isn't itself proof - a `StateNodes`/`FastBlocks` finish transition adjacent to them would
need to be confirmed separately.

**To test this without touching core sync code**, added a dedicated `Lmdb.GcPauseHeartbeat` background thread
in `LmdbDbFactoryDecorator` (started once per process, the first time the plugin is constructed): it pings a
monotonic `Stopwatch` timestamp every 250ms, completely independent of whether any LmdbDb call is in flight,
and logs `GC.GetTotalPauseDuration()`'s delta whenever a gap exceeds 1s. A blocking GC suspends *every* managed
thread, including a plain background `Thread` like this one, so a late wake-up here is itself direct evidence
of a process-wide pause - and the pause-duration delta over that exact window tells you whether GC accounts for
it. It also logs an unconditional "alive" line once a minute (and once on startup) regardless of whether any
stall was ever detected, specifically so silence in the log can't be misread as "no stall happened" when it
might instead mean "this thread isn't running, or its output isn't reaching the log, for some unrelated
reason" - confirmed necessary in practice: an initial run with the stall-only logging produced no output at all
during a confirmed hang, and only adding the unconditional heartbeat line distinguished "thread not running /
not logging" from "thread running, nothing to report."

**Refuted.** A live run froze completely - including no longer responding to Ctrl-C at all, strong independent
evidence of a severe stall - yet the heartbeat's periodic alive line kept logging right on schedule throughout:
`Lmdb heartbeat alive - no gap over 1s in the last ~1m, total GC pause so far: 21.2s, Gen0/1/2 collection
counts: 687/306/155`. A real stop-the-world blocking GC would have paused this thread along with everything
else; it didn't, so whatever causes this freeze is **not a managed GC pause** (and specifically not
`GCOnFeedFinished`'s `GC.Collect`, despite that remaining a plausible-looking mechanism on paper). The
`SetMinThreads`/ThreadPool-starvation theory was already refuted above by direct diagnostic evidence; this rules
out the other main .NET-runtime-level explanation too. The heartbeat thread itself continuing to run while
Ctrl-C and everything else is unresponsive also argues against an ordinary managed-lock deadlock blocking *all*
threads (a deadlock wouldn't stop an uninvolved thread from ticking, same reasoning as for GC - but Ctrl-C
specifically failing suggests whatever's stuck sits on a path the shutdown/signal handler also depends on, or
the stall is native/OS-level rather than managed entirely). Next concrete step: capture a thread/process dump
during the next occurrence (e.g. `dotnet-dump collect` or Task Manager "Create dump file") to see directly what
every thread - managed and native - is actually blocked on, since both leading hypotheses are now eliminated by
direct evidence rather than inference.

**Root cause found via a live thread dump (`dotnet-dump analyze` → `pstacks`/`syncblk`), and it has nothing to
do with this plugin.** 221 live threads, 176+ parked in `ManualResetEventSlim.Wait`/`Monitor.Wait` inside
`Nethermind.Trie.PatriciaTree.BulkSetParallelJobs` → `Nethermind.Core.Threading.ParallelUnbalancedWork`, reached
from 16 concurrent `SnapSyncFeed.HandleResponse` → `SnapProvider.AddStorageRange` → `BuildAndVerifyRoot` →
`PatriciaTree.BulkSet` chains - zero frames anywhere in `Nethermind.Db.Lmdb` (our `Lmdb.GcPauseHeartbeat` thread
is the only Lmdb-namespace frame present, and it's simply sleeping, as designed). `syncblk` showed zero held
managed monitors (`Free 8056` of `Total 9471`, no owner rows) - ruling out a classic lock-ordering deadlock;
this is thread-pool oversubscription/a scheduling convoy, not a deadlock, which is consistent with it eventually
self-resolving in earlier (non-Ctrl-C-killing) occurrences.

Mechanism, confirmed by reading the source (`ParallelUnbalancedWork.std.cs`, `PatriciaTree.BulkSet.cs`,
`SimpleDispatcher.cs`):
- `SimpleDispatcher.Run`'s `processingSemaphore` genuinely bounds concurrent `HandleResponse`/top-level `BulkSet`
  calls to `MaxProcessingThreads` (default `Environment.ProcessorCount`) - matching the observed "16" exactly
  (`SimpleDispatcher.cs:63-67,197-205`).
- Each of those ~16 calls, on hitting a full branch of ≥128 entries (`BulkSet.cs:259`; threshold lowered
  256→128 in commit `21910a0552` for unrelated latency reasons), calls `BulkSetParallelJobs`, which asks
  `ParallelUnbalancedWork.For` for up to `ProcessorCount` *more* ThreadPool workers (`ParallelUnbalancedWork.cs:
  43`, `std.cs:51-64`) - with **no awareness of the other ~15 concurrent top-level calls doing the exact same
  thing**, because nothing opens a `WorkerScope` (the mechanism that would cap this) anywhere on the snap-sync
  path.
- Recursion doesn't suppress further fan-out: `BulkSet.cs:171`'s `flags & ~Flags.DoNotParallelize` *clears* the
  no-parallelize bit rather than setting it (the adjacent `// Only parallelize at top level.` comment is stale/
  misleading relative to what the code actually does), so a sufficiently large child subtree recurses into
  *another* full `BulkSetParallelJobs` fan-out while the parent worker sits parked on `ManualResetEventSlim.Wait`
  - occupying a ThreadPool slot while waiting on a nested batch that itself needs fresh ThreadPool slots to
    progress. With ~16 top-level trees each doing this at multiple nesting depths, the result is a combinatorial
    pile-up of blocked-and-waiting ThreadPool threads - exactly the 176+ parked threads observed.
- `ParallelUnbalancedWork.WorkerScope` already exists in the codebase as the fix for precisely this class of
  problem (PR #13770 / commit `3adf9eae56`, "Budget parallel work in state root, blooms, receipts root and
  prewarming") and is wired into `BlockProcessor.cs:197`, `BlockCachePreWarmer.cs:201`, and `Eip2537.std.cs:42` -
  but **never into the snap-sync `HandleResponse → AddStorageRange → BuildAndVerifyRoot → BulkSetAndUpdateRootHash
  → BulkSet` path**, which still runs with `WorkerScope.Current == null` and therefore gets an unbudgeted,
  unbounded-across-callers `ProcessorCount` allowance at every nesting level.

**This is a pre-existing bug in core Nethermind trie/sync code, independent of storage backend** - consistent
with the earlier observation that `master` shows the same `newPayload`-timeout symptom, just less frequently.
LMDB's different timing characteristics most likely just make concurrent storage-range response bursts line up
often enough to trigger this fan-out collision more visibly; the bug itself lives entirely in
`Nethermind.Trie`/`Nethermind.Core.Threading`/`Nethermind.Synchronization`, well outside this plugin's
`Nethermind.Db.Lmdb` boundary (see "Non-goals"). Fixing it (wiring `BeginWorkerScope` into the snap-sync
`BulkSet` entry point, sized consistently with `SimpleDispatcher`'s `MaxProcessingThreads`) is a separate,
well-scoped piece of work that this experiment surfaced but does not itself own.

**Confirmed recurring** (2026-10-08, ~20% into a later sync): a ~30s window showed Account at literally
`WriteWave x0`/`CreateSnapshot` collapsing from 45,111 to 4 calls, Storage reduced to a trickle (4 tiny 2-op
waves) rather than complete silence, and `AlivePooledReaders` frozen at 256 throughout - the same signature as
before. The `Lmdb.GcPauseHeartbeat` line logged immediately after ("no gap over 1s in the last ~1m") again rules
out a GC stop-the-world pause for this specific occurrence, consistent with it being the same core
`ParallelUnbalancedWork`/`BulkSetParallelJobs` thread-oversubscription storm, not a new or LMDB-specific
phenomenon. Decision stands: out of scope for this experiment, not filed upstream for now.

**Pushback worth recording**: if this fan-out bug fired equally regardless of backend, months of RocksDB
production use should already have surfaced it - it hasn't, which means the real open question is why swapping
the backend changes how often the trigger condition (many large concurrent `BulkSet` calls landing at once)
actually occurs, not just that the bug exists. Leading theory, not yet directly confirmed: this plugin's write
path is confirmed ~2x slower than RocksDB's overall (see the earlier master-vs-LMDB comparison); a slower
write-commit path means incoming snap-sync responses can back up faster than they drain, making it more likely
that a burst of many large, concurrent storage-range `BulkSet` calls gets released together - precisely the
fan-out trigger - even though the vulnerable code itself is backend-agnostic. This reframes the plugin's own
write latency as a real, in-scope lever even though the frozen-thread mechanism itself sits in core code: a
faster write path could reduce how often the core bug's trigger condition is reached, without touching
`Nethermind.Trie`/`Nethermind.Synchronization` at all.

### `PutOptions.AppendData` (`MDB_APPEND`) investigated and ruled out for Account/Storage

Considered as a lever for the Account column's dominant cost pattern (few, very large, already-sorted single
batches - see the `AvgWriteWaveOps` finding above). A trace confirmed each individual snap-sync range response
*is* written to FlatDb Account/Storage in strict ascending key order (`SnapProviderHelper.cs:144-148` validates
this on the input before persisting; `FlatSnapStateTree.Commit`/`FlatSnapStorageTree.Commit` then write it out
in a plain sequential loop - `BulkSetParallelJobs`'s parallel workers never touch the write batch at all, so
"parallel workers scramble the order" is specifically refuted). The real obstacle is this plugin's own write
coordinator: `CommitWaveAndSignal` coalesces multiple different callers' batches into one transaction by
straight concatenation (`LmdbDb.cs:522`), and concurrent callers process unrelated key ranges, so the combined
sequence isn't globally sorted even when each contributor's own slice was - a self-verifying ascending-order
check immediately before the commit loop would correctly detect and avoid this case.

**Initially ruled out on a flawed assumption, then corrected.** `AppendData` means "append ... to the end of
the database. No key comparisons are performed" (LightningDB.xml:986) - every appended key must be greater than
**the current maximum key already in the database**, not merely greater than its neighbors within the same
batch. Account/Storage keys are hashes (`Address.ToAccountPath => KeccakCache.Compute(Bytes)`,
`Nethermind.Core/Address.cs:278` - "Path" in snap sync/trie terminology is literally the Keccak hash, not a
distinct concept), effectively uniformly random across the whole keyspace - from this alone, it looked like any
already-populated column's existing max key would already sit so close to the keyspace's theoretical ceiling
that a new batch's minimum key would essentially never exceed it, making the optimization worthless in practice.

That reasoning missed how snap sync's range-sync phase actually works: both account and storage ranges are
delivered in ascending path order **across the entire sync, not just within one response** - the protocol
partitions and walks the whole keyspace from lowest hash to highest, so the database's frontier genuinely does
advance monotonically through the bulk of a sync (the "existing max key is already near the ceiling" framing
only applies to an already-fully-populated column, not one that's still filling in from the bottom up). The
problem phase is different: snap sync's later *healing* pass patches already-synced keys out of order, which
would break the invariant - as would several coalesced callers' operations commingled in one wave, or two
concurrent range responses completing out of request order.

**Implemented as a self-verifying check**, exactly because it doesn't need to assume which of these is true for
any given commit: `LmdbDb.CanUseAppendData` scans `ApplyOperations`' operations list once (ignoring Deletes,
which don't add a key and so can't break the invariant), confirms the Puts are strictly ascending among
themselves, and - the check the flawed version above was missing - opens a cursor on the live transaction and
compares the batch's first key against the database's actual current last key (`cursor.Last()`) before choosing
`PutOptions.AppendData` over `PutOptions.None`. This is correct regardless of *why* a batch might not qualify:
range-sync's genuine forward progress lights it up, while healing, coalesced waves, and out-of-order concurrent
completions all safely and automatically fall back, because the check is against the database's real state at
commit time, not an assumption about caller behavior or sync phase. Two new counters
(`TotalPutCount`/`AppendedPutCount`, surfaced in `DiagnosticsSnapshot` and the periodic 30s diagnostics line as
`AppendData x/y puts`) make how often it actually engages directly observable on a live run rather than assumed.
Covered by four new tests in `LmdbDbTests.cs`, including one specifically regressing the original mistake
(`CanUseAppendData_checks_the_databases_actual_current_max_key_not_just_batch_order` - an internally-ascending
batch that reaches backward into already-written keyspace must not append, and a later batch genuinely beyond
the database's current max must re-engage). All 57 pre-existing tests plus these 4 pass.

**How much of the real workload this actually covers, checked against the real partitioning code**: snap
sync's account range is split into `SyncConfig.SnapSyncAccountRangePartitionCount` (default 8) fixed, equal,
contiguous, non-overlapping sub-ranges of the keyspace (`ProgressTracker.cs:101-134`,
`SetupAccountRangePartition`), with up to all 8 processed concurrently and no ordering between them
(`ShouldRequestAccountRequests`, line 352; "using a queue here to evenly distribute requests across
partitions", line 68). Because the partitions are disjoint and ordered, only the single *topmost* partition can
ever have its keys exceed the database's live global max - every other partition's entire range sits strictly
below it by construction, so `CanUseAppendData` will reliably and correctly decline for them once the top
partition has written anything at all. In an 8-way sync this means roughly 1/8 of account-range write volume
benefits, not "most of a sync" as an earlier framing of this implied - still correctly handled (no corruption
risk either way), just a smaller real payoff than first described. Whether storage-range writes have an
equivalent dynamic (large-storage accounts are tracked individually via `_largeStorageProgress`, a
`ConcurrentDictionary`, not a fixed keyspace partition like accounts) has not yet been traced.

**Investigated and refuted: a "sorted-cursor" middle ground that doesn't need the strict append precondition.**
Hypothesis: a shared `LightningCursor.Put` over pre-sorted keys might get a real speedup from B+tree locality
(cached page-stack position) even without qualifying for true `MDB_APPEND` - which, if real, would apply to
essentially the whole workload rather than ~1/8 of it. Tested directly with an isolated benchmark
(`scratchpad/lmdb-spike/Program.cs`): pre-populated a 2,000,000-key database, then timed 40,000-op batches
(matching the measured Account avg ops/wave) four ways, interleaved round-robin across 8 repetitions so DB
growth affects each equally:

| Scenario | Avg time |
|---|---|
| A: unsorted keys, repeated `tx.Put` | 307.6ms |
| B: sorted keys, repeated `tx.Put` | 298.8ms |
| C: sorted keys, one shared `LightningCursor.Put` | 295.5ms |
| D: sorted keys always beyond the DB's live max, `PutOptions.AppendData` | 2.4ms |

Sorting alone is within noise (~3%, and C was sometimes slower than B across individual reps). A shared cursor
adds nothing measurable beyond that. True `MDB_APPEND` is ~125x faster than any of A/B/C - and that entire
speedup comes specifically from skipping key-comparison/tree-search altogether, not from any softer notion of
"locality." There is no sorted-cursor middle ground in LMDB: a batch either qualifies for true append (skip the
search) or pays the same real B+tree search cost as everything else, sorted or not. **Not pursued further.**

**`MDB_WRITEMAP` ruled out on the vendored LMDB header's own documented tradeoff, before any benchmark.**
`lightningdb/0.23.0/wasm/lmdb.h`'s `MDB_WRITEMAP` comment states plainly: *"This may be slightly faster for DBs
that fit entirely in RAM, but is slower for DBs larger than RAM."* This directly conflicts with this
experiment's own central validation question (see "Validation methodology" below: *"does read/write throughput
and tail latency stay acceptable as the dataset grows past available RAM"*) - the one scenario this plugin is
explicitly being validated against is the one regime where LMDB's own documentation says this flag is a net
loss. **Not implemented.**

**Added two new diagnostics instead, to directly answer "where does a slow write's time actually go"** rather
than add another indirect/adjacent signal:
- **Phase breakdown of each physical commit** (`CommitOperationsWithMapGrowthRetry`): `begin` (acquiring the
  write transaction), `apply` (the actual `ApplyOperations` Put/Delete loop - the real B+tree insert cost),
  and `commit` (the flush/meta-page step, inside `_writeCommitGate`) are now timed separately, surfaced as
  `AvgWriteBeginMicros`/`AvgWriteApplyMicros`/`AvgWriteCommitMicros` in `DiagnosticsSnapshot` and the periodic
  30s line (`WriteWave x... = begin ...us + apply ...us + commit ...us`), and logged on every slow-commit outlier
  via a new dedicated Warn line. Every diagnostic before this one could only report a wave's total duration as
  one opaque number - this isolates which phase actually dominates a given slow event.
- **Windows page-fault count around slow events** (`WindowsProcessDiagnostics.cs`) - called for from the start
  in "Validation methodology" below but never implemented until now. "SSD activity near 0%" during an earlier
  stall ruled out *sustained* disk throughput, but not a scattered burst of single-page random reads against the
  memory-mapped file, which can be real page-fault latency without showing up as sustained activity on a coarse
  utilization graph. Implemented as a cached sample (piggybacked onto the existing `Lmdb.GcPauseHeartbeat`
  thread's tick, not a syscall on every write) for the cheap "before" baseline, with one real, uncached
  `GetProcessMemoryInfo` call only at the point of actually emitting a slow-event log - deliberately keeping the
  real P/Invoke cost off the hot path, the same tradeoff already made for the GC-pause heartbeat itself. No-op
  (returns 0) on non-Windows. Wired into both `SubmitWrite`'s existing slow-event log and the new physical-commit
  log above.

All 57 pre-existing tests still pass with both additions in place.

**First live data (2% into sync, cold start): a 3-op `LmdbStorage` write took 163.7ms** with only 32
`AlivePooledReaders`, 58.2ms GC pause (~35% of total, real but not dominant), and 37,649 page faults - a number
that needs a caveat: `GetProcessMemoryInfo` is process-wide (Windows exposes no way to scope it to the LMDB
mmap specifically), so this reflects *overall* process paging pressure during the window, not something this
one 3-op write caused by itself. The fault-rate (~2.8us/fault) is too fast to be real disk reads - consistent
with cheap soft faults (page-table setup for a growing, still-sparse mmap) rather than hard faults, plausibly a
transient cold-start effect rather than steady state.

**Second live data (20% into sync) is the clearest result yet, and gives the first phase-isolated evidence for
the reader-count hypothesis.** The periodic 30s line's phase breakdown, for both columns independently:

- Account: 14,783.8us avg wave = begin 2.9us + apply 5,744.3us + **commit 9,036.4us (61% of total)**
- Storage: 2,116.9us avg wave = begin 1.7us + apply 538.1us + **commit 1,576.9us (74.5% of total)**

Commit dominates over apply by 1.6-3x in both columns, at `AlivePooledReaders` = 256 in both - exactly the
phase where LMDB must find the oldest active reader for copy-on-write page reclamation, exactly the mechanism
the hypothesis predicted, now isolated from begin/apply for the first time rather than inferred from an opaque
wave total. Also confirms `AppendData` is engaging as designed: Account showed 42,217/210,974 puts appended
(~20%, higher than the naive 1/8 estimate - plausibly the topmost partition's range runs slightly larger and/or
other partitions have already finished progressing at 20% overall completion).

Two individual slow-event lines from the same window (354.0ms/3 ops and 323.0ms/39,650 ops) look like a
**separate phenomenon - queueing, not a slow commit**: GC pause only ~4% of the total, page faults far lower
than the cold-start sample, ThreadPool healthy, yet `SubmitWrite`'s own measured duration vastly exceeds the
~2-15ms wave average. No matching physical-commit slow-log line was found near either timestamp (to be
confirmed by the user). Storage alone ran 6,429 waves in 30s (~1 every 4.7ms), close to the single-writer
throughput ceiling implied by its own ~2.1ms average wave time - a momentary burst above that sustainable rate
would produce exactly this kind of queueing blowup for an unlucky caller without any single commit actually
being slow, consistent with LMDB's fundamental one-writer-at-a-time ceiling rather than any storage-engine bug.

**Third live sample (37%) showed the confound directly**: `AlivePooledReaders` rose 256→304 (+18.75%) alongside
substantial DB growth (Account 9.8M→16.6M keys, Storage 41M→75M keys) in the same window, and *both* `apply` and
`commit` rose (Account apply +44.5%, commit +23.6%; Storage apply +23.4%, commit +18.5%) - `apply` has no reason
to care about reader count at all, so its growth (sometimes outpacing commit's) points at ordinary B+tree-depth
growth as at least as plausible an explanation as reader count. Live correlational data alone couldn't separate
the two variables, since they move together over a real sync.

**Resolved with a corrected controlled benchmark** (`scratchpad/lmdb-reader-cost-bench/Program.cs`): pre-populate
a fixed 2,000,000-key database, then hold batch size fixed (4,096 ops) while varying only the number of
concurrently-open read-only transactions (0 to 1,024), round-robin across 10 repetitions so any residual DB
growth over the benchmark is spread evenly rather than correlated with reader count (same technique as the
AppendData spike). **First attempt was invalid** - forgot `TransactionBeginFlags.NoSync`, so every commit paid a
real fsync, unlike the live system's actual (`durable=False`) writes; re-run with `NoSync`:

| Readers | Avg apply (ms) | Avg commit (ms) |
|---|---|---|
| 0 | 9.077 | 9.995 |
| 32 | 8.544 | 9.353 |
| 64 | 8.747 | 9.388 |
| 128 | 8.784 | 9.329 |
| 256 | 8.730 | 9.798 |
| 512 | 8.475 | 9.791 |
| 1024 | 9.490 | 9.814 |

**Flat. No trend with reader count at all**, from 0 to 1,024 readers. This directly refutes "LMDB's own
reader-table scan drives commit cost" as the explanation for commit dominating apply in the live averages -
properly isolated and measured, reader count has no detectable effect in this regime. It also shows apply and
commit are roughly comparable (~9ms each) with zero contention - nothing like the live 1.6-3x gap.

**Confirmed cleanly by the sync's own phase transition, with no ambiguity this time** (closing the AppendData
loop specifically): the moment "Snap - State Ranges (Phase 1) finished" / "Starting state sync" appeared in the
log, the very next diagnostics line showed `AppendData` drop to exactly 0% for both columns (Account 0/695,
Storage 0/1,680,331) - directly confirming that it only engages during the keyspace-filling portion of
range-sync and safely, automatically stops once the workload shifts to healing/state-node-sync's out-of-order
patches. The same sample reconfirmed batch-size-drives-commit-cost from the opposite direction: Account's writes
collapsed to ~1 op/wave during healing and apply started dominating instead of commit (638.3us of 907.0us
total); Storage stayed at a large ~1,776 ops/wave and commit still dominated (9,336.4us of 14,715.9us). No
catastrophic process freeze occurred at this particular phase transition (unlike the earlier one traced to the
unrelated core `BulkSetParallelJobs` bug), and the subsequent "Saving root" log line landed successfully -
implicit correctness evidence that the LMDB-backed Account/Storage data produced a state root that actually
validated. The stack-buffer-overrun crash reported mid-session (`STATUS_STACK_BUFFER_OVERRUN` in
`rocksdb_get_into_buffer_cf`, documented in the sibling BTDB experiment's `RESULTS.md`) remains unconfirmed for
this run - `LmdbDb` was verified to inherit the same structural protection BTDB had for the primary vulnerable
site (it never overrides the unsafe `Get(key, Span<byte>)` fast path, so calls fall through to the
bounds-checked default), but whether the crash came through a still-RocksDB-backed History column or something
else was never confirmed against an actual crash dump.

**Resolved: lock-wait is negligible too.** A live sample with the new split in place
(`commit 9880.1us [lock-wait 2.0us + call 9878.1us]` for Account; `commit 1713.9us [lock-wait 74.9us + call
1639.0us]` for Storage) shows lock-wait is a tiny fraction of commit time in both columns - the
`_writeCommitGate` contention hypothesis is **refuted** too. The actual native `tx.Commit()` call itself is
genuinely the cost.

**Pivoted to batch size as the remaining candidate**, since the same sample showed a striking asymmetry:
Storage's database has 4x more keys than Account's (33.3M vs 8.0M) yet its commit call is 6x *cheaper* (1,639us
vs 9,878us) - ruling out raw DB size as the dominant driver and pointing at ops/wave instead (Account averages
3,880 ops/wave against Storage's 247 - a ~16x gap against a ~6x cost gap, consistent with a mix of fixed
overhead plus a real per-op cost). Built a third controlled benchmark
(`scratchpad/lmdb-batch-size-bench/Program.cs`): fixed 2,000,000-key DB, fixed 256 held-open readers, varying
only batch size (64 to 16,384), `NoSync` to match the real write path.

**First pass (10 reps) showed a smooth-ish upward trend with one anomaly at batch=4096** (139.8ms, breaking an
otherwise monotonic curve) - re-ran with 30 reps and min/median/max tracking to check if that was a single bad
repetition. It wasn't a fluke in the way expected, but revealed something more interesting: **the distribution
is strongly bimodal** - median commit time collapses to near-zero (0.027-0.040ms) for batch sizes 2048+, while
mean and max stay large and scale with batch size (up to 123ms max at 16,384). Checked whether GC explained this
directly (not inferred): instrumented `GC.CollectionCount(0/1/2)` immediately before/after apply and commit, and
pre-generated all batch data *outside* the timed section (the prior per-iteration allocation right before each
timer start was itself a candidate confound). Result: **0 out of 30 repetitions showed any GC collection during
apply or commit, at any batch size** - GC is conclusively ruled out, yet the bimodal pattern (median ~0,
max scaling with batch size) persisted unchanged.

**Most consistent explanation: Linux kernel dirty-page writeback throttling, not an LMDB-intrinsic cost.**
Under `NoSync`, `tx.Commit()` still issues real `write()` calls into the OS page cache (no fsync, but the write
itself still happens) - ordinarily fast, but larger batches dirty more pages, making it more likely to cross a
kernel dirty-page threshold and force the calling thread to block during writeback (`vm.dirty_ratio`-style
throttling) - a plausible, if not independently confirmed, match for "mostly instant, occasionally large,
scaling with batch size."

**Not confirmed as transferable to the real deployment, and treated with appropriate caution**: unlike the
reader-count result (which tests LMDB's own in-memory B+tree/MVCC logic, expected to be OS-independent), this
specific behavior is plausibly a Linux-kernel-specific disk-writeback artifact - Windows manages dirty
memory-mapped pages through an entirely different subsystem (Cache Manager/Memory Manager, different
thresholds). Extrapolating a sandbox-observed, possibly-Linux-specific pattern onto the real Windows deployment
without direct confirmation would repeat the same mistake as the earlier `MaxThreads`/ThreadPool and
`WriteMap`/RAM-size situations - sandbox benchmarking on this specific question was judged to have hit
diminishing returns.

**Decision: hold `MaxOperationsPerWave` at its current value for now.** Rather than act on a possibly
environment-specific sandbox result, wait for live data from much later in the same sync run, once the on-disk
dataset reaches roughly 50GB total - the point at which this experiment's central validation question (behavior
as the dataset grows past available RAM - see "Validation methodology" below) actually starts to bite. The
current lock-wait/commit-call/apply/begin phase breakdown and page-fault diagnostics are already in place and
will carry forward unchanged into that later-stage data.

**Added `ILmdbConfig.MaxOperationsPerWaveAccount`/`MaxOperationsPerWaveStorage`** (both default `0`, meaning
"fall back to the shared `MaxOperationsPerWave`") so Account and Storage can be tuned independently once there's
real data to tune against, without this being a code change at that point - purely additive prep work,
consistent with (not overriding) the "hold for now" decision above: the defaults preserve today's exact
behavior. Resolved via `LmdbDbFactoryDecorator.CreateColumnsDb` computing each column's effective cap before
constructing its `LmdbDb`. Covered by a new decorator-level test
(`CreateColumnsDb_resolves_per_column_MaxOperationsPerWave_overrides`) that drives a 50-op batch through each
column with different effective caps and asserts the smaller one chunks into multiple physical commits while the
larger one doesn't - proving the override reaches the right column end-to-end, not just that the config field
exists. `LmdbColumnsDbAdapter.Account`/`.Storage` widened from `private` to `internal` to make this directly
testable. All 58 pre-existing tests plus this one pass.

**Live experiment anyway (user's call, overriding the "hold" above): `MaxOperationsPerWaveAccount = 16384`
(4x the shared default) on a real mainnet sync.** Two samples, both windows well past Account's natural batch
size of ~3,880-4,096 ops/wave:

| Stage | Avg ops/wave | Apply per-op (us) | Commit-call per-op (us) | CreateSnapshot avg (us) |
|---|---|---|---|---|
| Baseline (20-37%, cap unset) | ~3,880 | - | ~2.546 | 217-261 |
| ~10% into this run | 14,062 | 1.676 | 2.527 | 465.5 |
| Later in the same run | 13,778 | 2.030 | 2.484 | 526.3 |

Commit-call cost per op stayed flat across both samples (~2.48-2.53us/op vs baseline ~2.55us/op, noise-level) -
the fixed-overhead-amortization hypothesis that motivated trying a bigger cap does not hold at this scale, at
either sample point. Apply-phase cost per op *rose* between the two samples (1.676 → 2.030us/op, +21%) as the
dataset grew, meaning the bigger batch isn't amortizing anything - it's just exposing more operations per wave
to a cost that is itself increasing with tree depth. Meanwhile `CreateSnapshot` average latency kept climbing
(465.5 → 526.3us), further above the 217-261us band seen at every baseline sample - the predicted cost of
longer individual commits holding `_writeCommitGate` for longer, and it is getting worse with scale rather than
leveling off.

**Decision: reverted.** No benefit materialized at two different points in the same run, and the one real cost
(CreateSnapshot tail latency) compounds with scale instead of flattening out - the opposite of what would be
needed to justify the trade-off. `MaxOperationsPerWaveAccount` is set back to `0` (shared default) in the live
config; the code default was already `0`, so no source change was needed. The per-column override mechanism
itself stays in place (it's still correct, generic prep work - see above), just unused for now. This closes out
the "wait for ~50GB-scale data" decision above with a negative result; `MaxOperationsPerWave` tuning is not
pursued further.

### Account partitioning - letting AppendData engage for every account-range partition, not just the top one

With `MaxOperationsPerWave` tuning closed out with a negative result, the next lever is the one originally
flagged alongside AppendData itself: snap sync's account-range phase splits the full Keccak-path keyspace into
`ISyncConfig.SnapSyncAccountRangePartitionCount` (default 8) fixed, contiguous, disjoint sub-ranges
(`ProgressTracker.SetupAccountRangePartition`), each delivered in its own byte-sorted order. Under a single flat
LMDB keyspace, `CanUseAppendData` correctly requires every appended key to exceed the database's *actual current
max key* - so only the single highest-numbered partition can ever advance that max; the other 7 are writing into
already-passed keyspace from the flat database's point of view and can never qualify, regardless of how
perfectly sorted their own individual stream is. That ceiling (~1/8 of account-range writes, confirmed via the
~20% AppendData engagement figure observed live) is structural, not a tuning problem.

**Implemented: `ILmdbConfig.EnableAccountPartitioning`** (default `false`, purely additive/opt-in). When on, the
Account `LmdbDb` opens one named LMDB database per partition (`"p0"` .. `"p{N-1}"`, sharing one environment/file)
instead of the single unnamed default database, with `N` taken directly from `ISyncConfig.SnapSyncAccountRangePartitionCount`
(wired through `LmdbDbFactoryDecorator`, which now takes `ISyncConfig` - never a separate, driftable knob: the
partition boundaries only matter *because* they match what `ProgressTracker` actually hands out). Storage was
initially left as a single flat database unconditionally here, on the assumption that "its keys are not
partitioned the same way" - **that assumption turned out to be wrong, and unchecked; see "Storage partitioning"
below**, added once live data made the gap impossible to ignore.

Key design points (`LmdbDb.cs`):
- `GetPartitionIndex(key, partitionCount)` mirrors `ProgressTracker`'s boundary math exactly: equal-sized ranges
  over the key's first 4 bytes (big-endian), any remainder from a non-power-of-two count folded into the last
  partition via a clamp. Pure function of the key's own bytes - correctness for ordinary Get/Put/Delete never
  depends on the mirroring being exact, only the AppendData engagement rate does.
- `ApplyOperations` groups a drained wave's operations by partition (a wave can span partitions, since it
  coalesces several concurrent callers) and runs the existing per-partition `CanUseAppendData` check
  independently against each partition's own database, inside the same physical transaction - still one atomic
  commit, just now potentially touching several named databases.
- `GetAll`/`GetAllKeys`/`GetAllValues`/`FirstKey`/`LastKey`/`EstimatedCount` iterate `_databases` in ascending
  order - no k-way merge needed, since partitions are already disjoint, contiguous, ascending-ordered ranges by
  construction (partition *i* is entirely below partition *i+1*).
- `GetViewBetween` (`ISortedKeyValueStore`, used by `FlatStoreBoundary`/history code) splits the requested
  `[firstKeyInclusive, lastKeyExclusive)` across whichever partitions it actually intersects and merges them via
  a new `LmdbMultiPartitionSortedView`, which just exhausts each intersecting partition's own `LmdbSortedView` in
  array order - again no merge-heap needed, for the same contiguous-ranges reason. Reduces to exactly the old
  single-partition behavior when only one partition intersects (always true in the non-partitioned case, and
  confirmed by the pre-existing `StartBefore_*`/`GetViewBetween` tests still passing unchanged). `StartBefore`
  (floor-seek) is the one case that needs real care in a multi-partition merge: a dedicated research pass
  (general-purpose Explore agent) confirmed **no production call site in this codebase exercises `StartBefore`
  against a partitioned column in practice** - every real `GetViewBetween` caller on Account/Storage (history,
  healing, proof code) only ever calls `MoveNext()` from the start, encoding its own resume position as the
  view's `firstKeyInclusive` instead; the only real `StartBefore` caller in the whole codebase is
  `LogIndexStorage`, an unrelated non-LMDB column. It is still implemented correctly rather than left as a known
  gap: the floor of a seek value can only live in the value's own partition or a strictly lower one (every
  higher partition's range is entirely greater than the value by construction), so the merge walks backward from
  the value's own partition only - trying a higher partition first would have been a genuine bug, since
  `LmdbSortedView.StartBefore` only checks its *own* clipped bounds once positioned, not the seek value itself,
  so a higher (empty-relative-to-value) partition could otherwise report a false floor that's actually greater
  than the seek value.
- **Not compatible with an existing non-partitioned `account.lmdb` directory** - partitioned mode looks for data
  in named databases a non-partitioned sync never wrote to, so existing data would silently read back empty with
  no error, the worst possible failure mode. Guarded directly: the constructor checks whether the default
  (non-partitioned) database already has entries before creating the named ones, and throws
  `InvalidOperationException` if so, rather than risk that silently. Caught one real bug while building this
  guard: throwing from partway through the constructor (after `LightningEnvironment` was already opened) leaked
  the native environment handle, which crashed the *entire test process* from its finalizer on the next GC - the
  exact `LightningEnvironment`/finalizer danger already documented for `BeginReadOnlyTransaction` above, just hit
  from a different angle. Fixed by wrapping the whole constructor body past `_env`'s creation in try/dispose.

Covered by a full pass of new tests (`LmdbDbTests.cs`): round-trip Get across partitions, `GetAll`/`FirstKey`/
`LastKey`/`EstimatedCount` merged correctly from deliberately out-of-order insertion, the actual
partition-independent-AppendData benefit this feature exists for, `GetViewBetween` iterating correctly across
and within a partition sub-range, the `StartBefore` cross-partition-floor case, the snapshot-pool path
(`LmdbDbSnapshot`), and both constructor-guard cases (throws on existing non-partitioned data, does not throw on
an empty/fresh directory) - plus a decorator-level test (`LmdbDbFactoryDecoratorTests.cs`) proving the partition
count is actually read from `ISyncConfig.SnapSyncAccountRangePartitionCount` end-to-end, not just that the
config flag exists. All 68 Nethermind.Db.Lmdb.Test tests pass (the one pre-existing, unrelated
`ThreadPool`-minimum failure is expected - see that test's own remarks).

**Not yet validated on a live sync** - the next step is a real run with `EnableAccountPartitioning = true` on a
fresh (not pre-existing) `account.lmdb` directory, watching whether AppendData engagement for Account rises
materially above the ~20%/~1-in-8-partitions ceiling observed before this change, and whether that translates
into a real commit-call-cost improvement rather than just a diagnostic-count change (the `MaxOperationsPerWave`
experiment above is a cautionary example of a metric moving without the underlying cost actually improving).

### Storage partitioning - the column that actually dominates write volume

**Live data exposed the gap in the "Storage stays unpartitioned" assumption above.** A log sample with
`EnableAccountPartitioning = true` showed Account's AppendData engagement at a clean 100% (548,796/548,796 puts)
- the feature working exactly as designed - but the user correctly observed the sync wasn't meaningfully faster
overall. The same sample showed why: Storage dominates write volume and got none of the benefit. At that point
Storage held 11,039,444 keys against Account's 4,118,977 (roughly 2.7x more), ran **12,208 physical write waves
against Account's 143 in the same 30s window (~85x more commits)**, and its AppendData engagement was stuck at
**8,056/2,261,265 puts (~0.36%)** - essentially off. Partitioning the smaller column while leaving the dominant
one untouched was never going to move the overall needle much, regardless of how well it worked for Account.

**Investigated (general-purpose Explore agent, read-only) whether Storage's keys actually support the same
trick, rather than assuming either way a second time.** Findings:
- Storage's on-disk key layout (`BaseFlatPersistence.EncodeStorageKey`, hashed mode) is
  `addressHash[0..4] || slotHash[0..32] || addressHash[4..20]` - 52 bytes total. The key's **first 4 bytes are
  literally the account hash's own top 4 bytes** - the exact same bytes `LmdbDb.GetPartitionIndex` already
  groups by for Account. So partitioning Storage by its key's first 4 bytes, using the same boundaries, routes
  every slot of a given account into exactly the same partition as that account's own Phase 1 write - no
  scattering, no new partition math needed.
- Tracing the actual write path confirmed the ordering property carries over too: `FlatSnapTrieFactory` creates
  one snap tree/write batch per account per storage response (`"a snap tree is created once per account per
  storage response"`), so each account's slots are committed independently, in the ascending order
  `SnapProviderHelper.BuildAndVerifyRoot` already validates before any write happens. `ProgressTracker.StoragesToRetrieve`
  (the shared queue feeding storage-range requests) does interleave accounts from all 8 account-range partitions
  at the global-queue level - but since Storage's partition boundaries exactly mirror Account's, a given storage
  partition can only ever receive accounts that originated from the one matching account-range-partition worker,
  and that worker's own sub-stream is strictly ascending. Same structural property Account's fix relies on,
  same caveats (coalesced waves, healing-phase patches, out-of-request-order concurrent responses - see
  `CanUseAppendData`'s own docstring) and same expected ceiling shape.

**Implemented: `ILmdbConfig.EnableStoragePartitioning`** (default `false`, independent toggle from
`EnableAccountPartitioning`). Uses the **same** `ISyncConfig.SnapSyncAccountRangePartitionCount` partition count
as Account - not an independently configurable count, since the two must derive from identical boundaries for
the key-grouping property above to hold at all. Reuses every piece of `LmdbDb`'s partition machinery unchanged -
`GetPartitionIndex`, `GetIntersectingPartitions`, `LmdbMultiPartitionSortedView`, the constructor's
already-partitioned-data guard - **zero new logic in `LmdbDb.cs` itself**; this was purely a wiring change in
`ILmdbConfig.cs`, `LmdbConfig.cs`, and `LmdbDbFactoryDecorator.CreateColumnsDb` (which now computes
`storagePartitionCount` the same way it already computed `accountPartitionCount`). Carries the identical
"not compatible with an existing non-partitioned `storage.lmdb` directory" constraint and constructor guard as
Account partitioning, for the same reason.

Covered by two new decorator-level tests in `LmdbDbFactoryDecoratorTests.cs`:
`CreateColumnsDb_wires_EnableAccountPartitioning_from_SyncConfigs_partition_count_independently_of_Storage`
(renamed from the original Account-only test to make the independence explicit) and
`CreateColumnsDb_wires_EnableStoragePartitioning_from_the_same_SyncConfig_partition_count_as_Account` (both
flags on, asserting Storage's lower-partition-after-higher-partition AppendData engagement the same way the
Account test does, plus a round-trip `Get` check). No changes needed to the partition-mechanics tests in
`LmdbDbTests.cs` - those already exercise `LmdbDb` generically by partition count, independent of which column
calls it.

**Not yet validated on a live sync**, same caveat as Account partitioning above - the next step is a run with
both `EnableAccountPartitioning` and `EnableStoragePartitioning` set on a fresh (non-pre-existing) pair of
`account.lmdb`/`storage.lmdb` directories, watching whether Storage's AppendData engagement rises materially
above the ~0.36% ceiling observed before this change, and - as always - whether that translates into an actual
per-op commit-cost improvement rather than just a diagnostic-count change.

**Live data from a run with both flags on: Account held at a clean 100% engagement as before, and Storage's
engagement rose sharply to ~22.6% (529,910/2,344,790 puts)** - a real, large jump from ~0.36%, confirming Storage
partitioning is genuinely engaging. But per-op commit-call cost showed no improvement at all (8.467us/op vs a
prior 8.317us/op baseline at a *larger* DB size, where smaller should mean cheaper if anything), and apply-phase
per-op cost looked *worse* (2.480us/op vs 1.954us/op) - the opposite of AppendData's expected effect. This result
on its own would suggest AppendData simply isn't paying off live despite clearly engaging mechanically.

### Why Storage only reaches ~22.6% engagement, not ~100% like Account

**Investigated (general-purpose Explore agent) and confirmed, not just plausible: storage-range response
completion order is NOT tied to account-hash discovery order, unlike Account's strictly sequential partitions.**
`SimpleDispatcher` fires every storage-range request as an independent `Task.Run`, bounded only by
`2 x MaxProcessingThreads` in-flight via semaphores - nothing serializes responses by issue order, and each
account's storage is committed as its own independent write batch the instant its own response arrives
(`SnapProvider.AddStorageRange` / `FlatSnapTrieFactory` - "a snap tree is created once per account per storage
response"). Since up to 1,200 accounts are batched per request and only the *last* account in a response can be
incomplete (needing a prioritized single-account continuation via `EnqueueNextSlot`), most accounts resolve in
one shot - but a small-storage account enqueued after a large one (same partition, same worker) can easily have
its single response land and get written *before* the large account's multi-round-trip storage finishes, since
nothing orders completion by discovery order. `CanUseAppendData`'s own docstring already names this exact
failure mode ("two concurrent range responses completing out of request order") as something it must defend
against. **Verdict: this is a structural property of the snap-sync storage dispatch design, not a bug** - ~22.6%
is closer to the real ceiling for Storage than ~100%, given the design has no ordering barrier anywhere.

### The `lmdb-spike` benchmark re-run, properly fixed - and a deeper discrepancy it exposed

The user noticed the live numbers above didn't remotely match the original `lmdb-spike` benchmark's ~128x
reported "speedup" for AppendData, and asked for the benchmark to be re-validated after fixing the bug found
earlier (scenario D's keys were smaller than the pre-populated DB's real max, so LMDB's `tx.Put(..., AppendData)`
was correctly returning `MDBResultCode.KeyExist` and inserting nothing for virtually all of D's 320,000 attempted
"appends" - confirmed directly via an isolated repro: a single out-of-order append after an in-range key returns
`KeyExist`, not corruption, and a second, mid-sequence out-of-order append within the same transaction is also
caught, refuting the "silently corrupts" framing from this doc's earlier spike notes for this specific call path).

**Fixed benchmark** (`scratchpad/lmdb-spike-fixed`): scenario D's keys use a 24-byte all-`0xFF` prefix (so they
deterministically exceed the pre-populated random data's real max) plus an ascending 8-byte counter, with return
codes checked and a final entry-count readback - both of which the original benchmark never did, which is how
the bug went unnoticed. Confirmed working: 320,000/320,000 (100%) real append successes, final entry count
exactly matching expectations.

**With the bug fixed, the benchmark's core claim holds up - and gets stronger under further isolation, not
weaker:**

| Variant | Batch size | Regular Put | AppendData | Ratio |
|---|---|---|---|---|
| Original re-run, fsync, 0 readers | 40,000 | 12.1us/op | 0.45us/op | 27x |
| NoSync, 0 readers | 40,000 | 6.5us/op | 0.24us/op | 27x |
| NoSync, 0 readers | 4,096 (~Account's live wave size) | 9.20us/op | 0.28us/op | 32x |
| NoSync, 0 readers | 200 (~Storage's live wave size) | 10.78us/op | 0.42us/op | 26x |
| NoSync, 256 pinned readers (worst-case reclaim block) | 4,096 | 4.22us/op | 0.12us/op | 34x |
| NoSync, 256 pinned readers | 200 | 40.81us/op | 1.65us/op | 25x |

Systematically ruled out, in order, as the explanation for the live/benchmark gap:
- **Durability (fsync vs `NoSync`)** - ratio unchanged (~27x either way); live already uses `NoSync` via
  `WriteFlags.DisableWAL`, so this was never the live path's bottleneck to begin with.
- **Batch size amortization** - ratio stays large (25-34x) all the way down to 200 ops/transaction, matching
  Storage's actual live wave size; a tiny batch does not erode the advantage the way "repeated-search avoidance
  needs many repetitions to pay off" would predict.
- **Reader-table pinning / blocked page reclaim** - holding 256 read-only transactions open for the entire run
  (worst case: nothing written during the timed phases can ever be reclaimed, matching live's
  `AlivePooledReaders` count) made *both* scenarios slower in absolute terms, but left the ratio essentially
  unchanged (24.8-34.1x) - reclaim blocking is not what's erasing append's relative advantage live.
- **Multi-partition structure** (`scratchpad/lmdb-spike-multipartition`) - the one remaining structural
  difference: Account/Storage now split a wave across up to 8 separate named databases in one transaction,
  something no single-DB benchmark variant above tested. Built a dedicated benchmark mirroring
  `LmdbDb.ApplyOperations`' actual per-partition grouping (8 named DBs, each pre-populated with its own
  key-range-scoped random data, each wave split evenly across all 8 and committed in one transaction). Result:
  the ratio **does** shrink substantially here - **19.8x** at 512 ops/partition (wave size 4,096) down to **7.3x**
  at just 25 ops/partition (wave size 200) - confirming partition dilution is real. But even 7.3x is nowhere
  close to explaining the ~0x observed live, so this narrows the gap without closing it.

**Conclusion: every live "before vs after" comparison made so far has been confounded, not necessarily wrong
about AppendData itself.** AppendData engagement is fully automatic (self-verifying, not independently
toggleable), so every live measurement to date has compared *different sync runs at different database sizes* -
exactly the kind of comparison this doc's own `MaxOperationsPerWave` experiment already showed can hide a real
effect behind unrelated scale-driven noise. Given how robust the controlled advantage is (25-34x survives
durability, batch size, and reader-pinning; only genuinely shrinks under the one untested variable, partition
count, and even then not to zero), the most likely explanation is that live cross-run comparisons simply cannot
resolve an effect this real system is actually getting, not that the effect is absent.

### Per-operation live diagnostics split - getting a true in-situ signal without a toggle or a second sync run

Rather than add a force-disable-AppendData config knob (which would need a whole second sync run to compare
against, re-introducing the same database-size confound this investigation just diagnosed), added direct
per-operation instrumentation to `LmdbDb.ApplyOperationsToDatabase`: apply-phase time and operation count are now
split into two independent buckets, `appended` and `non-appended`, based on that specific partition-group's own
`canAppend` result - no toggle needed, because a single live wave already routinely contains both kinds of
partition-groups side by side (certain for Storage at ~22.6% engagement; happens for Account too whenever a
partition's own stream breaks ascending order, e.g. during healing).

New fields on `DiagnosticsSnapshot`: `AppendedApplyOpsCount`/`AvgAppendedApplyMicrosPerOp` and
`NonAppendedApplyOpsCount`/`AvgNonAppendedApplyMicrosPerOp`, surfaced in the periodic diagnostics line
(`LmdbColumnsDbAdapter.FormatDiagnostics`) as `(apply: appended X.XXXus/op xN, non-appended X.XXXus/op xN)`. A
failed partition-group's partial timing is deliberately not recorded (a `MapFull` retry re-runs the whole wave
from scratch, so a partial timing would misattribute cost to neither bucket faithfully). Covered by a new test
(`Diagnostics_split_apply_time_by_appended_vs_non_appended_per_operation`) asserting a fully-ascending batch into
an empty DB attributes 100% to the appended bucket and a fully out-of-order batch attributes 100% to the
non-appended bucket. All 70 Nethermind.Db.Lmdb.Test tests pass (the one pre-existing `ThreadPool`-minimum
failure is expected).

**Not yet observed live** - the next step is simply watching the next few periodic diagnostics lines from the
currently-running (or next) sync: since Storage already has a natural mix of appended/non-appended groups within
single waves at its current ~22.6% engagement, this should produce a real, same-run, same-database-size A/B
comparison from the very next log line, with no restart or config change required.

### Live diagnostics observed - and the real root cause: AppendData's benefit is a whole-transaction property

First live sample with the new per-op split (~90% state ranges synced, Account 41.1M keys/1.90 GiB, Storage
196.3M keys/26.61 GiB):

```
Account: WriteWave x81 (avg 10895.6us = begin 3.0us + apply 3916.9us + commit 6975.5us [lock-wait 0.8us + call 6974.7us], avg 3810 ops/wave)
         AppendData 308,584/308,584 puts (apply: appended 0.994us/op x308,584, non-appended 0.000us/op x0)
Storage: WriteWave x9,667 (avg 2973.8us = begin 5.3us + apply 881.1us + commit 2087.2us [lock-wait 82.3us + call 2004.9us], avg 266 ops/wave)
         AppendData 294,579/2,574,223 puts (apply: appended 1.449us/op x294,579, non-appended 3.467us/op x2,279,644)
```

Two things stood out immediately:

1. **AppendData's apply-phase benefit is real but small**: 1.449us/op vs 3.467us/op for Storage, a genuine 2.39x
   measured in the same run, same DB size, same moment - the first clean same-run A/B this investigation
   produced. But apply is a minority of wave time (29.6% for Storage, 36.0% for Account) - **commit** dominates
   both (67.4% and 64.0% respectively), and commit cost is not attributed per append/non-append group by this
   diagnostic (it is one physical call spanning every partition's operations together).
2. **Commit cost per op differs sharply by column**: Account 1.83us/op (6974.7us / 3810 ops), Storage 7.54us/op
   (2004.9us / 266 ops) - a 4x gap that the per-op apply split cannot explain, since it only instruments apply.

Two candidate explanations for the commit-cost gap, which these two live data points (different DBs, different
sizes) cannot distinguish: (a) Storage's waves are tiny relative to its own 4,096 cap (`DrainPendingWrites` drains
greedily the instant it becomes leader - it never waits to accumulate more), so it pays a largely fixed
per-transaction commit cost far more often per op than Account's near-cap waves; or (b) Storage's DB is simply 14x
bigger and deeper, making every commit's copy-on-write work intrinsically costlier per op regardless of wave size.

**Built `lmdb-spike-commitcost`** (8 partitioned DBIs, 10M pre-populated 52+32-byte entries ≈ 1.55 GiB - sized for
a real multi-level B+tree, not live's full 26 GiB scale, which isn't needed to see the effect) to isolate these:

*Phase 1 - sweep wave size on a fixed DB, random `Put` only.* Median commit-call cost per op (GC-pause-filtered;
raw means were swung 5-10x by rare tail stalls unrelated to LMDB's own cost) came out **flat at ~6-7us/op from
264 to 4,096 ops/wave** - directly refuting hypothesis (a). (p90 did grow sharply at larger waves - 146us/op at
2,048 vs 7.5us/op at 512 - so bigger waves add tail-latency risk without reducing the typical cost.) That flat
~6-7us/op matches live Storage's 7.54us/op far more closely than live Account's 1.83us/op, on a similarly-sized
DB (1.55 GiB vs Account's 1.9 GiB) - pointing at the one structural difference Phase 1 didn't test: Account runs
near-100% AppendData, Phase 1 only tested random Put.

*Phase 2 - same fixed DB, three scenarios per wave size: `A` (random Put), `D` (pure per-partition AppendData),
and `M` (mixed - 1 of 8 partitions appended, 7 random, matching live Storage's ~11-22% engagement, since
`CanUseAppendData`/`ApplyOperations` make the append decision per-partition-group, all-or-nothing, never
partially within one partition's own batch):

| wave ops | A (random) median us/op | D (pure append) median us/op | M (1/8 appended) median us/op | A/D ratio | A/M ratio |
|---|---|---|---|---|---|
| 264 | 6.636 | 0.265 | 5.856 | 25.1x | 1.13x |
| 512 | 5.910 | 0.238 | 8.328 | 24.8x | 0.71x |
| 1,024 | 7.706 | 0.136 | 7.605 | 56.7x | 1.01x |
| 2,048 | 6.558 | 0.071 | 5.278 | 92.3x | 1.24x |
| 4,096 | 11.156 | 0.132 | 12.449 | 84.5x | 0.90x |

Pure AppendData's commit cost is **17-92x cheaper** than random Put, confirmed directly on the same DB, same wave
sizes - a huge, real effect. But the **mixed scenario is statistically indistinguishable from pure random** at
every wave size (A/M ratios all within ~0.7-1.3x, i.e. noise) despite 12.5% of its ops being appended.

**Root cause, now confirmed rather than hypothesized**: LMDB's physical commit cost is governed by the union of
distinct dirty pages touched by *every* operation in the transaction, not by any individual operation's own cost.
A sequential append only ever touches the rightmost path of its own partition's tree (the same few pages,
repeatedly); a random insert's position is spread uniformly across the whole key range, each one dirtying its own
distinct leaf and ancestor pages. Since `LmdbDb` commits all partitions together in **one physical transaction**
per wave, even a single non-appended partition's random inserts dominate that transaction's total dirty-page
footprint and erase essentially all of the commit-side benefit the appended partitions would have earned in
isolation. AppendData's commit-cost win is an **all-or-nothing, whole-transaction property** - not a per-op one,
and not something "% of ops appended" measures at all.

This resolves the original discrepancy end to end: Account's cheap commits (1.83us/op) are a product of its
waves being close to 100% appended *within the same transaction*; Storage's partitioning raised the fraction of
individually-appendable ops (0.36% → ~11-22%) without ever getting close to whole-transaction purity, because
snap sync's storage responses arrive with no ordering guarantee relative to account-hash order (confirmed
earlier via `SimpleDispatcher`/per-account independent write-batch research) - so *some* partition in nearly
every wave still carries random inserts, and that's enough to erase the win. Raising "ops appended %" was
measuring the wrong thing; what would actually move Storage's live numbers is whether *whole transactions*
can be made append-pure, which the keyspace-partitioning approach alone cannot achieve given this arrival pattern.

**Not yet decided**: whether to pursue (i) committing each partition-group in its own separate physical
transaction, so an appended partition's cheap commit isn't dragged down by a sibling random one (at the cost of
up to 8x more physical commits per wave, each paying its own lock/writer-gate overhead); (ii) buffering/sorting
Storage writes before commit to manufacture whole-transaction purity (bigger architectural change, adds latency
and memory); or (iii) treating Storage's AppendData/partitioning angle as a dead end given this arrival pattern,
and looking elsewhere for the real lever. All three are live options, not yet chosen.

**Buffer/sort investigated further - the cheap version is a dead end.** The obvious cheap form of option (ii) -
sort each wave's keys ascending before applying, without otherwise changing anything - was tested directly
(interleaved A/S comparison, same growing DB, 2,048 ops/wave, 150 reps/arm, GC-pause-filtered to avoid the same
mean-distorting tail stalls noted above): median commit cost was statistically identical, 5.082us/op (random)
vs 5.149us/op (sorted), ratio 0.99x. **Sorting alone, without actually qualifying for the real `AppendData` fast
path, buys nothing.** This makes sense in hindsight: plain `Put` always performs a full B-tree search for the
insertion point regardless of key order - LMDB's commit-cost saving comes specifically from the append fast path
proving a key exceeds the cached rightmost position and skipping that search, not from any cache-locality effect
sorting alone could provide at this key density (48 random bytes per key - sorted neighbors in one wave are still
nowhere near each other in the existing 10M-entry keyspace).

(An earlier pass of this same sort-before-apply test, run non-interleaved - all of scenario A's reps, then D's,
then M's, then S's, within each wave size, sweeping wave size afterward - showed M and S costs collapsing to
near-zero but only at the single largest wave size tested, while A stayed normal. That was a methodology
artifact (every rep in that version permanently commits real data to one never-reset, ever-growing DB across the
whole sweep, so later scenarios/sizes aren't comparable to earlier ones) and was rejected rather than reported -
flagged here as a reminder for any future benchmark built on this DB-reuse pattern.)

This means a genuine fix via buffering requires withholding a partition's writes until its *entire* snap-sync
range is complete (not just a bigger-than-usual wave) - the slot-hash-dominated key layout gives no smaller
streaming window a way to guarantee "beyond current max". That is a materially larger change than "sort a wave":
staging potentially several GiB per partition before any of it is visible or durable, no incremental commit
progress during that window, and new interaction with crash/resume semantics. Not yet decided whether this
trade-off is worth it versus options (i) or (iii) above.

**Option (i), per-partition-group separate transactions, tested directly - also a dead end, and worse than
today's behavior.** Same interleaved methodology, same fixed DB, same live-matching mix (1 of 8 partitions
appendable, 7 random), wave size 2,048, 150 reps/arm: committing each partition's group as its own separate
physical transaction (`Y`) was **1.75x slower** than today's single combined transaction (`X`), not faster -
median 8.847us/op vs 5.025us/op (ratio X/Y 0.57x). The one appendable partition's transaction does become cheap
in isolation, but that saving is tiny in absolute terms (256 ops at a fraction of a microsecond each), while the
7 non-appendable partitions still each pay full per-transaction commit cost individually, PLUS the benchmark now
pays 8x the transaction begin/commit boilerplate instead of 1x. The added overhead exceeds the isolated gain.

With sorting-without-real-append (no effect) and per-partition-group transactions (net loss) both ruled out by
direct measurement, the only remaining path to a real Storage win is genuine whole-partition buffering (option
(ii) at full scope, not the cheap version) or treating this angle as a dead end (option (iii)) and looking for a
different lever - e.g. the ~5-7us/op baseline random-Put commit cost itself (not yet explained - see the open
question in Phase 1 above about whether that's fixed-per-transaction overhead or DB-depth-driven COW cost), or
wave/lock overhead unrelated to AppendData entirely.

**Decision: reverted.** Three independent approaches to extract a real AppendData commit-cost benefit for
Storage - raising the ops-appended % via keyspace partitioning, sorting a wave before applying, and per-
partition-group separate transactions - were each implemented and directly measured, and none produced a real
win (the first was neutral live despite 100% apply-side confirmation of the underlying mechanism, the second was
statistically a no-op, the third was 1.75x worse). The only remaining option (whole-partition buffering) was
judged not worth its cost (staging several GiB per partition, delayed visibility, new crash/resume semantics)
for an as-yet-unquantified payoff. `EnableStoragePartitioning` has been removed from `ILmdbConfig`/`LmdbConfig`
entirely (not just left defaulted off) - `LmdbDbFactoryDecorator.CreateColumnsDb` now always constructs the
Storage `LmdbDb` with `partitionCount: 1`. The corresponding
`CreateColumnsDb_wires_EnableStoragePartitioning_from_the_same_SyncConfig_partition_count_as_Account` test was
removed; `LmdbDbTests.cs`'s general multi-partition tests are untouched since they exercise the partitioning
mechanism generically (still used by `EnableAccountPartitioning`, which stays - Account's near-100% live
engagement genuinely pays off, since its waves are close to whole-transaction append-pure). All 69
`Nethermind.Db.Lmdb.Test` tests pass (one pre-existing unrelated `ThreadPool`-minimum failure, same as before).

**Action needed on the live node**: if `EnableStoragePartitioning=true` is set in whatever config/command-line
the currently-running (or next-launched) node uses, that setting now does nothing (the property no longer
exists) - remove it from the launch config. The `storage.lmdb` directory written under the old partitioned
layout is a different on-disk shape (named per-partition databases) than the now-reverted single-flat-keyspace
layout `LmdbDb` writes with `partitionCount: 1` - resyncing Storage from scratch (or wiping `storage.lmdb`) is
required after this change, the same constraint the partitioned-mode constructor guard already documents for the
reverse direction.

### Other levers considered for speeding up Storage writes, after dropping the AppendData/partitioning angle

With AppendData-based approaches exhausted, a menu of alternatives was drawn up:
(1) `EnvironmentOpenFlags.WriteMap`, (2) separate LMDB environments per partition for true concurrent commits
(distinct from the already-rejected same-environment per-partition-transactions idea), (3) directly explaining
the unexplained ~5-7us/op baseline random-Put commit cost (fixed-per-transaction vs DB-depth-driven), (4)
re-running the wave-size sweep at closer-to-live (26 GiB) scale, and (5) naming the likely architecture mismatch
(LMDB's copy-on-write B-tree is a poor fit for Storage's near-random high-volume write pattern - the workload
LSM-trees are specifically designed for).

**Option 1, `WriteMap`, tested - another dead end, and a near-miss false positive.** Built `lmdb-spike-writemap`:
three environments (Base = today's prod flags; WriteMap; WriteMap+MapAsync), each pre-populated identically
(same seed, 10M entries, ~1.55 GiB), compared via the same interleaved methodology. **First pass, timing only the
`Commit()` call as every earlier benchmark in this investigation had**, showed WriteMap at a stunning 111x for
random Put and 25x for AppendData. This did not survive scrutiny: `WriteMap` makes `Put` write directly into the
memory map, so the page-dirtying work Base defers to `Commit` (copy dirty pages to a scratch buffer, write them
out) happens **during `Put` instead** - timing only `Commit` was measuring a phase shift, not a real saving.
Re-measured as **total apply+commit wall time** (the only fair comparison): WriteMap's apply time rose from
3.588us/op to 12.031us/op while commit fell from 6.115us/op to 0.081us/op, netting out to **~1.2x slower
overall** (TOTAL median 10.000us/op base vs 12.110us/op WriteMap, ratio 0.83x) for random Put, and a statistical
wash for AppendData (0.98-0.99x). `MapAsync` on top of `WriteMap` changed nothing further. Also checked and ruled
out as a separate concern: WriteMap's `data.mdb` immediately reports the full configured `MapSize` as its file
length (e.g. 32 GiB here) because the mmap requires the file ftruncated to that length up front - but `du`
confirmed the file stays sparse (3.9 GiB of real blocks used, identical to Base), so this is not a real disk-space
cost, only a caveat for `LmdbDb.GatherMetric()`'s "GiB on disk" diagnostic, which reads `FileInfo.Length` and
would need a block-count-based measure instead if WriteMap were ever adopted. **This is now flagged as a general
lesson for any future LMDB benchmark in this investigation: always measure total apply+commit wall time, never
just one phase in isolation, since a flag or technique can trivially move cost between phases without changing
the total.**

**Option 2, separate LMDB environments per partition, tested - the first genuine win in this entire
investigation.** Unlike the rejected "per-partition-group transactions" idea (which kept all 8 partitions in one
shared environment, so they still serialized on that environment's single writer lock - pure overhead, no
concurrency), this uses 8 fully separate `LightningEnvironment` instances (own file, own writer lock each), with
each partition's commit run concurrently on its own thread (`Parallel.For`). Built `lmdb-spike-multienv`: same
live-matching mix (1 of 8 appendable, 7 random), wave size 2,048, 150 reps/arm, same interleaved methodology and
GC-pause filtering as every other benchmark in this investigation. Four arms to cleanly separate "smaller
per-environment tree" from "real concurrency":

| arm | description | median us/op | p90 us/op |
|---|---|---|---|
| X | today's prod: 1 combined transaction, 1 shared environment | 14.363 | 56.317 |
| Y | 8 sequential transactions, 1 shared environment (already-rejected reference) | 14.964 | 46.487 |
| W | 8 sequential transactions, 8 SEPARATE (smaller) environments | 13.029 | 52.675 |
| Z | 8 PARALLEL transactions, 8 separate environments | 3.493 | 13.282 |

**X/W ratio: 1.10x** - each separate environment is smaller (0.39 GiB vs the shared environment's 1.55 GiB,
since it only holds its own partition's share), but that alone buys almost nothing run sequentially. **W/Z ratio:
3.73x** - the SAME 8 environments, same data, only difference is running their commits concurrently instead of
sequentially - this isolates the real effect as genuine parallelism, not DB size. **X/Z ratio: 4.11x median,
4.24x p90** - consistent across percentiles, not a tail artifact. This makes sense given everything already
confirmed in this investigation: `NoSync` already skips the fsync-equivalent cost, so the remaining per-commit
cost is CPU-bound (page-copy/tree-traversal work), which real OS-thread parallelism across separate environments
(no shared writer lock) can actually overlap - unlike the shared-environment case, where LMDB's one-writer-at-a-
time design serializes everything regardless of how the work is split into transactions.

**Implemented and deployed for a live testnet sync test** (2026-10-13) - see "`EnableStorageMultiEnvironment` -
implementation" below for the full writeup, including the correctness discussion that follows.

**Not yet decided for production use** - this was a materially bigger change than anything else tried in this
investigation (including the already-reverted Storage partitioning), because it changes two things `LmdbDb`
currently guarantees, not just where data lives:

1. **Cross-partition write atomicity is lost.** Today, one wave is one physical transaction spanning every
   partition - all-or-nothing. With separate environments, a wave becomes N independent commits; a crash between
   two of them could leave some partitions updated and others not. This may be an acceptable trade-off (snap
   sync's writers already tolerate partial application on failure - see `CommitLargeWriteInChunks`'s remarks on
   `DisableWAL`/re-fetch-and-redo), but it is a genuine new correctness property to verify, not assume.
2. **Cross-partition read consistency changes.** A single read-only transaction spanning two partitions (e.g.
   `GetViewBetween`/`StartBefore` crossing a partition boundary) currently sees one consistent point-in-time view,
   since it's the same environment/transaction. With separate environments, a cross-partition read needs multiple
   independent read transactions opened against different environments - a write landing between opening them
   could make different partitions reflect different points in time.

Also not yet measured: real per-environment overhead at production `MaxReaders` (8192, vs this benchmark's 512)
multiplied across 8 (or `SnapSyncAccountRangePartitionCount`) separate environments' own reader lock tables, and
whether the `DrainIfLeader`/single-writer-gate coordination model generalizes cleanly to N independent per-
environment writers or needs a real redesign. This is a design decision for the user, not something to implement
unilaterally given the correctness properties at stake.

**User's call on the correctness trade-off** (2026-10-13): confined to snap sync's bulk range-fill phase, losing
cross-partition write atomicity is a non-issue - "due to the nature of following a moving target, snap sync can
be easily restarted and sync to new pivot, even if node is shutdown gracefully, sync will not be restarted and
will start again after selection of new pivot." Correctness only becomes load-bearing "after sync, during node
processing, where it is needed to guarantee that post block processing commit is all or nothing - either have a
state of block N or N+1." Decision: implement the real multi-environment design (not the merge-at-`FinalizeSync`
design discussed above) and validate it directly with a live testnet sync, rather than building the merge step
or a write-ahead-log/two-phase-commit layer first. If the live run shows this is worth carrying into block
processing, that correctness work becomes a real next step then - not before.

### `EnableStorageMultiEnvironment` - implementation

Added as a new, fully isolated class, **`LmdbMultiEnvDb`** (`Nethermind.Db.Lmdb/LmdbMultiEnvDb.cs`), rather than
retrofitting multi-environment support into `LmdbDb` itself - `LmdbDb` has zero code changes beyond two
visibility changes (`GetPartitionIndex`/`GetIntersectingPartitions`: `private static` -> `internal static`, so
`LmdbMultiEnvDb` can reuse the exact same partition-boundary math) and implementing a new shared interface. This
keeps the already-validated Account path (named-database partitioning in one shared environment) completely
untouched, and makes the whole experiment trivially revertible if the live test doesn't pan out - delete
`LmdbMultiEnvDb.cs`/`ILmdbColumnStore.cs`, revert the two visibility changes, and the two `LmdbDbFactoryDecorator`/
`LmdbColumnsDbAdapter` call sites, exactly how `EnableStoragePartitioning` was cleanly reverted earlier.

**Design: a thin router over N ordinary `LmdbDb` children, not a parallel reimplementation.** `LmdbMultiEnvDb`
holds `LmdbDb[] _children`, one per partition, each an entirely ordinary `partitionCount: 1` `LmdbDb` in its own
subdirectory (`storage.lmdb/p0`, `storage.lmdb/p1`, ...). Every key-scoped operation (`Get`/`Set`/indexer) routes
to `_children[GetPartitionIndex(key, N)]` by direct delegation - no new logic. Multi-partition operations
(`GetAll`/`FirstKey`/`LastKey`/`GetViewBetween`/`CreateSnapshot`/`EstimatedCount`/`GatherMetric`) concatenate or
merge each child's own already-correct behavior in ascending partition order, mirroring `LmdbDb`'s own
multi-partition logic almost exactly (`MultiEnvSortedView` is structurally identical to `LmdbDb`'s own
`LmdbMultiPartitionSortedView`, including the same `StartBefore` floor-in-a-lower-partition reasoning - just
simpler, since each child view already owns its own transaction independently rather than needing to share one).

**Why this gets real concurrency for free, with almost no new code**: since a storage key's partition is fully
determined by its account hash's own top 4 bytes (confirmed earlier in this investigation), a single caller's
write batch - in practice, one account's whole storage write - lands in exactly one partition essentially always.
`LmdbMultiEnvWriteBatch.Dispose()` just submits that one group directly to its one child, no parallel dispatch
needed at the single-batch level. The real concurrency comes from *many different concurrent callers* (snap
sync's dispatcher writing many different accounts' storage at once) happening to land on *different* children at
the same time - each child is a complete, independent `LmdbDb` with its own `DrainIfLeader`/writer-gate/MapGrowth
machinery and, critically, its own `LightningEnvironment` and writer lock, so two children's writes can physically
commit on two different OS threads simultaneously with zero coordination between them - exactly the mechanism
`lmdb-spike-multienv` measured. (The rare case of one batch spanning multiple partitions is still handled -
dispatched in parallel via `Parallel.ForEach` - but is not where the throughput benefit comes from.)

**Shared interface**: `ILmdbColumnStore` (`IDb, ISortedKeyValueStore, IKeyValueStoreWithSnapshot`, plus
`TakeAndResetDiagnostics(): LmdbDb.DiagnosticsSnapshot`) lets `LmdbColumnsDbAdapter` stay agnostic to which
concrete type backs the Storage column. `LmdbDb` implements this interface's `TakeAndResetDiagnostics` via
**explicit** interface implementation specifically - `LmdbDb` is a public class but `DiagnosticsSnapshot` is
deliberately internal-only (a diagnostics surface, not public API), and explicit implementation is the only way
to satisfy an interface member without widening that return type to public. `LmdbColumnsDbAdapter` itself was
changed from `public` to `internal` in the process (nothing outside this assembly referenced it by name - it was
always returned to callers via the public `IColumnsDb<T>` interface).

**Diagnostics aggregation**: `LmdbMultiEnvDb.TakeAndResetDiagnostics()` reconstructs each child's own per-interval
totals from its `(count, average)` pair, sums them across all N children, and recomputes the combined averages -
a weighted aggregation, not a plain mean of the children's own averages, since different partitions can have very
different call volumes (covered by
`TakeAndResetDiagnostics_aggregates_a_weighted_average_across_children_not_a_plain_mean`). `LmdbColumnsDbAdapter`'s
existing periodic logger (`FormatDiagnostics`) needed zero changes - it already just consumes one
`DiagnosticsSnapshot`-shaped value regardless of which concrete type produced it.

**Testing**: new `LmdbMultiEnvDbTests.cs` mirrors `LmdbDbTests.cs`'s own partitioning tests almost exactly (Get
round-trip across partitions, ascending `GetAll`/`FirstKey`/`LastKey` merge, independent per-partition append
eligibility, `GetViewBetween`/`StartBefore` merge, snapshot merge, diagnostics weighting, cross-partition-batch
correctness, on-disk subdirectory layout) plus a new `LmdbDbFactoryDecoratorTests.cs` wiring test confirming
`EnableStorageMultiEnvironment` end-to-end from `ISyncConfig.SnapSyncAccountRangePartitionCount` through to real
separate subdirectories on disk. All 79 `Nethermind.Db.Lmdb.Test` tests pass (the one pre-existing unrelated
`ThreadPool`-minimum failure, same as always).

**Not yet validated live** - the next step is a real testnet snap sync with `EnableStorageMultiEnvironment=true`,
watching whether the live diagnostics line's Storage `WriteWave`/commit-cost numbers actually drop the way the
isolated benchmark predicted, and whether anything about running N times as many `LmdbDb` instances (reader-table
memory, file-handle count, `MapGrowth` behavior per child) surfaces a problem the benchmark's smaller scale
didn't. `storage.lmdb` from any prior run (flat, named-database-partitioned, or otherwise) must be wiped first -
this is a different on-disk shape again.

### First live testnet result (2026-10-13) - a regression, and a real gap in the synthetic benchmark

```
Storage: WriteWave x36,662 (avg 1504.9us = begin 4.8us + apply 263.6us + commit 1236.4us [lock-wait 37.3us + call 1199.2us], avg 60 ops/wave)
         MapGrowths 0, AlivePooledReaders 1,472
         AppendData 178,809/2,200,495 puts (apply: appended 3.318us/op x178,809, non-appended 4.366us/op x2,021,686)
```

Comparing TOTAL per-op wave cost (begin+apply+commit together - not commit alone, per the lesson learned from the
`WriteMap` benchmark's false-positive) against the pre-multi-env single-shared-environment baseline:

| | multi-env (now) | single shared env (before) |
|---|---|---|
| avg ops/wave | 60 | 266 |
| total us/wave ÷ ops/wave | **25.08us/op** | **11.18us/op** |
| commit call us/op | 19.99us/op | 7.54us/op |

**This is a regression, not an improvement** - overall per-op cost roughly doubled, commit-call cost alone nearly
tripled. Root cause: average wave size collapsed from 266 to 60 ops. `lmdb-spike-multienv` never modeled this -
it gave each of the 8 environments a *fixed, equal* share of one shared wave and measured parallel wall-clock
time for that fixed workload. Live traffic is different: many separate per-account write batches used to funnel
into **one shared queue**, where `DrainIfLeader` coalesces whichever concurrent callers happen to be queued at
the moment the leader starts draining into one bigger physical transaction (up to the 4,096 cap). Splitting that
same arrival stream across 8 independent per-environment queues *before* coalescing means each queue sees only
roughly 1/8th the concurrent traffic at any instant, so each one's own "drain whatever's queued right now" greedy
policy naturally produces much smaller waves. Since Phase 1 of this investigation already found per-op commit
cost is flat (at best) regardless of wave size rather than improving with bigger batches - i.e. a real,
largely-fixed per-transaction cost that doesn't amortize away - paying that cost far more often (more, smaller
transactions) outweighs whatever benefit the separate writer locks' parallelism provides.

A secondary, now-confirmed cost: `AlivePooledReaders` rose to 1,472 (from ~256-288) and `CreateSnapshot` call
count rose roughly 7x, because every logical snapshot now opens 8 separate reader pools (one per child
environment) instead of 1 - real additional overhead that was flagged as "not yet measured" when this was built,
now confirmed non-trivial.

**Not yet decided**: whether this is fixable (e.g. a bounded coalescing delay per child environment, so each one
accumulates more concurrent writers before draining - the same general idea as the earlier-rejected "buffer/sort"
direction, but aimed at wave size rather than key order) or whether this result means the multi-environment
direction, as implemented, is also a dead end for live traffic shapes despite the clean synthetic-benchmark win.

### More live samples (2026-10-13, same sync run) - wave size is the dominant lever, not AppendData engagement

Three more ~30s samples during the main sync phase, all consistent with the first regression (avg ops/wave stayed
in the 53-70 range, total/commit per-op cost stayed 1.7-2.7x worse than the single-shared-environment baseline,
even at one point - 203.5M keys/27.72 GiB - matched almost exactly to the baseline sample's own DB size, 196M
keys/26.61 GiB, ruling out "it's just a smaller DB" as the explanation).

Then a sample from a distinct **storage-only phase** (Account's own range sync already finished - its `WriteWave`
count is 0 in this sample) showed a sharp reversal:

```
Storage: WriteWave x437 (avg 39707.5us = begin 3.4us + apply 17151.9us + commit 22551.8us [lock-wait 0.8us + call 22551.0us], avg 4002 ops/wave)
         AppendData 0/1,744,662 puts (apply: appended 0.000us/op x0, non-appended 4.271us/op x1,744,662)
```

Average wave size jumped to 4,002 ops - right at the 4,096 cap, a ~60-75x jump from the 53-70 range every
mid-sync sample showed. Per-op cost: 9.92us/op total, 5.63us/op commit - **better than the single-shared-
environment baseline** (11.18us/op total, 7.54us/op commit), despite **0% AppendData engagement** in this sample
(vs the baseline's own 11-22%). This is strong evidence for the hypothesis already building across the mid-sync
samples: **wave size (coalescing), not AppendData engagement, is the dominant lever for Storage's write cost**.
It also cleanly explains the earlier regression: during the main sync phase, splitting one shared write-arrival
stream across 8 independent per-environment queues (each now seeing only ~1/8th the concurrent traffic at any
instant) fragments coalescing into much smaller waves; in this later, apparently more concentrated/sequential
phase (fewer concurrent writers, larger individual batches), each environment's own queue still coalesces well,
and the multi-environment design stops costing anything and even edges out the baseline.

**Decision: reverted** (2026-10-13). Across five live samples spanning most of a testnet sync - four during the
main, highly-concurrent phase (consistently 1.7-2.7x worse per-op cost than the single-shared-environment
baseline, one at matched DB size ruling out "smaller DB" as the explanation) and one from a later, more
sequential storage-only phase (where it briefly matched or beat the baseline, for reasons understood - see
above) - the net conclusion is that `EnableStorageMultiEnvironment` does not bring enough overall benefit to
justify its cost and complexity, particularly given the broad highly-concurrent phase it regresses in is also the
phase that dominates Storage's total write volume. `LmdbMultiEnvDb.cs` and `ILmdbColumnStore.cs` have been
deleted entirely (not left disabled-by-default) - `LmdbDb`'s two visibility changes
(`GetPartitionIndex`/`GetIntersectingPartitions`) were reverted back to `private static`, `LmdbColumnsDbAdapter`
reverted to concretely typing `storage` as `LmdbDb` (and back to `public`, since nothing outside this assembly
ever referenced it and the interface that required `internal` is gone), and `LmdbDbFactoryDecorator` always
constructs a plain `LmdbDb` for Storage again. The `LmdbMultiEnvDbTests.cs` file and the
`EnableStorageMultiEnvironment` wiring test were removed. All 69 `Nethermind.Db.Lmdb.Test` tests pass (the one
pre-existing unrelated `ThreadPool`-minimum failure, same as always) - back to the exact test count from before
this experiment began.

**Root cause, now understood clearly enough to act on directly**: the live regression traces to wave-size
fragmentation, not a flaw in the concurrency idea itself - splitting one shared write-arrival stream into N
independent per-environment queues *before* coalescing meant each queue saw a smaller slice of concurrent
traffic at any instant, and `LmdbDb`'s own `DrainIfLeader` greedily drains whatever is queued the instant it
becomes leader rather than waiting to accumulate more. This points at the real, more fundamental problem worth
fixing directly: **the current write-coordination model conflates "whichever caller happens to win a race becomes
the committer" with "when and how much to batch"**, with no real separation between the many concurrent
callers submitting writes and the one committer actually driving batching policy. See "Dedicated writer thread
redesign" below for the architecture now being built to address this directly, rather than working around it with
more parallelism.

Options 3-5 not yet investigated.

### Dedicated writer thread redesign

Replaces `LmdbDb`'s write-coordination model entirely (`_pendingWrites`/`_writerActive`/`DrainIfLeader`), for both
Account and Storage - this is core `LmdbDb` machinery, not a column-specific flag, since the multi-environment
experiment's own live data pointed at a problem in the fundamental coordination design, not at concurrency or any
column-specific property.

**What was wrong with the old design.** Every call to `SubmitWrite` raced to become "leader" via
`Interlocked.CompareExchange`; whichever caller won synchronously drained and committed the queue on *its own*
thread, then released the leader role. Two real costs: (1) the actual commit work ran on an arbitrary caller's
thread rather than a thread purpose-built for it, and (2) every transition from an empty queue back to non-empty
required a fresh compare-and-swap race among whichever callers happened to be submitting at that instant - one of
them could win and start draining before several near-simultaneous *other* callers had finished enqueuing,
producing smaller waves than the actual concurrent write pressure would otherwise support. This is a plausible
structural contributor to the small-wave problem observed throughout this investigation (Storage's live waves
averaging 53-266 ops against a 4,096 cap), independent of - and more fundamental than - the multi-environment
experiment that exposed it.

**New design**: one dedicated, persistent background `Thread` per `LmdbDb` instance (`WriterThreadLoop`), started
in the constructor once setup fully succeeds and joined in `Dispose()`. `SubmitWrite` is unchanged from the
caller's perspective (enqueue, signal, block on `ManualResetEventSlim.Wait()`) - only the draining side changes:
a `ManualResetEventSlim` (`_pendingWritesAvailable`) replaces the old `_writerActive` CAS flag, **reset before
draining, not after waiting** - the same safe pattern `DrainIfLeader`'s own old remarks described for its
equivalent race, now simpler since there is only ever one consumer: a write enqueued (and `Set()`) at any point
between a reset and the next `Wait()` call, including mid-drain, guarantees that `Wait()` returns immediately
rather than blocking, so no write can ever be stranded. `DrainPendingWrites`/`CommitLargeWriteInChunks`'s own
coalescing algorithm and oversized-write chunking are **unchanged** - only who calls them, and when, changes.

**Why this should recover bigger waves without an artificial delay**: a single persistent thread gets the classic
"group commit" self-regulation for free. While the thread is busy committing one wave, every write submitted
during that commit simply accumulates in the queue for the next drain pass - a slower commit or a burst of
concurrent writers both naturally produce a bigger next wave, with no explicit coalescing-delay knob needed (an
idea floated but not yet built after the multi-environment regression). It also removes the old design's
per-wave leader-election overhead (a `CompareExchange` for every empty-to-non-empty transition, contending on a
shared cache line at high concurrency) entirely, since there is no leader to elect any more.

**On "sorting"**: deliberately not added as a blanket pre-commit step, despite being part of the original ask.
This investigation already measured sorting a wave's keys before applying plain `Put` (no `AppendData`) and found
it provides **zero** benefit (interleaved A/S comparison, ratio 0.99x - see "Storage partitioning"'s "buffer/sort"
section above) - LMDB's `Put` always performs a full B-tree search for the insertion point regardless of key
order; the saving comes specifically from `AppendData`'s fast path proving a key exceeds the cached rightmost
position, which sorting alone cannot manufacture. Since `CanUseAppendData` already checks for ascending order
directly against the operations as submitted (and most real batches - one account's own storage slots, or one
account-range response - already arrive pre-sorted from the snap protocol), an unconditional sort would add CPU
cost to every wave for no measured benefit. If a concrete reason to sort ever surfaces (not yet identified), it
is a small, isolated addition to `ApplyOperationsToDatabase`, not a reason to withhold the rest of this redesign.

**Testing**: all existing concurrency/stress tests (`Concurrent_point_writes_from_multiple_threads_all_succeed`,
`Concurrent_write_batches_from_multiple_threads_all_succeed`, `Stress_many_concurrent_mixed_writers_are_all_committed_and_visible`,
the `CreateSnapshot`/snapshot-pool tests) pass unchanged against the new design - confirming it is a drop-in
replacement with no observable behavior change beyond the coordination mechanism itself. Added
`A_single_dedicated_writer_thread_is_started_on_construction_and_joined_cleanly_on_dispose`, backed by two new
test-only accessors (`LmdbDb.IsWriterThreadAlive`, `LmdbDb.WriterThreadName`), to lock in the one genuinely new
behavior: a real, named, alive background thread exists per instance from construction, and `Dispose()` joins it
rather than merely signaling it or leaving it running. All 70 `Nethermind.Db.Lmdb.Test` tests pass (69 succeeded,
the one pre-existing unrelated `ThreadPool`-minimum failure), stable across repeated runs.

**Validated live (2026-10-14 to 2026-10-19 testnet sync) - a real, confirmed win.** Three samples across the
sync:

| sync point | Storage avg ops/wave | total us/op | commit call us/op |
|---|---|---|---|
| ~18% (35M Storage keys) | 278 | - | - |
| end of phase 1/bulk sync (280M keys) | **4,010** (near the 4,096 cap) | **8.834** | **5.517** |
| post-sync trickle phase (282M keys) | 1 | - | - |

The 18% sample looked unremarkable (roughly matching the old pre-redesign baseline's 266 ops/wave), but by the
end of the bulk-sync phase - where write pressure is highest - wave size grew to nearly the configured cap,
confirming the "group commit" self-regulation hypothesis directly: no artificial coalescing delay was needed,
sustained concurrent write pressure alone was enough to let the single dedicated thread's natural batching
recover large waves. Against the pre-redesign baseline (266 ops/wave, 11.18us/op total, 7.54us/op commit), this
is a real ~21% improvement in total per-op cost and ~27% in commit cost - a genuine win, though far short of the
~4x the (ultimately-reverted) multi-environment synthetic benchmark had promised. The final, near-zero-volume
trickle-phase sample (avg 1 op/wave) is expected tail-of-sync behavior, not a regression - there is no concurrent
write pressure left to coalesce at that point, so it says nothing about the redesign's effectiveness during the
phase that actually matters.

### `mdb_stat`/`mdb_env_stat` live diagnostics - and a `PageSizeBytes` knob to act on them

Added live, periodic page-level stats reporting, logged every 30s alongside the existing diagnostics line as
`Lmdb page stats (mdb_stat) - Account: ..., Storage: ...`, plus a new `ILmdbConfig.PageSizeBytes` override, so a
page size decision can be made directly from real sync data and then tested without a second round of code
changes.

**`mdb_env_stat` vs `mdb_stat`** (the isolated `lmdb-spike-cursorstat` benchmark's experiment used the latter):
`mdb_env_stat` takes only the environment handle - no transaction, no specific database - and reports on the
environment's **root** database. For a single unnamed database (Storage's current shape, and Account whenever
`EnableAccountPartitioning` is off), the root database *is* the actual data, so this reports real entries/page
counts directly. For a **partitioned** (named-database) environment, the root database instead holds the catalog
mapping partition names to their own sub-databases - `mdb_env_stat` would then report that tiny catalog's own
stats, not the real per-partition data. `LmdbDb.GetPerPartitionStats()` was added alongside it for that case
(aggregates `mdb_stat` across every named partition instead) - confirmed identical to `GetEnvironmentStats()` in
the single-database case (`GetPerPartitionStats_matches_GetEnvironmentStats_in_the_single_database_case`), **and**
confirmed to actually diverge correctly for a partitioned column
(`GetPerPartitionStats_sees_real_partitioned_data_that_GetEnvironmentStats_cannot`: a 4-partition, 1000-entry
database shows `GetEnvironmentStats().Entries == 4` - the catalog, one row per partition - against
`GetPerPartitionStats().Entries == 1000` - the real data). The periodic log line calls `GetPerPartitionStats()`
for **both** Account and Storage - not `GetEnvironmentStats()` - since it is strictly correct in both the
partitioned and non-partitioned cases with no extra cost (same transaction count; reduces to iterating a single
database when `_databases.Length == 1`), making it the right default regardless of whether
`EnableAccountPartitioning` happens to be on for a given run.

**`PageSizeBytes`**: `0` (default) leaves LMDB's own OS-page-size default in place - unchanged behavior. A
non-zero override is wired through `LmdbDb`'s constructor (`pageSizeBytes`, new trailing optional parameter,
default `0`, so no existing call site needed to change) into `EnvironmentConfiguration.PageSize`. Only takes
effect for a brand new environment directory - LMDB stores the page size persistently at creation time; behavior
when set against an *existing* directory is explicitly flagged as unverified in the config's own doc comment,
not assumed safe or unsafe either way. Confirmed working end-to-end
(`PageSizeBytes_override_is_honored_for_a_brand_new_environment`).

**Hit, and fixed, a real build-breaking namespace collision while wiring this in**: unqualified `Stats` (the
LightningDB type `GetEnvironmentStats`/`GetPerPartitionStats` return) resolved ambiguously against
`Nethermind.Network.Stats` - a real sub-namespace reachable transitively through this project's existing
dependency chain - producing `error CS0118: 'Stats' is a namespace but is used like a type`. Fixed by fully
qualifying as `global::LightningDB.Stats` everywhere in `LmdbDb.cs`/`LmdbColumnsDbAdapter.cs`; the test project
did not hit the same collision (likely a different combination of `using` directives in scope), so its own usages
stay as the shorter `LightningDB.Stats`.

**What to look for once live**: a low `avg entries/leaf-page` figure relative to how many of Storage's ~84-byte
entries should fit in a 4096-byte page (naively ~40-45) would suggest leaf pages are splitting well before
they're actually full - the kind of signal that would motivate trying a larger `PageSizeBytes`. The isolated
`lmdb-spike-pagesize` benchmark (10M entries, single-sample-per-size, not yet repeated with this investigation's
usual interleaved methodology) found `Entries/LeafPages` of 29.5 at the 4096-byte default (~72% of the naive
~41-entries/page capacity) and lower per-op commit cost at 8192 bytes than either 4096 or 16384 - suggestive of a
non-monotonic sweet spot rather than "bigger is strictly better," but not yet confirmed rigorously (that run also
hit this sandbox's disk-space ceiling before finishing the 32768-byte case - see "`mdb_cursor_put` and a false
alarm: `MDB_CORRUPTED` turned out to be disk exhaustion, not a real bug" below for why that confused the
investigation for a while). All 74 `Nethermind.Db.Lmdb.Test` tests pass (73 succeeded, the one pre-existing
unrelated `ThreadPool`-minimum failure).

**Observed live (2026-10-14 to 2026-10-19 testnet sync) - the fill-factor problem is confirmed real, stable, and
worsening in depth over time, not a benchmark artifact:**

| sync point | Storage BTreeDepth | Storage entries/leaf-page | Account entries/leaf-page |
|---|---|---|---|
| ~18% (35M keys) | 5 | 27.6 | 83.4 |
| 100% (282M keys) | **6** | 27.2 | 76.2 |

Storage's ~27 entries/leaf-page (≈65% of the naive ~40-45-entry capacity at this key/value size) held essentially
constant across an 8x growth in entry count (35M → 282M) - and matches the isolated synthetic random-insert
benchmark's 29.5 figure from a completely different absolute scale (10M entries). Three independent measurements
now agree: this is a **stable, scale-invariant structural property of random-key insertion**, not a benchmark
artifact and not something that resolves itself as the database grows. Account's much higher figure (83.4 → 76.2,
still far above Storage's) is the direct, now-measured structural payoff of its near-100% sequential/append
engagement - sequential inserts pack pages almost completely before splitting, where random inserts settle at
the classic ~65-70% average. This is the clearest live evidence yet for *why* AppendData's benefit is real: it
produces a fundamentally better-packed tree, not just a cheaper individual commit.

Separately, Storage's `BTreeDepth` grew from 5 to 6 over the course of the sync - every single write now copies
through one additional tree level versus earlier in the same run, a real, measured write-amplification increase
as the database grows. Both signals (stable-but-suboptimal fill factor, and growing depth) point the same
direction and motivate the same fix: a larger `PageSizeBytes` targets exactly this (fewer, bigger, more
efficiently-packed pages means a shallower tree for the same total entries), independent of the fill-ratio issue
itself, which inherent randomness in Storage's key order means no amount of coalescing/batching tuning can fix.

**Decision: testing `PageSizeBytes=8192` for Storage next.**

### Per-column `PageSizeBytes` override

Added `ILmdbConfig.PageSizeBytesAccount`/`PageSizeBytesStorage`, mirroring `MaxOperationsPerWave`'s existing
shared-value-plus-per-column-override shape exactly: each `0` (the default) falls back to the shared
`PageSizeBytes`. Added specifically because the live data above shows Account and Storage want different
answers - Account is already well-packed via its own near-100% sequential/append engagement and gains nothing
from a bigger page, while Storage's fill factor is a stable property of its effectively-random key order that a
single shared knob would force onto both columns uniformly. `LmdbDbFactoryDecorator.CreateColumnsDb` resolves
each column's effective value (`config.PageSizeBytesAccount > 0 ? ... : config.PageSizeBytes`, same pattern as
`MaxOperationsPerWave`) before constructing each `LmdbDb`. Covered by
`CreateColumnsDb_resolves_per_column_PageSizeBytes_overrides` (Account falls back to the shared value, Storage's
own override wins, confirmed via each column's own `GetPerPartitionStats().PageSize`). All 75
`Nethermind.Db.Lmdb.Test` tests pass (74 succeeded, the one pre-existing unrelated `ThreadPool`-minimum failure).

To test: set `PageSizeBytesStorage=8192`, leave `PageSizeBytes`/`PageSizeBytesAccount` at their `0` default so
Account is unaffected. `storage.lmdb` must be wiped first - page size is stored persistently in the environment
at creation time and only takes effect for a brand new directory. Watch the next sync's `mdb_stat` line for
Storage specifically: does `entries/leaf-page` rise meaningfully above the ~65%-fill baseline now firmly
established at the 4096-byte default, does `BTreeDepth` grow more slowly relative to entry count than the
5→6 growth observed in this sync, and does per-op commit cost drop accordingly. Not yet observed live.

### `mdb_cursor_put` over `mdb_put` - another dead end, confirmed at 10x larger wave size

Tested whether reusing one `LightningCursor` (`mdb_cursor_put`) across a whole wave's Puts - rather than
`tx.Put` (`mdb_put`), which internally uses its own transient cursor per call with no position carried forward -
could recover some of `AppendData`'s benefit for a sequence that's merely *sorted* (not exceeding the database's
real global max, the much weaker condition a cursor's position-hint only needs). Built `lmdb-spike-cursorstat`:
same fixed single-flat-database shape as production Storage today, three scenarios (`A`: `tx.Put` random, `C`:
cursor reused, random order, `D`: cursor reused, sorted order), with every `Put`/`Commit` return code checked and
a final exact entry-count verification (`entriesTotal + totalOpsApplied == actual`) - the same discipline that
caught the original `lmdb-spike` benchmark's bug earlier in this investigation.

**Result: conclusively no benefit, confirmed at two wave sizes an order of magnitude apart (2,048 and 10,000
ops/wave)**:

| wave size | A (tx.Put random) | C (cursor random) | D (cursor sorted) |
|---|---|---|---|
| 2,048 | 7.680 us/op | 7.617 | 7.445 |
| 10,000 | 6.324 us/op | 6.320 | 6.034 |

A, C, and D are statistically indistinguishable at both sizes - cursor reuse buys nothing, with or without
sorting, even at a wave size ~5x the production configuration's cap. This makes sense in hindsight: with a dense,
52-byte-random-keyed database, two independently-random keys that happen to be *adjacent after sorting* are still
astronomically far apart in the real keyspace (sorting doesn't manufacture physical locality for independent
random draws), so they land on unrelated leaf pages regardless of whether the same cursor object issued both
`Put` calls. Closes this avenue definitively - no further investigation planned.

Also used `LightningTransaction.GetStats` (`mdb_stat`) here to measure *net new page allocations per op* (not
real per-transaction copy-on-write activity, which `mdb_stat`/`mdb_env_stat` cannot see - they report the tree's
committed structural state, not transaction-internal dirty-page bookkeeping): random Put allocates new pages at
roughly 0.033-0.035 pages/op, genuine append at ~0.023 pages/op - a real but modest (~30-50%) difference from
tighter page packing on append, nowhere near large enough to explain the 17-92x commit-cost gap already
established between random and append elsewhere in this investigation.

### `mdb_cursor_put` and a false alarm: `MDB_CORRUPTED` turned out to be disk exhaustion, not a real bug

Mid-investigation, both `lmdb-spike-cursorstat` (cursor-based Puts) and `lmdb-spike-pagesize` (plain `tx.Put`, no
cursor involved) crashed with `MDBResultCode.Corrupted` at various wave sizes/page sizes. Since this is a serious
correctness signal if real (not just a performance curiosity), it was investigated directly rather than assumed
away or worked around.

**Isolated with `corrupt-isolate`**: same 32768-byte page size that broke, same wave sizes, plain `tx.Put` (ruling
out cursor involvement entirely), run twice with proper free disk space - once with 256 permanently-pinned
read-only transactions (matching live's `AlivePooledReaders`), once with none. **Neither reproduced any
corruption.** This was the key diagnostic: if cursor reuse or large page sizes were genuinely unsafe, a clean,
isolated, disk-healthy re-run of the exact breaking conditions should have reproduced it.

**Root cause found directly**: a later re-run crashed with `MDBResultCode.ShortWrite` - LMDB's result code for a
`write()` syscall that returned fewer bytes than requested - with a *simultaneous* OS-level "No space left on
device" error. `ShortWrite` is exactly what that OS condition looks like from LMDB's side. Tracing back: every
spike program in this investigation deletes its own multi-GB temp database on success, but a **crashed** run
skips past that cleanup line entirely, leaving its pre-populated database (1.5-3 GiB each) behind. Across the
many spike programs built over this investigation's many sessions, those leftovers accumulated silently in
`/tmp` until it hit 0 bytes free - at which point even the harness's own tooling started failing with `ENOSPC`,
and any further LMDB write anywhere would fail with some disk-exhaustion-flavored error (`ShortWrite` in one
run, `Corrupted` - a plausible symptom of a partial/failed page write - in others). Confirmed by directly
reproducing it: a 150-rep, multi-wave-size sweep with 256 permanently-pinned readers ran the disk dry twice in a
row, because pinned readers block LMDB's free-list page reclaim for the *entire* run - every commit's root/
branch/meta page copies accumulate forever instead of being recycled, which bloats the file far faster than
"total new unique entries" alone would suggest across the thousands of commits a large sweep performs. Dropping
`pinnedReaderCount` to 0 for that specific sweep (reader-pinning was already ruled out as a *commit-cost* factor
in `lmdb-spike-readerpin` earlier in this investigation, so it wasn't the variable under test) and adding a
disk-space guard plus a final entry-count verification let the sweep complete cleanly with exact, verified
numbers (see "`mdb_cursor_put` over `mdb_put`" above).

**Lesson for any future benchmark in this investigation**: always check `df`/free disk space before and
periodically during a multi-GB LMDB benchmark, and clean up `/tmp` leftovers from any crashed prior run before
trusting a new "corruption" or unexplained-failure signal - on a disk-constrained sandbox, accumulated garbage
from past crashes is a more likely explanation than a genuine LMDB/LightningDB bug.

## Validation methodology — this is the part that genuinely changes, not just the library

BTDB's central measurement was "resident bytes per key," because its architecture makes that a fixed,
measurable, and load-bearing constant (`AllocSize - DeallocSize` from `IKeyValueDB.GetNativeMemoryStats()`).
**LMDB has no equivalent number to measure**, because that's the entire point of switching to it — how
much of the mmap'd file is RAM-resident is an OS page-cache fact driven by access patterns, not a
library-reported constant. Measuring "bytes/key" for LMDB and comparing it to BTDB's figure would be
comparing two different things and drawing a false conclusion either way. The validation questions for
this experiment are instead:

1. **Does read/write throughput and tail latency stay acceptable as the dataset grows past available
   RAM**, not just below it? This means the validation run must include at least one phase where the
   synced dataset is deliberately larger than a constrained RAM budget (e.g. run the node in a
   container/cgroup with a RAM limit well below the expected working-set size, or simply let a longer
   sync run past that point on the actual machine), and observe whether performance degrades
   gracefully (more page faults, more disk I/O, higher latency) or falls off a cliff. This is the
   actual bet being tested — that graceful degradation beats BTDB's hard wall — and it needs to be
   verified, not assumed from LMDB's general reputation.
2. **What does a real cache-miss cost on this specific access pattern** (Ethereum account/storage
   keys, 20–52 bytes, ~35–41-byte values, effectively random access order)? Instrument with OS-level
   tools: on Windows, working-set/private-bytes counters (`GetProcessMemoryInfo`,
   `QueryWorkingSetEx` over the mapped region) or Performance Counters; cross-check against
   `dotnet-trace`/ETW page-fault events if available. This replaces `NativeMemoryBytes` as the
   diagnostic BTDB's plugin logged every 30s — log page-fault counts and mapped-region RSS instead of
   a "bytes per key" figure that doesn't exist for this architecture.
3. **Correctness and throughput comparison against both RocksDB (baseline) and BTDB (the other
   experimental data point already measured)** — same devnet/testnet methodology BTDB used (Sepolia
   first; if RAM/scale makes completion unlikely, move to Hoodi, per the precedent already set), same
   emphasis that **live sync testing found 6 of 7 real bugs that unit tests never caught** — treat a
   full live-network sync run with dotTrace profiling as a mandatory phase early in implementation, not
   a final validation step tacked on at the end.

## Testing

Mirror the existing `Nethermind.Db.Btdb.Test` project shape exactly — new `Nethermind.Db.Lmdb.Test`
with `LmdbDbTests.cs`, `LmdbColumnsDbAdapterTests.cs`, `LmdbDbFactoryDecoratorTests.cs`. Additionally,
**add the concurrent-multi-threaded-snapshot-reads regression test from the start** (mirroring
`Snapshot_survives_real_multi_threaded_concurrent_reads`, which BTDB only added reactively after
finding bug #7 on a live restart) — write it before the reader-pool code, as the thing that would have
caught BTDB's bug #7 immediately in CI rather than on a live node weeks later.

## Benchmark

Extend `BtdbVsRocksDbBenchmark.cs` (or add a sibling `LmdbVsRocksDbVsBtdbBenchmark.cs` under
`Nethermind.Benchmark/Db/`) to a three-way comparison, parametrized by which `IDbFactory` decorator is
registered. Point Get/Set at realistic account/storage value sizes, plus the range-scan
(`GetViewBetween`) case.

## Non-goals (unchanged)

- Mainnet-scale runs before the devnet/testnet graceful-degradation behavior is actually confirmed.
- Swapping the classic trie State DB or trie-node columns — same reasoning as the BTDB experiment
  (deprecated backend; trie-node full-scan locality needs).
- Any change to core `Nethermind.Init`/`Nethermind.Db`/`Nethermind.State.Flat` files.
- Removing the BTDB plugin/experiment — it stays as a comparison baseline (see "Location and branch
  strategy").
