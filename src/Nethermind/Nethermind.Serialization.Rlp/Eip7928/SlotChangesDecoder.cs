// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : IRlpValueDecoder<SlotChanges>, IRlpStreamDecoder<SlotChanges>
{
    private static SlotChangesDecoder? _instance = null;
    public static SlotChangesDecoder Instance => _instance ??= new();

    public SlotChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
        => new()
        {
            Slot = ctx.DecodeByteArray(),
            Changes = [.. ctx.DecodeArray(StorageChangeDecoder.Instance)]
        };

    public int GetLength(SlotChanges item, RlpBehaviors rlpBehaviors)
    {
        int len = 32; // slot

        foreach (StorageChange slotChange in item.Changes)
        {
            len += StorageChangeDecoder.Instance.GetLength(slotChange, rlpBehaviors);
        }

        return len;
    }

    public SlotChanges Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        SlotChanges response = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return response;
    }

    public void Encode(RlpStream stream, SlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(0);
        stream.Encode(item.Slot);
        foreach (StorageChange change in item.Changes)
        {
            StorageChangeDecoder.Instance.Encode(stream, change);
        }
    }
}
