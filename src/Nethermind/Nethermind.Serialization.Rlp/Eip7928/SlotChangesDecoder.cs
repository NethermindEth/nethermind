// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : IRlpValueDecoder<SlotChanges>, IRlpStreamEncoder<SlotChanges>
{
    private static SlotChangesDecoder? _instance = null;
    public static SlotChangesDecoder Instance => _instance ??= new();

    private static readonly RlpLimit _codeLimit = new(Eip7928Constants.MaxCodeSize, "", ReadOnlyMemory<char>.Empty);

    public SlotChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        UInt256 slot = ctx.DecodeUInt256();

        int innerPositionCheck = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(innerPositionCheck);
        ctx.GuardLimit(count, _codeLimit);

        List<StorageChange> changesList = new(count);
        ushort lastIndex = 0;
        bool hasLast = false;
        for (int i = 0; i < count; i++)
        {
            StorageChange change = StorageChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
            ushort index = change.BlockAccessIndex;
            if (hasLast && index <= lastIndex)
            {
                throw new RlpException($"Storage changes were in incorrect order. index={index}, lastIndex={lastIndex}");
            }
            lastIndex = index;
            hasLast = true;
            changesList.Add(change);
        }
        ctx.Check(innerPositionCheck);
        SlotChanges slotChanges = new(slot, changesList);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return slotChanges;
    }

    public int GetLength(SlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public void Encode(RlpStream stream, SlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Slot);
        stream.EncodeArray([.. item.Changes], rlpBehaviors);
    }

    public static int GetContentLength(SlotChanges item, RlpBehaviors rlpBehaviors)
    {
        int storageChangesLen = 0;

        foreach (StorageChange slotChange in item.Changes)
        {
            storageChangesLen += StorageChangeDecoder.Instance.GetLength(slotChange, rlpBehaviors);
        }
        storageChangesLen = Rlp.LengthOfSequence(storageChangesLen);

        return storageChangesLen + Rlp.LengthOf(item.Slot);
    }
}
