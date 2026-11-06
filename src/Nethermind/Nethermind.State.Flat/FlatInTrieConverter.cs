// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

/// <summary>
/// Converts a <see cref="FlatLayout.Flat"/> DB in place to <see cref="FlatLayout.FlatInTrie"/>, then compacts it.
/// </summary>
/// <remarks>
/// Both layouts key the flat entries the same way, and those key lengths differ from every trie node key, so the
/// entries are copied verbatim from the Account and Storage columns into the StateNodes and StorageNodes columns.
/// The copy is flushed before the layout flips, so an interrupted conversion leaves a DB that still opens as
/// <see cref="FlatLayout.Flat"/>. The node may then change the state, so a marker recorded before the copy makes the
/// next conversion first drop the copies left behind. Once flipped, a rerun only redoes the cleanup and compaction.
/// The final compaction rewrites the files under the RocksDB options the DB is opened with, so it must be opened with
/// those of <see cref="FlatLayout.FlatInTrie"/>.
/// </remarks>
public class FlatInTrieConverter(IColumnsDb<FlatDbColumns> db, ILogManager logManager)
{
    private const int ProgressInterval = 10_000_000;
    private static readonly byte[] ConversionStartedKey = Keccak.Compute("FlatInTrieConversionStarted").BytesToArray();

    // Past every flat key: the longest one is a storage key.
    private static readonly byte[] AfterAllKeys = Enumerable.Repeat((byte)0xFF, BaseFlatPersistence.StorageKeyLength + 1).ToArray();

    private readonly ILogger _logger = logManager.GetClassLogger<FlatInTrieConverter>();

    public void Convert(CancellationToken cancellationToken)
    {
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        if (BasePersistence.ReadCurrentState(metadata) == StateId.PreGenesis || BasePersistence.ReadWipedForSync(metadata))
            Refuse("the flat DB holds no fully synced state");

        FlatLayout? layout = BasePersistence.ReadLayout(metadata);
        if (layout == FlatLayout.Flat)
        {
            // The layout is stamped together with the RLP slot encoding; a legacy raw DB must not be re-stamped.
            if (BasePersistence.ReadSlotEncoding(metadata) != BasePersistence.SlotEncodingRlp)
                Refuse("the flat DB stores raw slot values, a deprecated encoding; re-sync it instead");

            if (metadata.Get(ConversionStartedKey) is not null)
            {
                if (_logger.IsInfo) _logger.Info("Dropping the copies left by an interrupted conversion");
                RemoveFlatKeys(FlatDbColumns.StateNodes, BaseFlatPersistence.AccountKeyLength, cancellationToken);
                RemoveFlatKeys(FlatDbColumns.StorageNodes, BaseFlatPersistence.StorageKeyLength, cancellationToken);
                db.Flush();
            }

            metadata.PutSpan(ConversionStartedKey, [1]);
            metadata.FlushOrThrow();

            CopyFlatKeys(FlatDbColumns.Account, FlatDbColumns.StateNodes, BaseFlatPersistence.AccountKeyLength, cancellationToken);
            CopyFlatKeys(FlatDbColumns.Storage, FlatDbColumns.StorageNodes, BaseFlatPersistence.StorageKeyLength, cancellationToken);
            // The copies skip the WAL, so they are made durable before the layout that points at them.
            db.Flush();

            using (IWriteBatch batch = metadata.StartWriteBatch())
            {
                BasePersistence.SetLayout(batch, FlatLayout.FlatInTrie);
                batch.Remove(ConversionStartedKey);
            }
            metadata.FlushOrThrow();
        }
        else if (layout != FlatLayout.FlatInTrie)
        {
            Refuse($"the flat DB has the {layout?.ToString() ?? "unrecorded"} layout");
        }

        foreach (FlatDbColumns column in (ReadOnlySpan<FlatDbColumns>)[FlatDbColumns.Account, FlatDbColumns.Storage])
        {
            IRangeRemovableKeyValueStore store = (IRangeRemovableKeyValueStore)db.GetColumnDb(column);
            store.RemoveRange([], AfterAllKeys);
            store.ReclaimRange([0], AfterAllKeys);
        }

        if (_logger.IsInfo) _logger.Info("Compacting the flat DB. It can take hours.");
        db.Compact();
        if (_logger.IsInfo) _logger.Info($"Flat DB converted to {nameof(FlatLayout.FlatInTrie)}");
    }

    private void CopyFlatKeys(FlatDbColumns source, FlatDbColumns target, int keyLength, CancellationToken cancellationToken)
    {
        using IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
        IWriteBatch targetBatch = batch.GetColumnBatch(target);
        using ISortedView view = ((ISortedKeyValueStore)db.GetColumnDb(source)).GetViewBetween([], AfterAllKeys, ReadFlags.HintReadAhead);
        long count = 0;
        while (view.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view.CurrentKey.Length != keyLength) continue;

            targetBatch.PutSpan(view.CurrentKey, view.CurrentValue, WriteFlags.DisableWAL);
            if (++count % ProgressInterval == 0 && _logger.IsInfo) _logger.Info($"Copied {count:N0} entries from {source} to {target}");
        }

        if (_logger.IsInfo) _logger.Info($"Copied {count:N0} entries from {source} to {target}");
    }

    private void RemoveFlatKeys(FlatDbColumns column, int keyLength, CancellationToken cancellationToken)
    {
        using IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch();
        IWriteBatch columnBatch = batch.GetColumnBatch(column);
        using ISortedView view = ((ISortedKeyValueStore)db.GetColumnDb(column)).GetViewBetween([], AfterAllKeys, ReadFlags.HintReadAhead);
        while (view.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view.CurrentKey.Length == keyLength) columnBatch.Set(view.CurrentKey, null, WriteFlags.DisableWAL);
        }
    }

    private static void Refuse(string reason) =>
        throw new InvalidConfigurationException($"Cannot convert to {nameof(FlatLayout.FlatInTrie)}: {reason}.", ExitCodes.ForbiddenOptionValue);
}
