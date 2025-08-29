// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : IRlpValueDecoder<SlotChanges>, IRlpStreamDecoder<SlotChanges>
{
    private static SlotChangesDecoder? _instance = null;
    public static SlotChangesDecoder Instance => _instance ??= new();


    public SlotChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        byte[] slot = ctx.DecodeByteArray();

        // int changesLength = ctx.ReadSequenceLength();
        // int changesCheck = length + ctx.Position;

        SlotChanges slotChanges = new()
        {
            Slot = slot,
            Changes = [.. ctx.DecodeArray(StorageChangeDecoder.Instance)]
        };

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return slotChanges;
    }

    public int GetLength(SlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

    public SlotChanges Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        SlotChanges res = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, SlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        (int Total, int Slot, int SlotChanges) = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(Total);

        // stream.StartSequence(Slot);
        stream.Encode(item.Slot);

        // stream.StartSequence(SlotChanges);
        // foreach (StorageChange change in item.Changes)
        // {
        //     StorageChangeDecoder.Instance.Encode(stream, change);
        // }
        stream.EncodeArray<StorageChange>([.. item.Changes], rlpBehaviors);
    }

    public static (int Total, int Slot, int SlotChanges) GetContentLength(SlotChanges item, RlpBehaviors rlpBehaviors)
    {
        int slotChangeLen = 0;

        foreach (StorageChange slotChange in item.Changes)
        {
            slotChangeLen += StorageChangeDecoder.Instance.GetLength(slotChange, rlpBehaviors);
        }

        int slot = Rlp.LengthOf(item.Slot);
        slotChangeLen = Rlp.LengthOfSequence(slotChangeLen);
        return (slotChangeLen + slot, slot, slotChangeLen);
    }
}
