// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Segmented;

/// <summary>
/// Approach-2 counterpart of <see cref="HistoryReader"/>: reads finalized historical state "as of block B" from the
/// memory-mapped Elias-Fano segments instead of the RocksDB history columns. The flat-key encoding, RLP decoding,
/// tombstone and self-destruct-shadowing semantics are identical to <see cref="HistoryReader"/>; only the backing
/// change index differs (<see cref="SegmentHistoryStore"/> predecessor lookup vs. composite-key floor-seek).
/// </summary>
/// <remarks>
/// The three stores are shared, not owned: the same instances back <see cref="SegmentHistoryWriter"/> so the
/// reader sees the writer's still-buffered current step as well as the sealed segments. Ownership/disposal belongs
/// to whoever constructed them (the DI module), never this reader.
/// <para>
/// This deliberately duplicates the read glue from <see cref="HistoryReader"/> rather than sharing it: Approach 1
/// is a shipping code path that must stay untouched, and the two backends have unrelated storage models. If the two
/// readers converge, extract a shared base keyed on a read-only store abstraction.
/// </para>
/// </remarks>
public sealed class SegmentHistoryReader
{
    // Slim-format account RLP is at most nonce + balance + two 32-byte hashes; 256 bytes is ample headroom.
    private const int AccountValueBufferSize = 256;

    private readonly SegmentHistoryStore _accountHistory;
    private readonly SegmentHistoryStore _storageHistory;
    private readonly SegmentHistoryStore _storageClears;
    private readonly bool _rlpWrapSlots;

    /// <param name="accountHistory">Account change index; also the source of block-coverage via
    /// <see cref="SegmentHistoryStore.CoversBlock"/>.</param>
    /// <param name="storageHistory">Storage-slot change index.</param>
    /// <param name="storageClears">Valueless storage-clear (self-destruct) event index.</param>
    /// <param name="rlpWrapSlots">Whether stored slot values are RLP-wrapped, matching the live flat column encoding
    /// (resolved once by the caller via <c>BasePersistence.ResolveSlotEncoding</c>).</param>
    public SegmentHistoryReader(
        SegmentHistoryStore accountHistory,
        SegmentHistoryStore storageHistory,
        SegmentHistoryStore storageClears,
        bool rlpWrapSlots)
    {
        ArgumentNullException.ThrowIfNull(accountHistory);
        ArgumentNullException.ThrowIfNull(storageHistory);
        ArgumentNullException.ThrowIfNull(storageClears);
        _accountHistory = accountHistory;
        _storageHistory = storageHistory;
        _storageClears = storageClears;
        _rlpWrapSlots = rlpWrapSlots;
    }

    /// <summary>Whether a historical read at <paramref name="block"/> can be served. Unlike Approach 1 (which keeps a
    /// per-block AvailableBlocks marker column), coverage comes from the account index's captured range.</summary>
    public bool HasHistoryForBlock(ulong block) => _accountHistory.CoversBlock(block);

    /// <summary>
    /// Resolves the account as of <paramref name="block"/>. Returns <c>false</c> when the account did not exist at
    /// that block — either it never changed at/before it, or its latest change at/before it was a deletion.
    /// </summary>
    [SkipLocalsInit]
    public bool TryGetAccount(ulong block, Address address, out AccountStruct account)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], address.ToAccountPath);

        Span<byte> valueBuffer = stackalloc byte[AccountValueBufferSize];
        int written = _accountHistory.TryGetAt(block, flatKey, valueBuffer, out _);
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
    public bool TryGetStorage(ulong block, Address address, in UInt256 index, out SlotValue value)
    {
        ValueHash256 addrHash = address.ToAccountPath;
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(index, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], addrHash, slotHash);

        Span<byte> valueBuffer = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = _storageHistory.TryGetAt(block, flatKey, valueBuffer, out ulong changedAtBlock);
        if (written <= 0) // -1 = never changed at/before block, 0 = cleared tombstone
        {
            value = default;
            return false;
        }

        // A self-destruct between the slot's last write and the read block kills the value. The live column
        // expresses the destruct as a range-delete, which leaves no per-slot tombstone in the history.
        ReadOnlySpan<byte> accountKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], addrHash);
        if (_storageClears.HasChangeInRange(accountKey, changedAtBlock, block))
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
