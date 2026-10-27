// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class AccountChangesDecoder : RlpDecoder<ReadOnlyAccountChanges>
{
    public static readonly AccountChangesDecoder Instance = new();

    private static readonly RlpLimit _slotsLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _storageLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _txLimit = new(Eip7928Constants.MaxTxs, "", ReadOnlyMemory<char>.Empty);

    protected override ReadOnlyAccountChanges DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        Address address = ctx.DecodeAddress();

        ReadOnlySlotChanges[] slotChanges = ctx.DecodeNonNullArray(SlotChangesDecoder.Instance, limit: _slotsLimit);
        UInt256? lastSlot = null;
        foreach (ReadOnlySlotChanges slotChange in slotChanges)
        {
            UInt256 slot = slotChange.Key;
            if (lastSlot is not null && slot <= lastSlot)
            {
                ThrowStorageChangesOutOfOrder();
            }
            lastSlot = slot;
        }

        UInt256[] storageReads = UInt256Decoder.Instance.DecodeNonNullArray(ref ctx, RlpBehaviors.None, _storageLimit);
        UInt256? lastRead = null;
        foreach (UInt256 storageRead in storageReads)
        {
            if (lastRead is not null && storageRead.CompareTo(lastRead.Value) <= 0)
            {
                ThrowStorageReadsOutOfOrder();
            }
            // slotChanges is sorted ascending (verified above), so a binary search avoids the
            // per-account HashSet allocation a naive O(1) approach would require.
            if (ContainsStorageChange(slotChanges, storageRead))
            {
                ThrowInvalidStorageRead();
            }
            lastRead = storageRead;
        }

        BalanceChange[] balanceChanges = BalanceChangeDecoder.Instance.DecodeNonNullArray(ref ctx, RlpBehaviors.None, _txLimit);
        ValidateSortedByIndex(balanceChanges, "Balance");

        NonceChange[] nonceChanges = NonceChangeDecoder.Instance.DecodeNonNullArray(ref ctx, RlpBehaviors.None, _txLimit);
        ValidateSortedByIndex(nonceChanges, "Nonce");

        CodeChange[] codeChanges = CodeChangeDecoder.Instance.DecodeNonNullArray(ref ctx, RlpBehaviors.None, _txLimit);
        ValidateSortedByIndex(codeChanges, "Code");

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            ctx.Check(check);
        }

        return new ReadOnlyAccountChanges(address, slotChanges, storageReads, balanceChanges, nonceChanges, codeChanges);
    }

    public override void PopulateLength(ReadOnlyAccountChanges item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        int contentLength = Rlp.LengthOfAddressRlp
            + CodeChangeDecoder.Instance.PopulateSequenceAndPeek(item.CodeChanges, ref lengths, rlpBehaviors)
            + NonceChangeDecoder.Instance.PopulateSequenceAndPeek(item.NonceChanges, ref lengths, rlpBehaviors)
            + BalanceChangeDecoder.Instance.PopulateSequenceAndPeek(item.BalanceChanges, ref lengths, rlpBehaviors)
            + UInt256Decoder.Instance.PopulateSequenceAndPeek(item.StorageReads, ref lengths, rlpBehaviors)
            + SlotChangesDecoder.Instance.PopulateSequenceAndPeek(item.StorageChanges, ref lengths, rlpBehaviors);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    /// <remarks>
    /// Storage changes and reads are sorted only when written, so only their sequence lengths are pushed here;
    /// each storage change is populated on top of the stack right before it is written.
    /// </remarks>
    public void PopulateLength(GeneratedAccountChanges item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = Rlp.LengthOfAddressRlp
            + CodeChangeDecoder.Instance.PopulateSequenceAndPeek(CollectionsMarshal.AsSpan(item.CodeChanges), ref lengths, rlpBehaviors)
            + NonceChangeDecoder.Instance.PopulateSequenceAndPeek(CollectionsMarshal.AsSpan(item.NonceChanges), ref lengths, rlpBehaviors)
            + BalanceChangeDecoder.Instance.PopulateSequenceAndPeek(CollectionsMarshal.AsSpan(item.BalanceChanges), ref lengths, rlpBehaviors)
            + PushSequenceLength(UInt256ContentLength(item.StorageReads), ref lengths)
            + PushSequenceLength(GeneratedSlotChangesContentLength(item.StorageChanges, rlpBehaviors), ref lengths);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    public override void Encode<TWriter>(ReadOnlyAccountChanges item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        lengths.Pop();
        writer.StartSequence(lengths.Pop());
        writer.Encode(item.Address);
        SlotChangesDecoder.Instance.EncodeSequence(item.StorageChanges, ref lengths, ref writer, rlpBehaviors);
        UInt256Decoder.Instance.EncodeSequence(item.StorageReads, ref lengths, ref writer, rlpBehaviors);
        BalanceChangeDecoder.Instance.EncodeSequence(item.BalanceChanges, ref lengths, ref writer, rlpBehaviors);
        NonceChangeDecoder.Instance.EncodeSequence(item.NonceChanges, ref lengths, ref writer, rlpBehaviors);
        CodeChangeDecoder.Instance.EncodeSequence(item.CodeChanges, ref lengths, ref writer, rlpBehaviors);
    }

    public void Encode<TWriter>(GeneratedAccountChanges item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        lengths.Pop();
        writer.StartSequence(lengths.Pop());
        writer.Encode(item.Address);

        lengths.Pop();
        writer.StartSequence(lengths.Pop());
        using (ArrayPoolListRef<GeneratedSlotChanges> sortedSlots = item.GetSortedStorageChanges())
        {
            foreach (GeneratedSlotChanges slotChanges in sortedSlots.AsSpan())
            {
                SlotChangesDecoder.Instance.PopulateLength(slotChanges, ref lengths, rlpBehaviors);
                SlotChangesDecoder.Instance.Encode(slotChanges, ref lengths, ref writer, rlpBehaviors);
            }
        }

        lengths.Pop();
        writer.StartSequence(lengths.Pop());
        using (ArrayPoolListRef<UInt256> sortedReads = item.GetSortedStorageReads())
        {
            foreach (UInt256 storageRead in sortedReads.AsSpan())
            {
                writer.Encode(storageRead);
            }
        }

        BalanceChangeDecoder.Instance.EncodeSequence(CollectionsMarshal.AsSpan(item.BalanceChanges), ref lengths, ref writer, rlpBehaviors);
        NonceChangeDecoder.Instance.EncodeSequence(CollectionsMarshal.AsSpan(item.NonceChanges), ref lengths, ref writer, rlpBehaviors);
        CodeChangeDecoder.Instance.EncodeSequence(CollectionsMarshal.AsSpan(item.CodeChanges), ref lengths, ref writer, rlpBehaviors);
    }

    private static int PushSequenceLength(int contentLength, ref LengthStack lengths)
    {
        lengths.Push(contentLength);
        int length = Rlp.LengthOfSequence(contentLength);
        lengths.Push(length);
        return length;
    }

    private static int GeneratedSlotChangesContentLength(IReadOnlyCollection<GeneratedSlotChanges> items, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (GeneratedSlotChanges s in items) len += Rlp.LengthOfSequence(SlotChangesDecoder.GetContentLength(s, rlpBehaviors));
        return len;
    }

    private static int UInt256ContentLength(IReadOnlyCollection<UInt256> items)
    {
        int len = 0;
        foreach (UInt256 v in items) len += Rlp.LengthOf(v);
        return len;
    }

    private static void ValidateSortedByIndex<T>(T[] items, string changeName)
        where T : struct, IIndexedChange
    {
        uint? lastIndex = null;
        foreach (T item in items)
        {
            uint index = item.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                ThrowIndexedChangesOutOfOrder(changeName);
            }
            lastIndex = index;
        }
    }

    /// <summary>Binary search over <paramref name="sortedSlotChanges"/> (sorted by key, verified
    /// during decode) for <paramref name="key"/>. O(log n) lookup with no allocation, matching
    /// master's behaviour for the storage-read-vs-change overlap check.</summary>
    private static bool ContainsStorageChange(ReadOnlySlotChanges[] sortedSlotChanges, in UInt256 key)
    {
        int low = 0;
        int high = sortedSlotChanges.Length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int compare = sortedSlotChanges[mid].Key.CompareTo(key);
            if (compare == 0)
            {
                return true;
            }

            if (compare < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageChangesOutOfOrder() =>
        throw new RlpException("Storage changes were in incorrect order.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageReadsOutOfOrder() =>
        throw new RlpException("Storage reads were in incorrect order.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidStorageRead() =>
        throw new RlpException("Invalid storage read, already in storage changes.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIndexedChangesOutOfOrder(string changeName) =>
        throw new RlpException($"{changeName} changes were in incorrect order.");
}
