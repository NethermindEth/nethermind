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
