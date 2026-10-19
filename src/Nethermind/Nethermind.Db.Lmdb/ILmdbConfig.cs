// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db.Lmdb;

/// <summary>
/// Configuration for the experimental LMDB-backed FlatDb Account/Storage columns plugin.
/// </summary>
[ConfigCategory(Description = "Configuration for the experimental LMDB-backed FlatDb Account/Storage columns.")]
public interface ILmdbConfig : IConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether the FlatDb Account/Storage columns are backed by LMDB instead of
    /// RocksDB. This is a learning/benchmarking spike, not a production backend - see PLAN-LMDB.md.
    /// </summary>
    [ConfigItem(Description = "Whether the FlatDb Account/Storage columns are backed by LMDB instead of RocksDB.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>
    /// The initial <c>MapSize</c> (in bytes) reserved for each of the Account and Storage LMDB environments.
    /// LMDB requires an upfront maximum-size reservation; sparse files mean this does not cost real disk until
    /// pages are actually written, so it should be set generously for the dataset a validation run expects to
    /// reach (see PLAN-LMDB.md "MapSize sizing and growth"). If a write ever exceeds this, the map is doubled and
    /// the write retried automatically - see <see cref="LmdbDb"/>.
    /// </summary>
    [ConfigItem(Description = "Initial MapSize, in bytes, reserved for each of the Account and Storage LMDB environments.", DefaultValue = "68719476736")]
    long InitialMapSizeBytes { get; set; }

    /// <summary>
    /// Shared default LMDB page size (in bytes - must be a power of 2 from 512 to 65536) for the Account and
    /// Storage environments, overridden per-column by <see cref="PageSizeBytesAccount"/>/<see cref="PageSizeBytesStorage"/>
    /// (each <c>0</c> falls back to this value, mirroring <see cref="MaxOperationsPerWave"/>'s own
    /// shared-plus-per-column-override shape). <c>0</c> here (the default) leaves LMDB's own default in place,
    /// which is the operating system's page size (4096 bytes on Linux/Windows). See PLAN-LMDB.md "mdb_stat live
    /// diagnostics" for the live <c>entries/leaf-page</c>/<c>BTreeDepth</c> data this is meant to be tuned from -
    /// a low entries-per-leaf-page figure relative to how many entries should fit in a page of this size suggests
    /// leaf pages are splitting before they're actually full, which a page size change could address, trading off
    /// against more bytes copied per copy-on-write page touch. Live data (see PLAN-LMDB.md) confirmed this is a
    /// stable, scale-invariant property of Storage's effectively-random key order specifically - Account is
    /// already well-packed via its own near-100% sequential/append engagement and does not need this - which is
    /// why the per-column overrides exist rather than one setting applied uniformly to both.
    /// </summary>
    /// <remarks>
    /// LMDB stores the page size persistently in the environment at creation time, so this only takes effect for
    /// a brand new (empty) environment directory - changing it for an existing one is not yet verified to either
    /// take effect, be silently ignored, or error; treat that combination as unverified rather than assuming
    /// either outcome.
    /// </remarks>
    [ConfigItem(Description = "Shared default LMDB page size override, in bytes (power of 2, 512-65536), for the Account and Storage LMDB environments - overridden per-column by PageSizeBytesAccount/PageSizeBytesStorage. 0 (default) uses the OS page size. Only takes effect for a brand new environment directory - see PLAN-LMDB.md.", DefaultValue = "0")]
    int PageSizeBytes { get; set; }

    /// <summary>Per-column override of <see cref="PageSizeBytes"/> for the Account column specifically - <c>0</c>
    /// (the default) falls back to the shared value. See <see cref="PageSizeBytes"/>'s remarks for why Account and
    /// Storage are expected to want different values.</summary>
    [ConfigItem(Description = "Account-column override of PageSizeBytes. 0 (default) falls back to the shared value - see PLAN-LMDB.md.", DefaultValue = "0")]
    int PageSizeBytesAccount { get; set; }

    /// <summary>Per-column override of <see cref="PageSizeBytes"/> for the Storage column specifically - see
    /// <see cref="PageSizeBytesAccount"/>'s remarks.</summary>
    [ConfigItem(Description = "Storage-column override of PageSizeBytes. 0 (default) falls back to the shared value - see PLAN-LMDB.md.", DefaultValue = "0")]
    int PageSizeBytesStorage { get; set; }

    /// <summary>
    /// Size of each of the Account and Storage LMDB environments' reader-lock table, i.e. the maximum number of
    /// concurrently-open read transactions. LMDB's own default is only 126 - far too small here:
    /// <see cref="LmdbDb.CreateSnapshot"/> opens a whole pool of <c>Environment.ProcessorCount</c> read
    /// transactions per snapshot and holds them for the snapshot's entire lifetime, so on a many-core machine
    /// just a handful of concurrently-alive snapshots (or one that isn't disposed promptly) exhausts the default
    /// table (observed in practice: <c>MDB_READERS_FULL</c>).
    /// </summary>
    /// <remarks>
    /// This is not just a performance knob - see PLAN-LMDB.md "Known issues". Hitting the limit at all is far
    /// more serious than a normal recoverable error: the verification spike confirmed that in LightningDB 0.23.0,
    /// a failed <c>BeginTransaction</c> call leaks an internal handle that later crashes the *entire process*
    /// via an unrecoverable finalizer exception, on some unrelated later GC - even if a caller catches the
    /// immediate exception and a subsequent retry succeeds. Raise this value well above the realistic worst-case
    /// concurrent load if <c>MDB_READERS_FULL</c> ever recurs even at the default; do not rely on retrying past
    /// it.
    /// </remarks>
    [ConfigItem(Description = "Reader-lock table size (max concurrently-open read transactions) for each of the Account and Storage LMDB environments. Exceeding it is process-fatal in LightningDB 0.23.0 - see PLAN-LMDB.md.", DefaultValue = "8192")]
    int MaxReaders { get; set; }

    /// <summary>
    /// Caps how many operations <see cref="LmdbDb"/>'s dedicated writer thread (<c>WriterThreadLoop</c>) will
    /// apply in one physical LMDB write transaction, whether coalesced from several separate queued write
    /// batches or drawn from a single caller's own oversized one (see <c>CommitLargeWriteInChunks</c>). Every
    /// concurrent <see cref="LmdbDb.CreateSnapshot"/> call is blocked for a commit's entire duration (see
    /// PLAN-LMDB.md "Known issues" - this is a real correctness requirement, not a bug), so an unbounded
    /// commit can turn one slow/large physical transaction into a system-wide stall affecting every reader, not
    /// just the writers waiting on it - observed in practice as consensus-client Engine API timeouts during a
    /// live sync, traced to snap-sync account-tree writes coalescing tens of thousands of operations into a
    /// single <c>IWriteBatch.Dispose()</c> call.
    /// </summary>
    /// <remarks>
    /// A single caller's own write batch that by itself exceeds this cap is split into sequential chunks, each
    /// its own physical commit - this trades away that batch's previous all-or-nothing atomicity across chunks
    /// (a failure partway through can leave it partially applied), accepted because the callers observed
    /// producing oversized batches already pass <see cref="Nethermind.Core.WriteFlags.DisableWAL"/> and so
    /// already tolerate losing the *entire* batch on a crash - see <c>CommitLargeWriteInChunks</c>'s remarks for
    /// the full reasoning. Use <see cref="LmdbDb.DiagnosticsSnapshot.AvgWriteWaveOps"/> to confirm this cap is
    /// actually bringing per-commit size down for a given column.
    /// </remarks>
    [ConfigItem(Description = "Max operations per physical LMDB commit, whether coalesced from several write batches or chunked from a single oversized one - see PLAN-LMDB.md.", DefaultValue = "4096")]
    int MaxOperationsPerWave { get; set; }

    /// <summary>
    /// Per-column override of <see cref="MaxOperationsPerWave"/> for the Account column specifically - <c>0</c>
    /// (the default) falls back to the shared value. Added because Account and Storage were observed to have
    /// very different natural batch shapes (see PLAN-LMDB.md "Known issues": Account's writes are typically few,
    /// very large, already-sorted batches, while Storage's are typically many small ones) and batch size, not
    /// reader count or lock contention, was confirmed to be the dominant driver of physical-commit cost - a
    /// single shared cap cannot be independently tuned for both shapes.
    /// </summary>
    [ConfigItem(Description = "Account-column override of MaxOperationsPerWave. 0 (default) falls back to the shared value - see PLAN-LMDB.md.", DefaultValue = "0")]
    int MaxOperationsPerWaveAccount { get; set; }

    /// <summary>Per-column override of <see cref="MaxOperationsPerWave"/> for the Storage column - see
    /// <see cref="MaxOperationsPerWaveAccount"/>'s remarks.</summary>
    [ConfigItem(Description = "Storage-column override of MaxOperationsPerWave. 0 (default) falls back to the shared value - see PLAN-LMDB.md.", DefaultValue = "0")]
    int MaxOperationsPerWaveStorage { get; set; }

    /// <summary>
    /// Splits the Account LMDB environment into <c>ISyncConfig.SnapSyncAccountRangePartitionCount</c> separate
    /// named databases (one per account-range partition), so <see cref="PutOptions.AppendData"/> can engage for
    /// every partition's monotonically-advancing writes during Phase 1 snap sync, not just the single highest
    /// partition - see PLAN-LMDB.md "Known issues" for why only ~1/N of account-range writes could ever qualify
    /// under a single flat keyspace.
    /// </summary>
    /// <remarks>
    /// <b>Not compatible with an existing non-partitioned account.lmdb directory</b> - partitioned mode looks
    /// for data in separate named databases that a non-partitioned sync never wrote to, so existing data would
    /// silently appear empty. <see cref="LmdbDb"/>'s constructor refuses to start (rather than risk that) if the
    /// default (non-partitioned) database already has entries when this is enabled - wipe the directory for a
    /// fresh sync, or leave this disabled to keep reading existing data.
    /// </remarks>
    [ConfigItem(Description = "Splits the Account LMDB environment into one named database per account-range sync partition, so AppendData can engage for all of them - see PLAN-LMDB.md. Not compatible with an existing non-partitioned account.lmdb directory.", DefaultValue = "false")]
    bool EnableAccountPartitioning { get; set; }

    // A Storage counterpart to EnableAccountPartitioning (one named database per account-range partition,
    // applying the same AppendData-engagement idea to Storage) was implemented, deployed live, and then reverted
    // - see PLAN-LMDB.md "Storage partitioning" and "Why Storage only reaches ~22.6% engagement, not ~100% like
    // Account" for the full investigation. In short: AppendData's commit-cost benefit is a whole-transaction
    // property (LMDB's physical commit cost depends on the union of distinct dirty pages touched by every
    // operation in the transaction, not on any individual operation), and raising the fraction of individually-
    // appendable ops never came close to whole-transaction purity for Storage, because snap sync's storage
    // responses arrive with no ordering guarantee relative to account-hash order. Three different approaches to
    // recover a real benefit were tried and directly measured as ineffective or counter-productive: sorting a
    // wave's keys before applying plain Put (no effect, ratio ~0.99x - sorting alone doesn't engage LMDB's append
    // fast path), and committing each partition's group as its own separate physical transaction (1.75x *slower*
    // than one combined transaction, not faster - the added per-transaction overhead exceeds the isolated gain).
    // The only remaining path (buffering/sorting an entire partition's writes until its whole snap-sync range
    // completes, then bulk-loading) was judged not worth its cost (staging several GiB per partition, delayed
    // visibility, new crash/resume semantics) relative to the payoff. Storage stays unpartitioned.

    // EnableStorageMultiEnvironment (one fully independent LmdbDb/LightningEnvironment per account-range
    // partition, instead of one shared environment) was implemented and tested on a live testnet sync, then
    // reverted - see PLAN-LMDB.md "Separate LMDB environments per partition" and its live-sample follow-ups for
    // the full investigation. The isolated benchmark that motivated it gave each environment a fixed, equal share
    // of one shared wave and measured real wall-clock parallelism (a genuine ~4x win) - but live traffic doesn't
    // arrive that way: splitting one shared write-arrival stream across N independent per-environment queues
    // *before* coalescing meant each queue saw only ~1/8th the concurrent traffic at any instant, fragmenting
    // naturally-large waves (266 ops) into much smaller ones (53-70 ops) - and since per-transaction commit cost
    // doesn't amortize away with size (confirmed separately), paying it far more often outweighed whatever
    // parallelism benefit the separate writer locks provided. Net overall: roughly 1.7-2.7x *worse* per-op cost
    // across four consistent live samples (one at matched DB size, ruling out "smaller DB" as the explanation),
    // confirmed to reverse only in a later, more sequential/concentrated sync phase where coalescing recovered
    // regardless. This pointed at the real lever: wave size/coalescing, not raw concurrency or AppendData
    // engagement - which is what the dedicated-writer-thread redesign (see <see cref="LmdbDb"/>'s own remarks)
    // now targets directly instead.
}
