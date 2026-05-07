// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class AccountChangesDecoder : IRlpValueDecoder<AccountChanges>, IRlpStreamEncoder<AccountChanges>
{
    private static AccountChangesDecoder? _instance = null;
    public static AccountChangesDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _slotsLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _storageLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _txLimit = new(Eip7928Constants.MaxTxs, "", ReadOnlyMemory<char>.Empty);

    public AccountChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        Address address = ctx.DecodeAddress();

        SlotChanges[] slotChanges = ctx.DecodeArray(SlotChangesDecoder.Instance, true, default, _slotsLimit);
        UInt256? lastSlot = null;
        foreach (SlotChanges slotChange in slotChanges)
        {
            if (slotChange is null)
            {
                ThrowEmptySlotChanges();
            }

            UInt256 slot = slotChange.Key;
            if (lastSlot is not null && slot <= lastSlot)
            {
                ThrowStorageChangesOutOfOrder();
            }
            lastSlot = slot;
        }

        UInt256[] storageReads = ctx.DecodeArray(UInt256Decoder.Instance, true, default, _storageLimit);
        SortedSet<UInt256> storageReadsList = new(GenericComparer.GetOptimized<UInt256>());
        UInt256? lastRead = null;
        foreach (UInt256 storageRead in storageReads)
        {
            if (lastRead is not null && storageRead.CompareTo(lastRead.Value) <= 0)
            {
                ThrowStorageReadsOutOfOrder();
            }
            if (ContainsStorageChange(slotChanges, storageRead))
            {
                ThrowInvalidStorageRead();
            }
            storageReadsList.Add(storageRead);
            lastRead = storageRead;
        }

        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance, true, default, _txLimit);
        IndexedChanges<BalanceChange> balanceChangesList = ToIndexedChanges(balanceChanges, "Balance");

        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance, true, default, _txLimit);
        IndexedChanges<NonceChange> nonceChangesList = ToIndexedChanges(nonceChanges, "Nonce");

        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance, true, default, _txLimit);
        IndexedChanges<CodeChange> codeChangesList = ToIndexedChanges(codeChanges, "Code");

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            ctx.Check(check);
        }

        return AccountChanges.FromSortedStorageChanges(address, slotChanges, storageReadsList, balanceChangesList, nonceChangesList, codeChangesList);
    }

    public int GetLength(AccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public void Encode(RlpStream stream, AccountChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Address);
        EncodeSequence(stream, item.StorageChanges, SlotChangesDecoder.Instance, rlpBehaviors);
        EncodeStorageReads(stream, item.StorageReads, rlpBehaviors);
        EncodeIndexedChanges(stream, item.BalanceChangeSet, BalanceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexedChanges(stream, item.NonceChangeSet, NonceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexedChanges(stream, item.CodeChangeSet, CodeChangeDecoder.Instance, rlpBehaviors);
    }

    public static int GetContentLength(AccountChanges item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfAddressRlp
            + SequenceLength(item.StorageChanges, SlotChangesDecoder.Instance, rlpBehaviors)
            + StorageReadsSequenceLength(item.StorageReads, rlpBehaviors)
            + IndexedSequenceLength(item.BalanceChangeSet, BalanceChangeDecoder.Instance, rlpBehaviors)
            + IndexedSequenceLength(item.NonceChangeSet, NonceChangeDecoder.Instance, rlpBehaviors)
            + IndexedSequenceLength(item.CodeChangeSet, CodeChangeDecoder.Instance, rlpBehaviors);

    private static void EncodeSequence<T>(RlpStream stream, IList<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
    {
        int contentLength = SequenceContentLength(items, encoder, rlpBehaviors);
        stream.StartSequence(contentLength);
        for (int i = 0; i < items.Count; i++)
        {
            encoder.Encode(stream, items[i], rlpBehaviors);
        }
    }

    private static int SequenceLength<T>(IList<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(SequenceContentLength(items, encoder, rlpBehaviors));

    private static int SequenceContentLength<T>(IList<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
    {
        int length = 0;
        for (int i = 0; i < items.Count; i++)
        {
            length += encoder.GetLength(items[i], rlpBehaviors);
        }
        return length;
    }

    private static void EncodeStorageReads(RlpStream stream, SortedSet<UInt256> items, RlpBehaviors rlpBehaviors)
    {
        int contentLength = StorageReadsContentLength(items, rlpBehaviors);
        stream.StartSequence(contentLength);
        foreach (UInt256 item in items)
        {
            UInt256Decoder.Instance.Encode(stream, item, rlpBehaviors);
        }
    }

    private static int StorageReadsSequenceLength(SortedSet<UInt256> items, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(StorageReadsContentLength(items, rlpBehaviors));

    private static int StorageReadsContentLength(SortedSet<UInt256> items, RlpBehaviors rlpBehaviors)
    {
        int length = 0;
        foreach (UInt256 item in items)
        {
            length += UInt256Decoder.Instance.GetLength(item, rlpBehaviors);
        }
        return length;
    }

    private static void EncodeIndexedChanges<T>(RlpStream stream, IndexedChanges<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
        where T : struct, IIndexedChange
    {
        int contentLength = IndexedSequenceContentLength(items, encoder, rlpBehaviors);
        stream.StartSequence(contentLength);
        if (items.HasPrestate)
        {
            encoder.Encode(stream, items.Prestate, rlpBehaviors);
        }

        ReadOnlySpan<T> blockAccessChanges = items.BlockAccessChanges;
        for (int i = 0; i < blockAccessChanges.Length; i++)
        {
            encoder.Encode(stream, blockAccessChanges[i], rlpBehaviors);
        }
    }

    private static int IndexedSequenceLength<T>(IndexedChanges<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
        where T : struct, IIndexedChange =>
        Rlp.LengthOfSequence(IndexedSequenceContentLength(items, encoder, rlpBehaviors));

    private static int IndexedSequenceContentLength<T>(IndexedChanges<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
        where T : struct, IIndexedChange
    {
        int length = 0;
        if (items.HasPrestate)
        {
            length += encoder.GetLength(items.Prestate, rlpBehaviors);
        }

        ReadOnlySpan<T> blockAccessChanges = items.BlockAccessChanges;
        for (int i = 0; i < blockAccessChanges.Length; i++)
        {
            length += encoder.GetLength(blockAccessChanges[i], rlpBehaviors);
        }

        return length;
    }

    private static IndexedChanges<T> ToIndexedChanges<T>(T[] items, string changeName)
        where T : struct, IIndexedChange
    {
        uint? lastIndex = null;
        IndexedChanges<T> indexed = new(items.Length);
        foreach (T item in items)
        {
            uint index = item.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                ThrowIndexedChangesOutOfOrder(changeName);
            }
            lastIndex = index;
            indexed.Add(item);
        }
        return indexed;
    }

    private static bool ContainsStorageChange(SlotChanges[] sortedSlotChanges, UInt256 key)
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
    private static void ThrowEmptySlotChanges() =>
        throw new RlpException("Empty SlotChanges entry; EIP-7928 requires a 2-field sequence.");

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
