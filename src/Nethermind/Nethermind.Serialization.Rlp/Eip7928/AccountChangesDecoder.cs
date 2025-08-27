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
        byte[] address = ctx.DecodeAddress().Bytes;
        SlotChanges[] slotChanges = ctx.DecodeArray(SlotChangesDecoder.Instance);
        SortedDictionary<byte[], SlotChanges> slotChangesMap = new(slotChanges.ToDictionary(s => s.Slot, s => s));
        byte[][] storageReads = ctx.DecodeByteArrays();
        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance);
        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance);
        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance);
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
    {
        int len = 20; // address

        foreach (SlotChanges slotChanges in item.StorageChanges.Values)
        {
            len += SlotChangesDecoder.Instance.GetLength(slotChanges, rlpBehaviors);
        }

        len += item.StorageReads.Count * 32;

        foreach (BalanceChange balanceChange in item.BalanceChanges)
        {
            len += BalanceChangeDecoder.Instance.GetLength(balanceChange, rlpBehaviors);
        }

        foreach (NonceChange nonceChange in item.NonceChanges)
        {
            len += NonceChangeDecoder.Instance.GetLength(nonceChange, rlpBehaviors);
        }

        foreach (CodeChange codeChange in item.CodeChanges)
        {
            len += CodeChangeDecoder.Instance.GetLength(codeChange, rlpBehaviors);
        }

        return len;
    }

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
        stream.StartSequence(GetLength(item, rlpBehaviors));
        stream.Encode(item.Address);

        foreach (SlotChanges slotChanges in item.StorageChanges.Values)
        {
            SlotChangesDecoder.Instance.Encode(stream, slotChanges);
        }

        foreach (byte[] storageKey in item.StorageReads)
        {
            stream.Encode(storageKey);
        }

        foreach (BalanceChange balanceChange in item.BalanceChanges)
        {
            BalanceChangeDecoder.Instance.Encode(stream, balanceChange);
        }

        foreach (NonceChange nonceChange in item.NonceChanges)
        {
            NonceChangeDecoder.Instance.Encode(stream, nonceChange);
        }

        foreach (CodeChange codeChange in item.CodeChanges)
        {
            CodeChangeDecoder.Instance.Encode(stream, codeChange);
        }
    }
}
