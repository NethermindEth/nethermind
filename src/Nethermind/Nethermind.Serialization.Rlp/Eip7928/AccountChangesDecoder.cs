// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class AccountChangesDecoder : IRlpValueDecoder<AccountChanges>, IRlpStreamDecoder<AccountChanges>
{
    private static AccountChangesDecoder? _instance = null;
    public static AccountChangesDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _slotsLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _txLimit = new(Eip7928Constants.MaxTxs, "", ReadOnlyMemory<char>.Empty);

    public AccountChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        Address address = ctx.DecodeAddress();

        SlotChanges[] slotChanges = ctx.DecodeArray(SlotChangesDecoder.Instance, true, default, _slotsLimit);
        byte[]? lastSlot = null;
        SortedDictionary<byte[], SlotChanges> slotChangesMap = new(slotChanges.ToDictionary(s =>
        {
            byte[] slot = s.Slot;
            if (lastSlot is not null && Bytes.BytesComparer.Compare(slot, lastSlot) <= 0)
            {
                throw new RlpException("Storage changes were in incorrect order.");
            }
            lastSlot = slot;
            return slot;
        }, s => s), Bytes.Comparer);

        StorageRead[] storageReads = ctx.DecodeArray(StorageReadDecoder.Instance, true, default, _slotsLimit);
        SortedSet<StorageRead> storageReadsList = [];
        StorageRead? lastRead = null;
        foreach (StorageRead storageRead in storageReads)
        {
            if (lastRead is not null && storageRead.CompareTo(lastRead.Value) <= 0)
            {
                throw new RlpException("Storage reads were in incorrect order.");
            }
            storageReadsList.Add(storageRead);
            lastRead = storageRead;
        }

        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance, true, default, _txLimit);
        ushort? lastIndex = null;
        SortedList<ushort, BalanceChange> balanceChangesList = new(balanceChanges.ToDictionary(s =>
        {
            ushort index = s.BlockAccessIndex;
            if (lastIndex is not null && index <= lastIndex)
            {
                Console.WriteLine($"Balance changes were in incorrect order. index={index}, lastIndex={lastIndex}");
                throw new RlpException("Balance changes were in incorrect order.");
            }
            lastIndex = index;
            return index;
        }, s => s));

        lastIndex = null;
        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance, true, default, _txLimit);
        SortedList<ushort, NonceChange> nonceChangesList = new(nonceChanges.ToDictionary(s =>
        {
            ushort index = s.BlockAccessIndex;
            if (lastIndex is not null && index <= lastIndex)
            {
                throw new RlpException("Nonce changes were in incorrect order.");
            }
            lastIndex = index;
            return index;
        }, s => s));

        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance, true, default, _txLimit);

        lastIndex = null;
        SortedList<ushort, CodeChange> codeChangesList = new(codeChanges.ToDictionary(s =>
        {
            ushort index = s.BlockAccessIndex;
            if (lastIndex is not null && index <= lastIndex)
            {
                throw new RlpException("Code changes were in incorrect order.");
            }
            lastIndex = index;
            return index;
        }, s => s));

        return new()
        {
            Address = address,
            StorageChanges = slotChangesMap,
            StorageReads = storageReadsList,
            BalanceChanges = balanceChangesList,
            NonceChanges = nonceChangesList,
            CodeChanges = codeChangesList
        };
    }

    public int GetLength(AccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public AccountChanges Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        AccountChanges res = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, AccountChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Address);
        stream.EncodeArray([.. item.StorageChanges.Values], rlpBehaviors);
        stream.EncodeArray([.. item.StorageReads], rlpBehaviors);
        stream.EncodeArray([.. item.BalanceChanges.Values], rlpBehaviors);
        stream.EncodeArray([.. item.NonceChanges.Values], rlpBehaviors);
        stream.EncodeArray([.. item.CodeChanges.Values], rlpBehaviors);
    }

    private static int GetContentLength(AccountChanges item, RlpBehaviors rlpBehaviors)
    {
        int slotChangesLen = 0;
        foreach (SlotChanges slotChanges in item.StorageChanges.Values)
        {
            slotChangesLen += SlotChangesDecoder.Instance.GetLength(slotChanges, rlpBehaviors);
        }
        slotChangesLen = Rlp.LengthOfSequence(slotChangesLen);

        int storageReadsLen = 0;
        foreach (StorageRead storageRead in item.StorageReads)
        {
            storageReadsLen += StorageReadDecoder.Instance.GetLength(storageRead, rlpBehaviors);
        }
        storageReadsLen = Rlp.LengthOfSequence(storageReadsLen);

        int balanceChangesLen = 0;
        foreach (BalanceChange balanceChange in item.BalanceChanges.Values)
        {
            balanceChangesLen += BalanceChangeDecoder.Instance.GetLength(balanceChange, rlpBehaviors);
        }
        balanceChangesLen = Rlp.LengthOfSequence(balanceChangesLen);

        int nonceChangesLen = 0;
        foreach (NonceChange nonceChange in item.NonceChanges.Values)
        {
            nonceChangesLen += NonceChangeDecoder.Instance.GetLength(nonceChange, rlpBehaviors);
        }
        nonceChangesLen = Rlp.LengthOfSequence(nonceChangesLen);

        int codeChangesLen = 0;
        foreach (CodeChange codeChange in item.CodeChanges.Values)
        {
            codeChangesLen += CodeChangeDecoder.Instance.GetLength(codeChange, rlpBehaviors);
        }
        codeChangesLen = Rlp.LengthOfSequence(codeChangesLen);

        return Rlp.LengthOfAddressRlp + slotChangesLen + storageReadsLen + balanceChangesLen + nonceChangesLen + codeChangesLen;
    }
}
