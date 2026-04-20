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

        List<SlotChanges> slotChangesList = DecodeAscendingList(ref ctx, SlotChangesDecoder.Instance, _slotsLimit,
            static (ref SlotChanges current, ref UInt256 last, bool hasLast) =>
            {
                UInt256 slot = current.Slot;
                if (hasLast && slot <= last) throw new RlpException("Storage changes were in incorrect order.");
                last = slot;
            });

        List<StorageRead> storageReadsList = DecodeAscendingList(ref ctx, StorageReadDecoder.Instance, _storageLimit,
            static (ref StorageRead current, ref StorageRead last, bool hasLast) =>
            {
                if (hasLast && current.CompareTo(last) <= 0) throw new RlpException("Storage reads were in incorrect order.");
                last = current;
            });

        List<BalanceChange> balanceChangesList = DecodeAscendingByIndex(ref ctx, BalanceChangeDecoder.Instance, _txLimit, "Balance");
        List<NonceChange> nonceChangesList = DecodeAscendingByIndex(ref ctx, NonceChangeDecoder.Instance, _txLimit, "Nonce");
        List<CodeChange> codeChangesList = DecodeAscendingByIndex(ref ctx, CodeChangeDecoder.Instance, _txLimit, "Code");

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            ctx.Check(check);
        }

        return new(address, slotChangesList, storageReadsList, balanceChangesList, nonceChangesList, codeChangesList);
    }

    private delegate void AscendingCheck<T, TKey>(ref T current, ref TKey last, bool hasLast);

    private static List<T> DecodeAscendingList<T, TKey>(
        ref Rlp.ValueDecoderContext ctx,
        IRlpValueDecoder<T> decoder,
        RlpLimit limit,
        AscendingCheck<T, TKey> check)
    {
        int positionCheck = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(positionCheck);
        ctx.GuardLimit(count, limit);
        List<T> list = new(count);
        TKey last = default!;
        bool hasLast = false;
        for (int i = 0; i < count; i++)
        {
            T item = decoder.Decode(ref ctx, RlpBehaviors.None);
            check(ref item, ref last, hasLast);
            hasLast = true;
            list.Add(item);
        }
        ctx.Check(positionCheck);
        return list;
    }

    private static List<T> DecodeAscendingByIndex<T>(
        ref Rlp.ValueDecoderContext ctx,
        IRlpValueDecoder<T> decoder,
        RlpLimit limit,
        string changeName)
        where T : struct, IIndexedChange
    {
        int positionCheck = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(positionCheck);
        ctx.GuardLimit(count, limit);
        List<T> list = new(count);
        ushort lastIndex = 0;
        bool hasLast = false;
        for (int i = 0; i < count; i++)
        {
            T item = decoder.Decode(ref ctx, RlpBehaviors.None);
            ushort index = item.BlockAccessIndex;
            if (hasLast && index <= lastIndex)
            {
                throw new RlpException($"{changeName} changes were in incorrect order.");
            }
            lastIndex = index;
            hasLast = true;
            list.Add(item);
        }
        ctx.Check(positionCheck);
        return list;
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
            + SequenceLength(item.StorageReads, StorageReadDecoder.Instance, rlpBehaviors)
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

}
