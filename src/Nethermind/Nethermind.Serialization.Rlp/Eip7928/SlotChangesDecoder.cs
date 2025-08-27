// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : IRlpValueDecoder<SlotChanges>, IRlpStreamDecoder<SlotChanges>
{
    private static SlotChangesDecoder? _instance = null;
    public static SlotChangesDecoder Instance => _instance ??= new();

    private const int SlotSize = 32;

    public SlotChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
        => new()
        {
            Slot = ctx.DecodeByteArray(),
            Changes = [.. ctx.DecodeArray(StorageChangeDecoder.Instance)]
        };

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
        (int Total, int SlotChanges) len = GetContentLength(item, rlpBehaviors);
        stream.StartSequence(len.Total);
        stream.Encode(item.Slot);

        stream.StartSequence(len.SlotChanges);
        foreach (StorageChange change in item.Changes)
        {
            StorageChangeDecoder.Instance.Encode(stream, change);
        }
    }

    public static (int Total, int SlotChanges) GetContentLength(SlotChanges item, RlpBehaviors rlpBehaviors)
    {
        int slotChangeLen = 0;

        foreach (StorageChange slotChange in item.Changes)
        {
            slotChangeLen += StorageChangeDecoder.Instance.GetLength(slotChange, rlpBehaviors);
        }

        return (slotChangeLen + Rlp.LengthOf(item.Slot), slotChangeLen);
    }
}
