// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Captures finalized per-block changesets into the history columns before the per-block snapshots are pruned,
/// using the exact flat key/value encoders so the recorded bytes match the live flat columns. Deleted accounts
/// and zeroed/removed slots are recorded as empty tombstones.
/// </summary>
public sealed class HistoryWriter : IFlatPersistenceCaptureHook
{
    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryStore _accountHistory;
    private readonly HistoryStore _storageHistory;
    private readonly StorageClearStore _storageClears;
    private readonly IDb _availableBlocks;
    private readonly bool _rlpWrapSlots;
    private readonly bool _enabled;
    private readonly ILogger _logger;

    private ulong _lastCapturedBlock;
    private bool _anyCaptured;

    public HistoryWriter(IColumnsDb<FlatDbColumns> db, IColumnsDb<FlatHistoryColumns> history, IFlatDbConfig config, ILogManager logManager)
        : this(history, BasePersistence.ResolveSlotEncoding(
            db,
            (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage),
            logManager.GetClassLogger<HistoryWriter>()), config.HistoryEnabled, logManager.GetClassLogger<HistoryWriter>())
    {
    }

    private HistoryWriter(IColumnsDb<FlatHistoryColumns> history, bool rlpWrapSlots, bool enabled, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(history);
        _enabled = enabled;
        _history = history;
        _rlpWrapSlots = rlpWrapSlots;
        _logger = logger;
        _accountHistory = new HistoryStore(
            history.GetColumnDb(FlatHistoryColumns.AccountHistory),
            history.GetColumnDb(FlatHistoryColumns.AccountChangeSets));
        _storageHistory = new HistoryStore(
            history.GetColumnDb(FlatHistoryColumns.StorageHistory),
            history.GetColumnDb(FlatHistoryColumns.StorageChangeSets));
        _storageClears = new StorageClearStore(history.GetColumnDb(FlatHistoryColumns.StorageClears));
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
    }

    public ulong LastCapturedBlock => _lastCapturedBlock;

