// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// One-shot startup conversion of a flat DB's base tier from the RocksDB Account/Storage columns
/// (<see cref="FlatBaseStore.Rocks"/>) into the arena shard tables (<see cref="FlatBaseStore.Arena"/>).
/// Runs before block processing (see the <c>ConvertFlatBaseStore</c> init step) when
/// <see cref="IFlatDbConfig.ConvertBaseStore"/> is set; a restart on an already-converted DB is a no-op.
/// </summary>
/// <remarks>
/// Sequence and crash safety — the kind marker is the commit point, written after the shard tables are
/// built and fsynced but BEFORE the overlay rows are deleted:
/// <list type="number">
/// <item>Drop any shard tables a previously crashed conversion left behind, then bulk-load every
/// Account/Storage row into new shard tables (each fsynced before its registry entry commits).</item>
/// <item>Stamp the Arena kind marker (durable). A crash before this point re-converts from the still
/// intact overlay on the next start; the marker must NOT come after the overlay deletion, because
/// re-converting from a partially-deleted overlay would lose data.</item>
/// <item>Cleanup: delete the migrated overlay rows, manually compact the Account/Storage column families
/// (so overlay probes during a benchmark are cheap near-empty lookups rather than tombstone scans), flush
/// the DB, and advise the produced arena files out of the page cache (so the conversion's sequential
/// writes don't leave the arena read path unfairly warm versus a cold RocksDB baseline). A crash anywhere
/// in this phase is benign: leftover overlay rows shadow byte-identical base values and are reconciled by
/// the next fold.</item>
/// </list>
/// </remarks>
public sealed class FlatBaseStoreConverter(
    IColumnsDb<FlatDbColumns> db,
    ArenaBasePersistence persistence,
    ILogManager logManager)
{
    private const int ProgressLogInterval = 5_000_000;
    private const int DeleteBatchSize = 10_000;

    private readonly ILogger _logger = logManager.GetClassLogger<FlatBaseStoreConverter>();

    private long _accountRows;
    private long _storageRows;
    private long _bytes;

    /// <summary>Run the conversion when the DB on disk still belongs to the Rocks base store.</summary>
    /// <returns><c>true</c> when a conversion ran; <c>false</c> when the DB is already Arena or empty.</returns>
    public bool Convert(CancellationToken cancellationToken)
    {
        if (ArenaBasePersistence.ReadBaseStoreKind(db) == FlatBaseStore.Arena)
        {
            if (_logger.IsInfo) _logger.Info("Flat base store is already 'Arena'; skipping conversion.");
            return false;
        }

        if (!BasePersistence.HasCurrentState(db.GetColumnDb(FlatDbColumns.Metadata)))
        {
            if (_logger.IsInfo) _logger.Info("Flat DB is empty; nothing to convert to the Arena base store.");
            return false;
        }

        if (_logger.IsInfo) _logger.Info("Converting flat base store 'Rocks' -> 'Arena'. This is a one-time migration and may take a while on large state.");
        long start = Stopwatch.GetTimestamp();

        BuildShardTables(cancellationToken);
        CommitConverted();
        CleanupOverlay(cancellationToken);

        if (_logger.IsInfo) _logger.Info(
            $"Flat base store conversion completed: converted {_accountRows:N0} accounts, {_storageRows:N0} slots " +
            $"({(double)_bytes / 1.GiB:F2} GiB) in {Stopwatch.GetElapsedTime(start).TotalSeconds:F0}s");
        return true;
    }

    /// <summary>Phase 1: drop any partial previous attempt, then bulk-load every overlay row into new,
    /// fsynced shard tables. The overlay is left untouched so a crash here re-converts cleanly.</summary>
    internal void BuildShardTables(CancellationToken cancellationToken)
    {
        persistence.ClearShardTables();
        persistence.BulkLoad(
            EnumerateRows(FlatDbColumns.Account, BaseFlatPersistence.AccountKeyLength, "account", cancellationToken),
            EnumerateRows(FlatDbColumns.Storage, BaseFlatPersistence.StorageKeyLength, "slot", cancellationToken));
    }

    /// <summary>Phase 2: stamp the durable Arena kind marker — the conversion's commit point.</summary>
    internal void CommitConverted() => persistence.WriteBaseStoreKindMarker();

    /// <summary>Phase 3 (post-commit cleanup, safe to lose to a crash): delete the migrated overlay rows,
    /// compact the overlay column families, flush, and evict the arena files from the page cache.</summary>
    internal void CleanupOverlay(CancellationToken cancellationToken)
    {
        DeleteAllRows(FlatDbColumns.Account, cancellationToken);
        DeleteAllRows(FlatDbColumns.Storage, cancellationToken);

        if (_logger.IsInfo) _logger.Info("Compacting the flat Account/Storage columns after base store conversion.");
        db.GetColumnDb(FlatDbColumns.Account).Compact();
        db.GetColumnDb(FlatDbColumns.Storage).Compact();
        db.Flush();

        persistence.EvictShardTablePageCache();
    }

    private IEnumerable<KeyValuePair<byte[], byte[]>> EnumerateRows(
        FlatDbColumns column, int keyLength, string label, CancellationToken cancellationToken)
    {
        long rows = 0;
        long startingBytes = _bytes;
        long start = Stopwatch.GetTimestamp();
        foreach (KeyValuePair<byte[], byte[]?> kv in db.GetColumnDb(column).GetAll(ordered: true))
        {
            if (kv.Value is null || kv.Key.Length != keyLength) continue;
            rows++;
            _bytes += kv.Key.Length + kv.Value.Length;
            if (rows % ProgressLogInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_logger.IsInfo) _logger.Info(
                    $"Base store conversion: {rows:N0} {label} rows ({(double)(_bytes - startingBytes) / 1.GiB:F2} GiB) in {Stopwatch.GetElapsedTime(start).TotalSeconds:F0}s");
            }

            yield return new KeyValuePair<byte[], byte[]>(kv.Key, kv.Value);
        }

        if (column == FlatDbColumns.Account) _accountRows = rows;
        else _storageRows = rows;
    }

    /// <summary>Delete every row of <paramref name="column"/> in bounded batches (a single batch over a
    /// mainnet-scale column would exhaust memory, mirroring <see cref="BasePersistence.ClearAllColumns"/>).</summary>
    private void DeleteAllRows(FlatDbColumns column, CancellationToken cancellationToken)
    {
        IDb columnDb = db.GetColumnDb(column);
        IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
        try
        {
            int count = 0;
            foreach (byte[] key in columnDb.GetAllKeys())
            {
                batch.GetColumnBatch(column).Remove(key);
                if (++count == DeleteBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IColumnsWriteBatch<FlatDbColumns> next = db.StartWriteBatch();
                    batch.Dispose(); // commit the chunk
                    batch = next;
                    count = 0;
                }
            }
        }
        finally
        {
            batch.Dispose();
        }
    }
}
