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


    public SlotChanges Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        // var tmp = ctx.Data[ctx.Position..].ToArray();

        // Console.WriteLine("slot change uncut:");
        // Console.WriteLine(Bytes.ToHexString(tmp));

        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        // tmp = tmp[..(length + 2)];
        // Console.WriteLine("slot change:" + length);
        // Console.WriteLine(Bytes.ToHexString(tmp));

        byte[] slot = ctx.DecodeByteArray();
        if (slot.Length != 32)
        {
            throw new RlpException("Invalid storage key, should be 32 bytes.");
        }

        StorageChange[] changes = ctx.DecodeArray(StorageChangeDecoder.Instance);
        if (changes.Length > Eip7928Constants.MaxSlots)
        {
            throw new RlpException("Number of slot changes exceeded maximum.");
        }

        SlotChanges slotChanges = new()
        {
            Slot = slot,
            Changes = [.. changes]
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
