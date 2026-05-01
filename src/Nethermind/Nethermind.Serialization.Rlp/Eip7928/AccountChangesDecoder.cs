// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class AccountChangesDecoder :
    IRlpValueDecoder<ReadOnlyAccountChanges>,
    IRlpStreamEncoder<ReadOnlyAccountChanges>,
    IRlpStreamEncoder<GeneratedAccountChanges>
{
    private static AccountChangesDecoder? _instance;
    public static AccountChangesDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _slotsLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _storageLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _txLimit = new(Eip7928Constants.MaxTxs, "", ReadOnlyMemory<char>.Empty);

    public ReadOnlyAccountChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        Address address = ctx.DecodeAddress();

        ReadOnlySlotChanges[] slotChanges = ctx.DecodeArray(SlotChangesDecoder.Instance, true, default, _slotsLimit);
        UInt256? lastSlot = null;
        HashSet<UInt256> slotKeys = new(slotChanges.Length);
        foreach (ReadOnlySlotChanges slotChange in slotChanges)
        {
            UInt256 slot = slotChange.Key;
            if (lastSlot is not null && slot <= lastSlot)
            {
                throw new RlpException("Storage changes were in incorrect order.");
            }
            lastSlot = slot;
            slotKeys.Add(slot);
        }

        UInt256[] storageReads = ctx.DecodeArray(UInt256Decoder.Instance, true, default, _storageLimit);
        UInt256? lastRead = null;
        foreach (UInt256 storageRead in storageReads)
        {
            if (lastRead is not null && storageRead.CompareTo(lastRead.Value) <= 0)
            {
                throw new RlpException("Storage reads were in incorrect order.");
            }
            if (slotKeys.Contains(storageRead))
            {
                throw new RlpException("Invalid storage read, already in storage changes.");
            }
            lastRead = storageRead;
        }

        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance, true, default, _txLimit);
        ValidateSortedByIndex(balanceChanges, "Balance");

        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance, true, default, _txLimit);
        ValidateSortedByIndex(nonceChanges, "Nonce");

        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance, true, default, _txLimit);
        ValidateSortedByIndex(codeChanges, "Code");

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            ctx.Check(check);
        }

        return new ReadOnlyAccountChanges(address, slotChanges, storageReads, balanceChanges, nonceChanges, codeChanges);
    }

    public int GetLength(ReadOnlyAccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public int GetLength(GeneratedAccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public void Encode(RlpStream stream, ReadOnlyAccountChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Address);
        EncodeSlotChanges(stream, item.StorageChanges, rlpBehaviors);
        EncodeUInt256s(stream, item.StorageReads, rlpBehaviors);
        EncodeIndexed(stream, item.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed(stream, item.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed(stream, item.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);
    }

    public void Encode(RlpStream stream, GeneratedAccountChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Address);
        EncodeGeneratedSlotChanges(stream, item.StorageChanges, rlpBehaviors);
        EncodeUInt256s(stream, item.StorageReads, rlpBehaviors);
        EncodeIndexed(stream, item.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed(stream, item.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed(stream, item.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);
    }

    public static int GetContentLength(ReadOnlyAccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfAddressRlp
            + SlotChangesSequenceLength(item.StorageChanges, rlpBehaviors)
            + UInt256SequenceLength(item.StorageReads, rlpBehaviors)
            + IndexedSequenceLength(item.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors)
            + IndexedSequenceLength(item.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors)
            + IndexedSequenceLength(item.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);

    public static int GetContentLength(GeneratedAccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfAddressRlp
            + GeneratedSlotChangesSequenceLength(item.StorageChanges, rlpBehaviors)
            + UInt256SequenceLength(item.StorageReads, rlpBehaviors)
            + IndexedSequenceLength(item.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors)
            + IndexedSequenceLength(item.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors)
            + IndexedSequenceLength(item.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);

    private static int SlotChangesSequenceLength(IEnumerable<ReadOnlySlotChanges> items, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (ReadOnlySlotChanges s in items) len += SlotChangesDecoder.Instance.GetLength(s, rlpBehaviors);
        return Rlp.LengthOfSequence(len);
    }

    private static int GeneratedSlotChangesSequenceLength(IEnumerable<GeneratedSlotChanges> items, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (GeneratedSlotChanges s in items) len += SlotChangesDecoder.Instance.GetLength(s, rlpBehaviors);
        return Rlp.LengthOfSequence(len);
    }

    private static int UInt256SequenceLength(IEnumerable<UInt256> items, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (UInt256 v in items) len += UInt256Decoder.Instance.GetLength(v, rlpBehaviors);
        return Rlp.LengthOfSequence(len);
    }

    private static int IndexedSequenceLength<T>(IEnumerable<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (T item in items) len += encoder.GetLength(item, rlpBehaviors);
        return Rlp.LengthOfSequence(len);
    }

    private static void EncodeSlotChanges(RlpStream stream, IEnumerable<ReadOnlySlotChanges> items, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (ReadOnlySlotChanges s in items) len += SlotChangesDecoder.Instance.GetLength(s, rlpBehaviors);
        stream.StartSequence(len);
        foreach (ReadOnlySlotChanges s in items) SlotChangesDecoder.Instance.Encode(stream, s, rlpBehaviors);
    }

    private static void EncodeGeneratedSlotChanges(RlpStream stream, IEnumerable<GeneratedSlotChanges> items, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (GeneratedSlotChanges s in items) len += SlotChangesDecoder.Instance.GetLength(s, rlpBehaviors);
        stream.StartSequence(len);
        foreach (GeneratedSlotChanges s in items) SlotChangesDecoder.Instance.Encode(stream, s, rlpBehaviors);
    }

    private static void EncodeUInt256s(RlpStream stream, IEnumerable<UInt256> items, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (UInt256 v in items) len += UInt256Decoder.Instance.GetLength(v, rlpBehaviors);
        stream.StartSequence(len);
        foreach (UInt256 v in items) UInt256Decoder.Instance.Encode(stream, v, rlpBehaviors);
    }

    private static void EncodeIndexed<T>(RlpStream stream, IEnumerable<T> items, IRlpStreamEncoder<T> encoder, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (T item in items) len += encoder.GetLength(item, rlpBehaviors);
        stream.StartSequence(len);
        foreach (T item in items) encoder.Encode(stream, item, rlpBehaviors);
    }

    private static void ValidateSortedByIndex<T>(T[] items, string changeName)
        where T : struct, IIndexedChange
    {
        int? lastIndex = null;
        foreach (T item in items)
        {
            int index = item.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                throw new RlpException($"{changeName} changes were in incorrect order.");
            }
            lastIndex = index;
        }
    }
}
