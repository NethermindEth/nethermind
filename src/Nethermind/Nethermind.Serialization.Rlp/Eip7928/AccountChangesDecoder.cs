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

    public AccountChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        // var tmp = ctx.Data[ctx.Position..].ToArray();

        // Console.WriteLine("account change uncut:");
        // Console.WriteLine(Bytes.ToHexString(tmp));

        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        // tmp = tmp[..(length + 1)];
        // Console.WriteLine("account change:" + length);
        // Console.WriteLine(Bytes.ToHexString(tmp));

        Address address = ctx.DecodeAddress();

        SlotChanges[] slotChanges = ctx.DecodeArray(SlotChangesDecoder.Instance);
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

        StorageRead[] storageReads = ctx.DecodeArray(StorageReadDecoder.Instance);
        SortedSet<StorageRead> storareReadsList = [];
        StorageRead? lastRead = null;
        foreach (StorageRead storageRead in storageReads)
        {
            if (lastRead is not null && storageRead.CompareTo(lastRead.Value) <= 0)
            {
                throw new RlpException("Storage reads were in incorrect order.");
            }
            storareReadsList.Add(storageRead);
            lastRead = storageRead;
        }

        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance);
        ushort lastIndex = 0;
        SortedList<ushort, BalanceChange> balanceChangesList = new(balanceChanges.ToDictionary(s =>
        {
            Console.WriteLine("Decoding balance change: " + s.ToString());
            ushort index = s.BlockAccessIndex;
            if (index <= lastIndex)
            {
                Console.WriteLine($"Balance changes were in incorrect order. index={index}, lastIndex={lastIndex}");
                throw new RlpException("Balance changes were in incorrect order.");
            }
            lastIndex = index;
            return index;
        }, s => s));

        lastIndex = 0;
        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance);
        SortedList<ushort, NonceChange> nonceChangesList = new(nonceChanges.ToDictionary(s =>
        {
            ushort index = s.BlockAccessIndex;
            if (index <= lastIndex)
            {
                throw new RlpException("Nonce changes were in incorrect order.");
            }
            lastIndex = index;
            return index;
        }, s => s));

        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance);

        if (codeChanges.Length > Eip7928Constants.MaxCodeChanges)
        {
            throw new RlpException("Number of code changes exceeded maximum.");
        }

        lastIndex = 0;
        SortedList<ushort, CodeChange> codeChangesList = new(codeChanges.ToDictionary(s =>
        {
            ushort index = s.BlockAccessIndex;
            if (index <= lastIndex)
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
            StorageReads = storareReadsList,
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
