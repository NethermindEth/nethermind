// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        SortedList<UInt256, SlotChanges> slotChangesList = new(slotChanges.Length, GenericComparer.GetOptimized<UInt256>());
        foreach (SlotChanges slotChange in slotChanges)
        {
            UInt256 slot = slotChange.Key;
            if (lastSlot is not null && slot <= lastSlot)
            {
                throw new RlpException("Storage changes were in incorrect order.");
            }
            lastSlot = slot;
            slotChangesList.Add(slot, slotChange);
        }

        UInt256[] storageReads = ctx.DecodeArray(UInt256Decoder.Instance, true, default, _storageLimit);
        SortedSet<UInt256> storageReadsList = new(GenericComparer.GetOptimized<UInt256>());
        UInt256? lastRead = null;
        foreach (UInt256 storageRead in storageReads)
        {
            if (lastRead is not null && storageRead.CompareTo(lastRead.Value) <= 0)
            {
                throw new RlpException("Storage reads were in incorrect order.");
            }
            if (slotChangesList.ContainsKey(storageRead))
            {
                throw new RlpException("Invalid storage read, already in storage changes.");
            }
            storageReadsList.Add(storageRead);
            lastRead = storageRead;
        }

        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance, true, default, _txLimit);
        SortedList<int, BalanceChange> balanceChangesList = ToSortedByIndex(balanceChanges, "Balance");

        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance, true, default, _txLimit);
        SortedList<int, NonceChange> nonceChangesList = ToSortedByIndex(nonceChanges, "Nonce");

        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance, true, default, _txLimit);
        SortedList<int, CodeChange> codeChangesList = ToSortedByIndex(codeChanges, "Code");

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            ctx.Check(check);
        }

        return new(address, slotChangesList, storageReadsList, balanceChangesList, nonceChangesList, codeChangesList);
    }

    public int GetLength(AccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public void Encode(RlpStream stream, AccountChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Address);
        stream.EncodeArray([.. item.StorageChanges], rlpBehaviors);
        stream.EncodeArray([.. item.StorageReads], rlpBehaviors);
        stream.EncodeArray([.. item.BalanceChanges], rlpBehaviors);
        stream.EncodeArray([.. item.NonceChanges], rlpBehaviors);
        stream.EncodeArray([.. item.CodeChanges], rlpBehaviors);
    }

    public static int GetContentLength(AccountChanges item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfAddressRlp
            + SequenceLength(item.StorageChanges, SlotChangesDecoder.Instance, rlpBehaviors)
            + SequenceLength(item.StorageReads, UInt256Decoder.Instance, rlpBehaviors)
            + SequenceLength(item.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors)
            + SequenceLength(item.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors)
            + SequenceLength(item.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);

    private static int SequenceLength<T>(IEnumerable<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
    {
        int length = 0;
        foreach (T item in items)
        {
            length += encoder.GetLength(item, rlpBehaviors);
        }
        return Rlp.LengthOfSequence(length);
    }

    private static SortedList<int, T> ToSortedByIndex<T>(T[] items, string changeName)
        where T : struct, IIndexedChange
    {
        int? lastIndex = null;
        SortedList<int, T> sorted = new(items.Length, GenericComparer.GetOptimized<int>());
        foreach (T item in items)
        {
            int index = item.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                throw new RlpException($"{changeName} changes were in incorrect order.");
            }
            lastIndex = index;
            sorted.Add(index, item);
        }
        return sorted;
    }
}
