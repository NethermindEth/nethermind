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
    /// Caps how many operations <see cref="LmdbDb"/>'s group-commit coordinator (<c>DrainIfLeader</c>) will
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
}
