// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Paprika;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>Converts a synced <see cref="FlatLayout.Flat"/> flat DB into a <see cref="FlatLayout.PaprikaFlat"/> one in place, for development testing.</summary>
/// <remarks>
/// Copies the flat accounts and slots into a new Paprika file, then stamps the flat DB with the new layout and drops its
/// account and storage columns. The trie columns are kept as they are, with the trie node log merged into them first, so
/// the PaprikaFlat trie node log starts empty. Slots are copied only for accounts whose storage root is not empty. Not
/// resumable: a failed copy deletes the file it created, while a crash leaves it to be deleted by hand.
/// </remarks>
public sealed class PaprikaFlatConverter(IPersistence persistence, ITrieNodeLog trieNodeLog, IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
{
    private const int EntriesPerCommit = 1_000_000;
    private const int CommitsPerProgressLog = 10;

    /// <summary>A bound above every account and storage key.</summary>
    private static readonly byte[] PastEveryKey = Enumerable.Repeat(byte.MaxValue, 64).ToArray();

    private readonly ILogger _logger = logManager.GetClassLogger<PaprikaFlatConverter>();

    /// <summary>Copies the flat state into <c>paprika.db</c> in <paramref name="directory"/> and switches the flat DB to <see cref="FlatLayout.PaprikaFlat"/>.</summary>
    /// <exception cref="InvalidConfigurationException">The flat DB is not a synced <see cref="FlatLayout.Flat"/> one, or the Paprika file exists.</exception>
    public void Convert(string directory, CancellationToken cancellationToken)
    {
        Db.IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        string file = MemoryMappedPageManager.GetPaprikaFilePath(directory);
        if (config.Layout != FlatLayout.Flat) Refuse($"it is configured with layout '{config.Layout}'; run the conversion with 'FlatDb.Layout' set to '{FlatLayout.Flat}'");
        if (File.Exists(file)) Refuse($"{file} exists; delete it to convert again");
        if (BasePersistence.ReadSnapCheckpoint(metadata) is not null || BasePersistence.ReadWipedForSync(metadata)) Refuse("a state sync is in progress");

        trieNodeLog.Drain();
        StateId state;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.FullScan))
        {
            state = reader.CurrentState;
            if (state == StateId.PreGenesis) Refuse("it holds no state");
            try
            {
                using PagedDb paprika = PaprikaFlatStore.OpenFile(directory, config.PaprikaFlatFileSizeBytes, config.PaprikaFlatPageCacheBytes, logManager);
                Copy(reader, paprika, state, cancellationToken);
            }
            catch
            {
                File.Delete(file);
                throw;
            }
        }

        // One write, so the flat DB is stamped with one layout or the other, never with the new layout and a RocksDB state pointer.
        using (IColumnsWriteBatch<FlatDbColumns> batch = db.StartWriteBatch())
        {
            IWriteBatch metadataBatch = batch.GetColumnBatch(FlatDbColumns.Metadata);
            BasePersistence.SetLayout(metadataBatch, FlatLayout.PaprikaFlat);
            BasePersistence.RemoveCurrentState(metadataBatch);
        }
        db.Flush();

        foreach (FlatDbColumns column in (ReadOnlySpan<FlatDbColumns>)[FlatDbColumns.Account, FlatDbColumns.Storage])
        {
            IRangeRemovableKeyValueStore removable = (IRangeRemovableKeyValueStore)db.GetColumnDb(column);
            removable.RemoveRange([], PastEveryKey);
            removable.ReclaimRange([0], PastEveryKey);
        }

        if (_logger.IsInfo) _logger.Info($"Converted the flat DB at {state} to {FlatLayout.PaprikaFlat}. Start the node with 'FlatDb.Layout' set to '{FlatLayout.PaprikaFlat}' and 'FlatDb.TrieNodeLogEnabled' set to true.");
    }

    [SkipLocalsInit]
    private void Copy(IPersistence.IPersistenceReader reader, PagedDb paprika, in StateId state, CancellationToken cancellationToken)
    {
        Span<byte> slotValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        long accounts = 0;
        long slots = 0;
        int pending = 0;
        int commits = 0;
        IBatch batch = paprika.BeginNextBatch();
        try
        {
            using IPersistence.IFlatIterator accountIterator = reader.CreateAccountIterator(ValueKeccak.Zero, ValueKeccak.MaxValue);
            while (accountIterator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValueHash256 account = accountIterator.CurrentKey;
                batch.SetRaw(PaprikaFlatKeys.Account(account.Bytes), accountIterator.CurrentValue);
                accounts++;
                bool hasStorage = AccountDecoder.Slim.DecodeStorageRootOnly(accountIterator.CurrentValue) != Keccak.EmptyTreeHash;
                CommitIfFull();
                if (!hasStorage) continue;

                ValueHash256 storageAccount = PaprikaFlatKeys.StorageAccount(account.Bytes);
                using IPersistence.IFlatIterator slotIterator = reader.CreateStorageIterator(account, ValueKeccak.Zero, ValueKeccak.MaxValue);
                while (slotIterator.MoveNext())
                {
                    int length = BaseFlatPersistence.EncodeSlotValue(BaseFlatPersistence.DecodeSlotValue(slotIterator.CurrentValue), rlpWrapSlots: true, slotValue);
                    batch.SetRaw(PaprikaFlatKeys.Slot(storageAccount.Bytes, slotIterator.CurrentKey.Bytes), slotValue[..length]);
                    slots++;
                    CommitIfFull();
                }
            }

            PaprikaFlatMetadata.ChangeSet markers = new();
            BasePersistence.SetLayout(markers, FlatLayout.PaprikaFlat);
            BasePersistence.SetCurrentState(markers, state);
            PaprikaFlatMetadata.Empty.Merge(markers.Changes).WriteTo(batch);
            batch.Commit(CommitOptions.FlushDataAndRoot).AsTask().GetAwaiter().GetResult();
            if (_logger.IsInfo) _logger.Info($"Converting to {FlatLayout.PaprikaFlat}: {accounts:N0} accounts and {slots:N0} slots copied");
        }
        finally
        {
            batch.Dispose();
        }

        void CommitIfFull()
        {
            if (++pending < EntriesPerCommit) return;

            batch.Commit(CommitOptions.FlushDataOnly).AsTask().GetAwaiter().GetResult();
            batch.Dispose();
            batch = paprika.BeginNextBatch();
            pending = 0;
            if (++commits % CommitsPerProgressLog == 0 && _logger.IsInfo) _logger.Info($"Converting to {FlatLayout.PaprikaFlat}: {accounts:N0} accounts and {slots:N0} slots copied");
        }
    }

    [DoesNotReturn]
    private static void Refuse(string reason) =>
        throw new InvalidConfigurationException($"Cannot convert the flat DB to {FlatLayout.PaprikaFlat}: {reason}.", -1);
}
