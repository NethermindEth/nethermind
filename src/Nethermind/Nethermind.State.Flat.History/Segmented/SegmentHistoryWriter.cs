// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Segmented;

/// <summary>
/// Approach-2 counterpart of <see cref="HistoryWriter"/>: captures finalized per-block changesets into the
/// memory-mapped Elias-Fano segments instead of the RocksDB history columns. The change detection, flat-key/value
/// encoding and tombstone semantics are identical to <see cref="HistoryWriter"/>; only the sink differs — changes
/// are handed to <see cref="SegmentHistoryStore"/>, which buffers the current step in memory and seals it into an
/// immutable segment at a step/size boundary.
/// </summary>
/// <remarks>
/// The three stores are shared with <see cref="SegmentHistoryReader"/> and owned by the DI module, which must
/// dispose them on shutdown so the last partial (unsealed) step is flushed to disk. Unlike Approach 1, there is no
/// per-block cross-column atomic commit: a crash loses only the current unsealed step, which the store re-derives
/// on resume by dropping any re-delivered block at or below its durable frontier.
/// <para>
/// This deliberately duplicates the capture glue from <see cref="HistoryWriter"/> rather than sharing it, to keep
/// the shipping Approach-1 writer untouched; see <see cref="SegmentHistoryReader"/> for the same rationale.
/// </para>
/// </remarks>
public sealed class SegmentHistoryWriter : IFlatPersistenceCaptureHook
{
    private readonly SegmentHistoryStore _accountHistory;
    private readonly SegmentHistoryStore _storageHistory;
    private readonly SegmentHistoryStore _storageClears;
    private readonly bool _rlpWrapSlots;
    private readonly bool _enabled;

    private ulong _lastCapturedBlock;
    private bool _anyCaptured;

    /// <param name="accountHistory">Account change index (shared with the reader).</param>
    /// <param name="storageHistory">Storage-slot change index (shared with the reader).</param>
    /// <param name="storageClears">Valueless storage-clear (self-destruct) event index (shared with the reader).</param>
    /// <param name="rlpWrapSlots">Whether slot values must be RLP-wrapped to match the live flat column encoding.</param>
    /// <param name="enabled">Whether history capture is on; when false, <see cref="CaptureUpTo"/> is a no-op.</param>
    public SegmentHistoryWriter(
        SegmentHistoryStore accountHistory,
        SegmentHistoryStore storageHistory,
        SegmentHistoryStore storageClears,
        bool rlpWrapSlots,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(accountHistory);
        ArgumentNullException.ThrowIfNull(storageHistory);
        ArgumentNullException.ThrowIfNull(storageClears);
        _accountHistory = accountHistory;
        _storageHistory = storageHistory;
        _storageClears = storageClears;
        _rlpWrapSlots = rlpWrapSlots;
        _enabled = enabled;
    }

    public ulong LastCapturedBlock => _lastCapturedBlock;

    /// <summary>
    /// Captures the changeset of every block on <paramref name="persistedHead"/>'s chain that has not yet been
    /// captured, up to and including <paramref name="persistedHead"/>. Must run before the per-block snapshots are
    /// pruned. Blocks whose per-block snapshot is no longer leasable are skipped (their state already left memory)
    /// and, matching Approach 1, are left outside the covered range.
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
        Span<byte> accountKey = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        foreach (KeyValuePair<HashedKey<Address>, bool> destructed in snapshot.SelfDestructedStorageAddresses)
        {
            // Value == true means the account had no persisted storage before the destruct; PersistenceManager
            // skips the flat range-delete in that case, so there is nothing in history to shadow either.
            if (destructed.Value) continue;

            ValueHash256 destructedAddrHash = destructed.Key.Key.ToAccountPath;
            _storageClears.RecordChange(block, BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, destructedAddrHash), ReadOnlySpan<byte>.Empty);
        }

        foreach (KeyValuePair<HashedKey<Address>, Account?> change in snapshot.Accounts)
        {
            ValueHash256 addrHash = change.Key.Key.ToAccountPath;
            ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(accountKey, addrHash);

            if (change.Value is null)
            {
                _accountHistory.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty);
                continue;
            }

            using ArrayPoolSpan<byte> value = AccountDecoder.Slim.EncodeToArrayPoolSpan(change.Value);
            _accountHistory.RecordChange(block, flatKey, value);
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
            _storageHistory.RecordChange(block, flatKey, storageValue[..written]);
        }

        // Mark this block complete in every domain so the covered range advances in lockstep and step sealing can
        // fire. Only leased (captured) blocks are completed, so skipped genesis/pruned blocks stay uncovered.
        _accountHistory.CompleteBlock(block);
        _storageHistory.CompleteBlock(block);
        _storageClears.CompleteBlock(block);
    }
}
