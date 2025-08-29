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
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

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
