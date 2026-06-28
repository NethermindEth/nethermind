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
/// Captures finalized per-block changesets into the history columns before the per-block snapshots are pruned.
/// For each newly-finalized block it leases that block's per-block snapshot and records every changed account
/// and storage slot via <see cref="HistoryStore"/>, using the exact flat key/value encoders so the recorded
/// bytes are identical to what the live flat columns store. A deleted account or zeroed/removed slot is recorded
/// as an empty (tombstone) value.
/// </summary>
public sealed class HistoryWriter : IFlatPersistenceCaptureHook
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly HistoryStore _accountHistory;
    private readonly HistoryStore _storageHistory;
    private readonly bool _rlpWrapSlots;
    private readonly bool _enabled;

    private ulong _lastCapturedBlock;
    private bool _anyCaptured;

    public HistoryWriter(IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
        : this(db, BasePersistence.ResolveSlotEncoding(
            db,
            (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage),
            logManager.GetClassLogger<HistoryWriter>()), config.HistoryEnabled)
    {
    }

    private HistoryWriter(IColumnsDb<FlatDbColumns> db, bool rlpWrapSlots, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(db);
        _enabled = enabled;
        _db = db;
        _rlpWrapSlots = rlpWrapSlots;
        _accountHistory = new HistoryStore(
            db.GetColumnDb(FlatDbColumns.AccountHistory),
            db.GetColumnDb(FlatDbColumns.AccountChangeSets));
        _storageHistory = new HistoryStore(
            db.GetColumnDb(FlatDbColumns.StorageHistory),
            db.GetColumnDb(FlatDbColumns.StorageChangeSets));
    }

    public ulong LastCapturedBlock => _lastCapturedBlock;

    /// <summary>
    /// Captures the changeset of every block on <paramref name="persistedHead"/>'s chain that has not yet been
    /// captured, up to and including <paramref name="persistedHead"/>. Must run before the per-block snapshots are
    /// pruned. Blocks whose per-block snapshot is no longer leasable are skipped (their state already left memory).
    /// </summary>
    public void CaptureUpTo(in StateId persistedHead, ISnapshotRepository snapshotRepository)
    {
        if (!_enabled) return;

        ulong target = persistedHead.BlockNumber;
        if (_anyCaptured && target <= _lastCapturedBlock) return;

        for (ulong block = _anyCaptured ? _lastCapturedBlock + 1 : 0; block <= target; block++)
        {
            if (!snapshotRepository.TryFindAncestorStateAtBlock(persistedHead, block, out StateId stateAtBlock))
            {
                MarkCaptured(block);
                continue;
            }

            if (!snapshotRepository.TryLeaseState(stateAtBlock, out Snapshot? snapshot))
            {
                // Genesis / already-pruned blocks have no per-block snapshot; nothing to record.
                MarkCaptured(block);
                continue;
            }

            using (snapshot)
            {
                CaptureBlock(block, snapshot);
            }

            MarkCaptured(block);
        }
    }

    private void MarkCaptured(ulong block)
    {
        _lastCapturedBlock = block;
        _anyCaptured = true;
    }

    [SkipLocalsInit]
    private void CaptureBlock(ulong block, Snapshot snapshot)
    {
        using IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch();

        IWriteBatch accountHistory = batch.GetColumnBatch(FlatDbColumns.AccountHistory);
        IWriteBatch accountChangeMarkers = batch.GetColumnBatch(FlatDbColumns.AccountChangeSets);
        IWriteBatch storageHistory = batch.GetColumnBatch(FlatDbColumns.StorageHistory);
        IWriteBatch storageChangeMarkers = batch.GetColumnBatch(FlatDbColumns.StorageChangeSets);

        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
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
