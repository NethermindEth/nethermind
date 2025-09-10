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
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        Address address = ctx.DecodeAddress();
        SlotChanges[] slotChanges = ctx.DecodeArray(SlotChangesDecoder.Instance);
        SortedDictionary<byte[], SlotChanges> slotChangesMap = new(slotChanges.ToDictionary(s => s.Slot, s => s), Bytes.Comparer);
        StorageRead[] storageReads = ctx.DecodeArray(StorageReadDecoder.Instance);
        BalanceChange[] balanceChanges = ctx.DecodeArray(BalanceChangeDecoder.Instance);
        SortedList<ushort, BalanceChange> balanceChangesList = new(balanceChanges.ToDictionary(s => s.BlockAccessIndex, s => s));
        NonceChange[] nonceChanges = ctx.DecodeArray(NonceChangeDecoder.Instance);
        SortedList<ushort, NonceChange> nonceChangesList = new(nonceChanges.ToDictionary(s => s.BlockAccessIndex, s => s));
        CodeChange[] codeChanges = ctx.DecodeArray(CodeChangeDecoder.Instance);
        SortedList<ushort, CodeChange> codeChangesList = new(codeChanges.ToDictionary(s => s.BlockAccessIndex, s => s));

        if (codeChanges.Length > Eip7928Constants.MaxCodeChanges)
        {
            throw new RlpException("Number of code changes exceeded maximum.");
        }

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return new()
        {
            Address = address,
            StorageChanges = slotChangesMap,
            StorageReads = [.. storageReads],
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