    /// <summary>
    /// Captures the changeset of every not-yet-captured block on <paramref name="persistedHead"/>'s chain, up to and
    /// including it. Must run before the per-block snapshots are pruned.
    /// </summary>
    /// <remarks>
    /// Walks backwards through each base's <see cref="Snapshot.From"/> link (one base == one block's changeset),
    /// leasing from the persisted tier when long-finality Phase 2 converted the in-memory copy away. The first
    /// capture runs to genesis (<see cref="StateId.PreGenesis"/>); a resume stops past the last-captured block.
    /// Availability is driven by the per-block <c>AvailableBlocks</c> markers, not the watermark.
    /// </remarks>
    public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository)
    {
        if (!_enabled) return;

        ulong target = persistedHead.BlockNumber;
        if (_anyCaptured && target <= _lastCapturedBlock) return;

        bool resuming = _anyCaptured;
        ulong lastCaptured = _lastCapturedBlock;

        StateId current = persistedHead;
        while (current != StateId.PreGenesis && (!resuming || current.BlockNumber > lastCaptured))
        {
            if (snapshotRepository.TryLeaseInMemoryState(current, SnapshotTier.InMemoryBase, out Snapshot? snapshot))
            {
                using (snapshot)
                {
                    CaptureBlock(current.BlockNumber, snapshot);
                    current = snapshot.From;
                }
            }
            else if (snapshotRepository.TryLeaseBasePersistedSnapshot(current, out PersistedSnapshot? persisted))
            {
                using (persisted)
                {
                    CaptureBlock(current.BlockNumber, persisted);
                    current = persisted.From;
                }
            }
            else
            {
                break;
            }
        }

        // A lease miss at an already-captured block is the expected restart resume (the watermark is process-local);
        // an uncaptured one is a genuine gap a later as-of read floor-seeks past, so surface it.
        bool reachedFloor = current == StateId.PreGenesis || (resuming && current.BlockNumber <= lastCaptured);
        if (!reachedFloor && !IsBlockCaptured(current.BlockNumber) && _logger.IsWarn)
            _logger.Warn($"History capture stopped early at {current}, which was never captured: as-of reads for keys last changed there may resolve to an earlier value.");

        MarkCaptured(target);
    }

    private void MarkCaptured(ulong block)
    {
        _lastCapturedBlock = block;
        _anyCaptured = true;
    }

    [SkipLocalsInit]
    private bool IsBlockCaptured(ulong block)
    {
        Span<byte> key = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, block);
        return _availableBlocks.KeyExists(key);
    }

    /// <summary>
    /// Seeds the genesis (block 0) changeset from the chain's initial allocations, for a node that enabled history
    /// after genesis left memory and so cannot capture block 0 via the walk. Anchors the floor that dormant genesis
    /// allocations floor-seek to.
    /// </summary>
    /// <remarks>Must run at startup before block processing begins: it writes the history columns without the
    /// persistence lock that serializes <see cref="CaptureUpTo"/>, so it must not overlap a capture.</remarks>
    [SkipLocalsInit]
    public void SeedGenesis(IReadOnlyCollection<KeyValuePair<Address, Account>> allocations)
    {
        if (!_enabled) return;

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        HistoryColumnBatches columns = new(batch);
        MarkBlockAvailable(batch, 0);

        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        foreach (KeyValuePair<Address, Account> allocation in allocations)
        {
            RecordAccount(0, allocation.Key.ToAccountPath, allocation.Value, accountKey, in columns);
        }
    }

    [SkipLocalsInit]
    private void CaptureBlock(ulong block, Snapshot snapshot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        HistoryColumnBatches columns = new(batch);
        MarkBlockAvailable(batch, block);

        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        foreach (KeyValuePair<HashedKey<Address>, bool> destructed in snapshot.SelfDestructedStorageAddresses)
        {
            // Value == true means the account had no persisted storage before the destruct; PersistenceManager
            // skips the flat range-delete in that case, so there is nothing in history to shadow either.
            if (destructed.Value) continue;

            _storageClears.RecordClear(block, BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, destructed.Key.Key.ToAccountPath), columns.StorageClears);
        }

        foreach (KeyValuePair<HashedKey<Address>, Account?> change in snapshot.Accounts)
        {
            RecordAccount(block, change.Key.Key.ToAccountPath, change.Value, accountKey, in columns);
        }

        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> change in snapshot.Storages)
        {
            (Address addr, UInt256 slot) = change.Key.Key;
            RecordStorage(block, addr.ToAccountPath, slot, change.Value, storageKey, storageValue, in columns);
        }
    }

    /// <summary>
    /// Captures a block whose in-memory base was converted to the persisted tier by long-finality Phase 2 — the
    /// persisted base holds the same one-block changeset.
    /// </summary>
    [SkipLocalsInit]
    private void CaptureBlock(ulong block, PersistedSnapshot snapshot)
    {
        using WholeReadSession session = snapshot.BeginWholeReadSession();
        WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, snapshot);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
        HistoryColumnBatches columns = new(batch);
        MarkBlockAvailable(batch, block);

        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
        {
            ValueHash256 addrHash = entry.Address.ToAccountPath;

            if (entry.SelfDestructFlag is false)
            {
                _storageClears.RecordClear(block, BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, addrHash), columns.StorageClears);
            }

            if (entry.HasAccount)
            {
                RecordAccount(block, addrHash, entry.Account, accountKey, in columns);
            }

            foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
            {
                RecordStorage(block, addrHash, slot.Slot, slot.Value, storageKey, storageValue, in columns);
            }
        }
    }

    private void RecordAccount(ulong block, in ValueHash256 addrHash, Account? account, Span<byte> keyBuffer, scoped in HistoryColumnBatches columns)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(keyBuffer, addrHash);

        if (account is null)
        {
            _accountHistory.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, columns.AccountHistory, columns.AccountChangeMarkers);
            return;
        }

        using ArrayPoolSpan<byte> value = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        _accountHistory.RecordChange(block, flatKey, value, columns.AccountHistory, columns.AccountChangeMarkers);
    }

    private void RecordStorage(ulong block, in ValueHash256 addrHash, in UInt256 slot, in SlotValue? value, Span<byte> keyBuffer, Span<byte> valueBuffer, scoped in HistoryColumnBatches columns)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(keyBuffer, addrHash, slotHash);

        // A removed slot, or one stripped to empty (zero), is a tombstone — matching the flat column,
        // which removes / stores an empty value in the same cases.
        int written = value is SlotValue slotValue
            ? BaseFlatPersistence.EncodeSlotValue(slotValue, _rlpWrapSlots, valueBuffer)
            : 0;
        _storageHistory.RecordChange(block, flatKey, valueBuffer[..written], columns.StorageHistory, columns.StorageChangeMarkers);
    }

    [SkipLocalsInit]
    private static void MarkBlockAvailable(IColumnsWriteBatch<FlatHistoryColumns> batch, ulong block)
    {
        Span<byte> blockKey = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(blockKey, block);
        batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks).Set(blockKey, Array.Empty<byte>());
    }

    private readonly ref struct HistoryColumnBatches(IColumnsWriteBatch<FlatHistoryColumns> batch)
    {
        public readonly IWriteBatch AccountHistory = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
        public readonly IWriteBatch AccountChangeMarkers = batch.GetColumnBatch(FlatHistoryColumns.AccountChangeSets);
        public readonly IWriteBatch StorageHistory = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);
        public readonly IWriteBatch StorageChangeMarkers = batch.GetColumnBatch(FlatHistoryColumns.StorageChangeSets);
        public readonly IWriteBatch StorageClears = batch.GetColumnBatch(FlatHistoryColumns.StorageClears);
    }
}
