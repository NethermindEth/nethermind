// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Rewrites the storage column of a <see cref="FlatLayout.PreimageFlatV1"/> flat DB into the
/// <see cref="FlatLayout.PreimageFlat"/> key shape and stamps the new layout, so an existing preimage DB can
/// adopt the full-address-leading key without a resync.
/// </summary>
/// <remarks>
/// The two shapes are permutations of the same 52 bytes and therefore share a key space, so the column cannot be
/// rewritten in place: a converted key may land on one the scan has not reached yet. Instead the whole column is
/// dumped into a scratch DB under its converted key, deleted, and copied back.
///
/// Every phase is idempotent and the phase boundary is recorded in the flat DB's metadata column, so an
/// interrupted migration is completed by simply running it again. The marker is what keeps the dump from running
/// a second time once deletion has started, which would convert already-converted keys.
///
/// Only the storage column changes. Account keys, the trie node columns (see <see cref="BaseTriePersistence"/>)
/// and the persisted-snapshot tier are all unaffected by the layout.
/// </remarks>
public class PreimageStorageKeyMigration(
    IColumnsDb<FlatDbColumns> db,
    Func<IDb> scratchDbFactory,
    ILogManager logManager)
{
    /// <summary>Entries per write batch, bounding the memory a batch holds. Mirrors <see cref="BasePersistence.ClearAllColumns"/>.</summary>
    private const int BatchSize = 10_000;

    private const int ProgressIntervalMs = 10_000;

    private const int StorageKeyLength = BaseFlatPersistence.StorageKeyLength;

    /// <summary>Marks the scratch dump as complete, i.e. that the storage column may now be deleted and rebuilt from it.</summary>
    internal static readonly byte[] DumpCompleteKey = Keccak.Compute("PreimageStorageKeyMigrationDumpComplete").BytesToArray();

    private readonly ILogger _logger = logManager.GetClassLogger<PreimageStorageKeyMigration>();

    /// <summary>Converts the flat DB's storage column to the <see cref="FlatLayout.PreimageFlat"/> key shape.</summary>
    /// <param name="cancellationToken">Aborts between batches; a re-run resumes from the last completed phase.</param>
    /// <returns><c>true</c> when the DB was converted, <c>false</c> when there was nothing to convert.</returns>
    /// <exception cref="InvalidConfigurationException">The DB is not on a preimage layout.</exception>
    public bool Run(CancellationToken cancellationToken)
    {
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        switch (BasePersistence.ReadLayout(metadata))
        {
            case null:
                if (_logger.IsInfo) _logger.Info("Flat DB has no recorded layout, so there is nothing to migrate to PreimageFlat.");
                return false;
            case FlatLayout.PreimageFlat:
                if (_logger.IsInfo) _logger.Info("Flat DB is already on the PreimageFlat layout.");
                return false;
            case FlatLayout.PreimageFlatV1:
                break;
            case FlatLayout stored:
                throw new InvalidConfigurationException(
                    $"Flat DB was synced with layout '{stored}', which has no PreimageFlat equivalent. " +
                    $"Only a '{FlatLayout.PreimageFlatV1}' DB can be migrated.", -1);
        }

        IDb storage = db.GetColumnDb(FlatDbColumns.Storage);
        using IDb scratchDb = scratchDbFactory();

        if (metadata.Get(DumpCompleteKey) is null)
        {
            DumpToScratch(storage, scratchDb, cancellationToken);
            scratchDb.Flush();
            metadata.Set(DumpCompleteKey, [1]);
            metadata.Flush();
        }
        else if (_logger.IsWarn)
        {
            _logger.Warn("Resuming an interrupted PreimageFlat migration from the existing scratch DB.");
        }

        DeleteStorageColumn(storage, scratchDb.EstimatedCount, cancellationToken);
        storage.Flush();

        RestoreFromScratch(storage, scratchDb, cancellationToken);
        storage.Flush();

        using (IWriteBatch metadataBatch = metadata.StartWriteBatch())
        {
            BasePersistence.SetLayoutMarker(metadataBatch, FlatLayout.PreimageFlat);
            metadataBatch.Remove(DumpCompleteKey);
        }
        metadata.Flush();

        // Disposes the scratch DB and deletes its directory. The `using` above then no-ops.
        scratchDb.Clear();

        if (_logger.IsWarn) _logger.Warn($"Flat DB migrated to the {FlatLayout.PreimageFlat} layout.");
        return true;
    }

    /// <summary>Writes every storage entry into the scratch DB under its converted key, leaving the column untouched.</summary>
    internal void DumpToScratch(IDb storage, IDb scratchDb, CancellationToken cancellationToken)
    {
        long passedThrough = 0;
        byte[] convertedKey = new byte[StorageKeyLength];

        long dumped = ForEachBatch("Dumping converted storage", storage.GetAll(ordered: true), estimatedTotal: 0, scratchDb,
            (writeBatch, entry) =>
            {
                if (entry.Key.Length == StorageKeyLength)
                {
                    BaseFlatPersistence.ConvertV1StorageKey(entry.Key, convertedKey);
                    writeBatch.Set(convertedKey, entry.Value, WriteFlags.DisableWAL);
                }
                else
                {
                    // Not a storage entry — the readers skip these, but dropping them would be silent data loss.
                    writeBatch.Set(entry.Key, entry.Value, WriteFlags.DisableWAL);
                    passedThrough++;
                }
            },
            cancellationToken);

        if (passedThrough > 0 && _logger.IsWarn)
            _logger.Warn($"Copied {passedThrough} storage column entries of unexpected key length through unconverted.");
        if (_logger.IsInfo) _logger.Info($"Dumped {dumped} storage entries.");
    }

    private void DeleteStorageColumn(IDb storage, long estimatedTotal, CancellationToken cancellationToken)
    {
        long deleted = ForEachBatch("Deleting old storage", storage.GetAllKeys(), estimatedTotal, storage,
            static (writeBatch, key) => writeBatch.Set(key, null, WriteFlags.DisableWAL),
            cancellationToken);

        if (_logger.IsInfo) _logger.Info($"Deleted {deleted} old storage entries.");
    }

    private void RestoreFromScratch(IDb storage, IDb scratchDb, CancellationToken cancellationToken)
    {
        long restored = ForEachBatch("Restoring storage", scratchDb.GetAll(ordered: true), scratchDb.EstimatedCount, storage,
            static (writeBatch, entry) => writeBatch.Set(entry.Key, entry.Value, WriteFlags.DisableWAL),
            cancellationToken);

        if (_logger.IsInfo) _logger.Info($"Restored {restored} storage entries.");
    }

    /// <summary>
    /// Applies <paramref name="apply"/> to every item, one write batch per <see cref="BatchSize"/> items, reporting
    /// progress until the enumeration ends.
    /// </summary>
    /// <param name="estimatedTotal">Denominator for the progress report, or 0 when the total is not known up front.</param>
    /// <returns>The number of items applied.</returns>
    private long ForEachBatch<T>(
        string phase,
        IEnumerable<T> source,
        long estimatedTotal,
        IDb target,
        Action<IWriteBatch, T> apply,
        CancellationToken cancellationToken)
    {
        if (_logger.IsWarn) _logger.Warn($"{phase}. This takes a while on a large DB.");

        ProgressLogger progressLogger = new(phase, logManager);
        progressLogger.SetFormat(p => p.TargetValue > 0
            ? $"{phase} {p.CurrentValue,13:N0} / ~{p.TargetValue,13:N0} entries | {p.CurrentPerSecond,9:N0} entries/s"
            : $"{phase} {p.CurrentValue,13:N0} entries | {p.CurrentPerSecond,9:N0} entries/s");
        progressLogger.Reset(0, (ulong)Math.Max(estimatedTotal, 0));

        using Timer timer = new(ProgressIntervalMs) { Enabled = true };
        timer.Elapsed += (_, _) => progressLogger.LogProgress();

        long count = 0;
        try
        {
            foreach (IReadOnlyList<T> batch in Batched(source, cancellationToken))
            {
                using (IWriteBatch writeBatch = target.StartWriteBatch())
                {
                    foreach (T item in batch) apply(writeBatch, item);
                }

                count += batch.Count;
                progressLogger.Update((ulong)count);
            }
        }
        finally
        {
            timer.Stop();
            progressLogger.MarkEnd();
            progressLogger.LogProgress();
        }

        return count;
    }

    /// <summary>Chunks an enumeration into lists of at most <see cref="BatchSize"/> entries.</summary>
    private static IEnumerable<IReadOnlyList<T>> Batched<T>(IEnumerable<T> source, CancellationToken cancellationToken)
    {
        List<T> batch = new(BatchSize);
        foreach (T item in source)
        {
            batch.Add(item);
            if (batch.Count < BatchSize) continue;

            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
            batch = new List<T>(BatchSize);
        }

        if (batch.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
    }
}
