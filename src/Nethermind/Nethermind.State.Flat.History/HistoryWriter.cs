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
    }

    public ulong LastCapturedBlock => _lastCapturedBlock;

    /// <summary>
    /// Captures the changeset of every block on <paramref name="persistedHead"/>'s chain that has not yet been
    /// captured, up to and including <paramref name="persistedHead"/>. Must run before the per-block snapshots are
    /// pruned.
    /// </summary>
    /// <remarks>
    /// Walks backwards through the per-block base snapshots' <see cref="Snapshot.From"/> links — each base is
    /// exactly one block's changeset, so the walk follows persistedHead's canonical chain, one lease at a time,
    /// allocating nothing. The first capture runs down to genesis, whose From is the
    /// <see cref="StateId.PreGenesis"/> sentinel; a resume stops once past the last-captured block. Advancing the
    /// watermark past an early stop is safe for reads — availability is driven by the per-block
    /// <c>AvailableBlocks</c> markers, never the watermark — but a block durably persisted and never captured
    /// (a crash between persist and capture) leaves a hole through which a later as-of read floor-seeks to a stale
    /// earlier value; closing it requires capturing in the same batch as the flat persist.
    /// </remarks>
    public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository)
    {
        if (!_enabled) return;

        ulong target = persistedHead.BlockNumber;
        if (_anyCaptured && target <= _lastCapturedBlock) return;

        bool resuming = _anyCaptured;
        ulong lastCaptured = _lastCapturedBlock;

        StateId current = persistedHead;
        while (current != StateId.PreGenesis
               && (!resuming || current.BlockNumber > lastCaptured)
               && snapshotRepository.TryLeaseInMemoryState(current, SnapshotTier.InMemoryBase, out Snapshot? snapshot))
        {
            StateId parent;
            using (snapshot)
            {
                CaptureBlock(current.BlockNumber, snapshot);
                parent = snapshot.From;
            }

            current = parent;
        }

        // A lease miss before the genesis/resume floor is the documented crash-gap; surface it rather than let it
        // widen silently.
        bool reachedFloor = current == StateId.PreGenesis || (resuming && current.BlockNumber <= lastCaptured);
        if (!reachedFloor && _logger.IsWarn)
            _logger.Warn($"History capture stopped early at {current}: its in-memory base is no longer leasable. Blocks down to {(resuming ? lastCaptured + 1 : 0)} are left uncaptured and will report no history.");

        MarkCaptured(target);
    }

    private void MarkCaptured(ulong block)
    {
        _lastCapturedBlock = block;
        _anyCaptured = true;
    }

    [SkipLocalsInit]
    private void CaptureBlock(ulong block, Snapshot snapshot)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();

        IWriteBatch accountHistory = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
        IWriteBatch accountChangeMarkers = batch.GetColumnBatch(FlatHistoryColumns.AccountChangeSets);
        IWriteBatch storageHistory = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);
        IWriteBatch storageChangeMarkers = batch.GetColumnBatch(FlatHistoryColumns.StorageChangeSets);

        Span<byte> blockKey = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(blockKey, block);
        batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks).Set(blockKey, Array.Empty<byte>());

        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        IWriteBatch storageClears = batch.GetColumnBatch(FlatHistoryColumns.StorageClears);
        foreach (KeyValuePair<HashedKey<Address>, bool> destructed in snapshot.SelfDestructedStorageAddresses)
        {
            // Value == true means the account had no persisted storage before the destruct; PersistenceManager
            // skips the flat range-delete in that case, so there is nothing in history to shadow either.
            if (destructed.Value) continue;

            ValueHash256 destructedAddrHash = destructed.Key.Key.ToAccountPath;
            _storageClears.RecordClear(block, BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, destructedAddrHash), storageClears);
        }

        foreach (KeyValuePair<HashedKey<Address>, Account?> change in snapshot.Accounts)
        {
            ValueHash256 addrHash = change.Key.Key.ToAccountPath;
            ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, addrHash);

            if (change.Value is null)
            {
                _accountHistory.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, accountHistory, accountChangeMarkers);
                continue;
            }

            using ArrayPoolSpan<byte> value = AccountDecoder.Slim.EncodeToArrayPoolSpan(change.Value);
            _accountHistory.RecordChange(block, flatKey, value, accountHistory, accountChangeMarkers);
        }

        Span<byte> storageKey = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        Span<byte> storageValue = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> change in snapshot.Storages)
        {
            (Address addr, UInt256 slot) = change.Key.Key;
            ValueHash256 addrHash = addr.ToAccountPath;
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
            ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(storageKey, addrHash, slotHash);

            // A removed slot, or one stripped to empty (zero), is a tombstone — matching the flat column,
            // which removes / stores an empty value in the same cases.
            int written = change.Value is SlotValue value
                ? BaseFlatPersistence.EncodeSlotValue(value, _rlpWrapSlots, storageValue)
                : 0;
            _storageHistory.RecordChange(block, flatKey, storageValue[..written], storageHistory, storageChangeMarkers);
        }
    }
}
