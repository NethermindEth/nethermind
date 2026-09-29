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
    /// Caps how many operations from separate, already-queued write batches <see cref="LmdbDb"/>'s group-commit
    /// coordinator (<c>DrainIfLeader</c>) will coalesce into one physical LMDB write transaction. Every
    /// concurrent <see cref="LmdbDb.CreateSnapshot"/> call is blocked for a commit's entire duration (see
    /// PLAN-LMDB.md "Known issues" - this is a real correctness requirement, not a bug), so an unbounded wave can
    /// turn one slow/large commit into a system-wide stall affecting every reader, not just the writers waiting
    /// on it.
    /// </summary>
    /// <remarks>
    /// This bounds coalescing <em>across separate queued write batches</em> only - it does not split a single
    /// caller's own write batch (e.g. one <c>IWriteBatch</c> accumulated from many <c>PutSpan</c> calls before a
    /// single <c>Dispose()</c>) into multiple physical commits, since doing so would sacrifice that batch's
    /// current all-or-nothing atomicity. If a single write batch is itself larger than this cap, it is still
    /// committed as one physical transaction, uncapped - see <see cref="LmdbDb.DiagnosticsSnapshot.AvgWriteWaveOps"/>
    /// to check whether that is actually happening before assuming this setting is being honored.
    /// </remarks>
    [ConfigItem(Description = "Max operations coalesced from separate queued write batches into one physical LMDB commit. Does not split a single caller's own write batch - see PLAN-LMDB.md.", DefaultValue = "4096")]
    int MaxOperationsPerWave { get; set; }
}
