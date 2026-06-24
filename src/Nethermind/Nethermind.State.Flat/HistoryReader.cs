// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

/// <summary>
/// Reads finalized historical state "as of block B" from the history columns. The mirror of
/// <see cref="HistoryWriter"/>: encode the flat key, floor-seek the value at B via <see cref="HistoryStore"/>,
/// decode with the same flat account/slot format the live columns use. Serves block-parameter reads below the
/// finalization barrier, where the per-block snapshots have already been pruned.
/// </summary>
public sealed class HistoryReader
{
    // Slim-format account RLP is at most nonce + balance + two 32-byte hashes; 256 bytes is ample headroom.
    private const int AccountValueBufferSize = 256;

    private readonly HistoryStore _accountHistory;
    private readonly HistoryStore _storageHistory;
    private readonly bool _rlpWrapSlots;

    public HistoryReader(IColumnsDb<FlatDbColumns> db, ILogManager logManager)
        : this(db, BasePersistence.ResolveSlotEncoding(
            db,
            (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage),
            logManager.GetClassLogger<HistoryReader>()))
    {
    }

    internal HistoryReader(IColumnsDb<FlatDbColumns> db, bool rlpWrapSlots)
    {
        ArgumentNullException.ThrowIfNull(db);
        _rlpWrapSlots = rlpWrapSlots;
        _accountHistory = new HistoryStore(
            db.GetColumnDb(FlatDbColumns.AccountHistory),
            db.GetColumnDb(FlatDbColumns.AccountChangeSets));
        _storageHistory = new HistoryStore(
            db.GetColumnDb(FlatDbColumns.StorageHistory),
            db.GetColumnDb(FlatDbColumns.StorageChangeSets));
    }

    /// <summary>
    /// Resolves the account as of <paramref name="block"/>. Returns <c>false</c> when the account did not exist at
    /// that block — either it never changed at/before it, or its latest change at/before it was a deletion.
    /// </summary>
    [SkipLocalsInit]
    public bool TryGetAccount(long block, Address address, out AccountStruct account)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], address.ToAccountPath);

        Span<byte> valueBuffer = stackalloc byte[AccountValueBufferSize];
        int written = _accountHistory.TryGetAt(block, flatKey, valueBuffer);
        if (written <= 0) // -1 = never changed at/before block, 0 = deletion tombstone
        {
            account = default;
            return false;
        }

        RlpReader context = new(valueBuffer[..written]);
        return AccountDecoder.Slim.TryDecodeStruct(ref context, out account);
    }

    /// <summary>
    /// Resolves the storage slot as of <paramref name="block"/>. Returns <c>false</c> when the slot was unset at
    /// that block — either it never changed at/before it, or its latest change at/before it cleared it.
    /// </summary>
    [SkipLocalsInit]
    public bool TryGetStorage(long block, Address address, in UInt256 index, out SlotValue value)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(index, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], address.ToAccountPath, slotHash);

        Span<byte> valueBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = _storageHistory.TryGetAt(block, flatKey, valueBuffer);
        if (written <= 0) // -1 = never changed at/before block, 0 = cleared tombstone
        {
            value = default;
            return false;
        }

        ReadOnlySpan<byte> stored = valueBuffer[..written];
        if (_rlpWrapSlots)
        {
            RlpReader context = new(stored);
            stored = context.DecodeByteArraySpan();
        }

        value = SlotValue.FromSpanWithoutLeadingZero(stored);
        return true;
    }
}
