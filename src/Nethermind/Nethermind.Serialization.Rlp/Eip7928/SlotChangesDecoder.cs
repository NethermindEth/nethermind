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
        StorageChange[] changes = ctx.DecodeArray(StorageChangeDecoder.Instance, true, default, _codeLimit);

        int? lastIndex = null;
        SortedList<int, StorageChange> changesList = new(changes.Length, GenericComparer.GetOptimized<int>());
        foreach (StorageChange s in changes)
        {
            int index = s.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                throw new RlpException($"Storage changes were in incorrect order. index={index}, lastIndex={lastIndex}");
            }
            lastIndex = index;
            changesList.Add(index, s);
        }
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
        stream.Encode(item.Key);
        stream.EncodeArray([.. item.Changes.Values], rlpBehaviors);
    }

    public static int GetContentLength(SlotChanges item, RlpBehaviors rlpBehaviors)
    {
        int storageChangesLen = 0;

        foreach (StorageChange slotChange in item.Changes.Values)
        {
            storageChangesLen += StorageChangeDecoder.Instance.GetLength(slotChange, rlpBehaviors);
        }
        storageChangesLen = Rlp.LengthOfSequence(storageChangesLen);

        return storageChangesLen + Rlp.LengthOf(item.Key);
    }
}
