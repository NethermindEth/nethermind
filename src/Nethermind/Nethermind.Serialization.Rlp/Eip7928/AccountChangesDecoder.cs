// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class AccountChangesDecoder : IRlpValueDecoder<AccountChanges>, IRlpStreamDecoder<AccountChanges>
{
    private static AccountChangesDecoder? _instance = null;
    public static AccountChangesDecoder Instance => _instance ??= new();

    public AccountChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        byte[] address = ctx.DecodeAddress().Bytes;
        SlotChanges[] slotChanges = ctx.DecodeArray(SlotChangesDecoder.Instance);
        SortedDictionary<byte[], SlotChanges> slotChangesMap = new(slotChanges.ToDictionary(s => s.Slot, s => s));
        byte[][] storageReads = ctx.DecodeByteArrays();
        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance);
        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance);
        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return new()
        {
            Address = address,
            StorageChanges = slotChangesMap,
            StorageReads = [.. storageReads],
            BalanceChanges = [.. balanceChanges],
            NonceChanges = [.. nonceChanges],
            CodeChanges = [.. codeChanges]
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
        // stream.StartSequence(Rlp.LengthOfAddressRlp);
        stream.Encode(item.Address);
        stream.EncodeArray([.. item.StorageChanges.Values], rlpBehaviors);
        stream.EncodeArray([.. item.StorageReads], rlpBehaviors);
        stream.EncodeArray([.. item.BalanceChanges], rlpBehaviors);
        stream.EncodeArray([.. item.NonceChanges], rlpBehaviors);
        stream.EncodeArray([.. item.CodeChanges], rlpBehaviors);
        // stream.StartSequence(contentLengths.SlotChanges);
        // foreach (SlotChanges slotChanges in item.StorageChanges.Values)
        // {
        //     SlotChangesDecoder.Instance.Encode(stream, slotChanges);
        // }

        // stream.StartSequence(contentLengths.StorageReads);
        // foreach (byte[] storageKey in item.StorageReads)
        // {
        //     stream.Encode(storageKey);
        // }

        // stream.StartSequence(contentLengths.BalanceChanges);
        // foreach (BalanceChange balanceChange in item.BalanceChanges)
        // {
        //     BalanceChangeDecoder.Instance.Encode(stream, balanceChange);
        // }

        // stream.StartSequence(contentLengths.NonceChanges);
        // foreach (NonceChange nonceChange in item.NonceChanges)
        // {
        //     NonceChangeDecoder.Instance.Encode(stream, nonceChange);
        // }

        // stream.StartSequence(contentLengths.CodeChanges);
        // foreach (CodeChange codeChange in item.CodeChanges)
        // {
        //     CodeChangeDecoder.Instance.Encode(stream, codeChange);
        // }
    }

    private static int GetContentLength(AccountChanges item, RlpBehaviors rlpBehaviors)
    {
        // ContentLengths res = new()
        // {
        //     Total = Rlp.LengthOfAddressRlp,
        //     SlotChanges = 0,
        //     StorageReads = 0,
        //     BalanceChanges = 0,
        //     NonceChanges = 0,
        //     CodeChanges = 0
        // };
        int slotChangesLen = 0;
        foreach (SlotChanges slotChanges in item.StorageChanges.Values)
        {
            slotChangesLen += SlotChangesDecoder.Instance.GetLength(slotChanges, rlpBehaviors);
        }
        slotChangesLen = Rlp.LengthOfSequence(slotChangesLen);

        int storageReadsLen = 0;
        foreach (byte[] storageRead in item.StorageReads)
        {
            storageReadsLen += Rlp.LengthOf(storageRead);
        }
        storageReadsLen = Rlp.LengthOfSequence(storageReadsLen);

        int balanceChangesLen = 0;
        foreach (BalanceChange balanceChange in item.BalanceChanges)
        {
            balanceChangesLen += BalanceChangeDecoder.Instance.GetLength(balanceChange, rlpBehaviors);
        }
        balanceChangesLen = Rlp.LengthOfSequence(balanceChangesLen);

        int nonceChangesLen = 0;
        foreach (NonceChange nonceChange in item.NonceChanges)
        {
            nonceChangesLen += NonceChangeDecoder.Instance.GetLength(nonceChange, rlpBehaviors);
        }
        nonceChangesLen = Rlp.LengthOfSequence(nonceChangesLen);

        int codeChangesLen = 0;
        foreach (CodeChange codeChange in item.CodeChanges)
        {
            codeChangesLen += CodeChangeDecoder.Instance.GetLength(codeChange, rlpBehaviors);
        }
        codeChangesLen = Rlp.LengthOfSequence(codeChangesLen);

        return Rlp.LengthOfAddressRlp + slotChangesLen + storageReadsLen + balanceChangesLen + nonceChangesLen + codeChangesLen;
    }

    // private struct ContentLengths
    // {
    //     public int Total;
    //     public int SlotChanges;
    //     public int StorageReads;
    //     public int BalanceChanges;
    //     public int NonceChanges;
    //     public int CodeChanges;
    // }
}
