// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class StorageChangeDecoder : IRlpValueDecoder<StorageChange>, IRlpStreamEncoder<StorageChange>
{
    private static StorageChangeDecoder? _instance = null;
    public static StorageChangeDecoder Instance => _instance ??= new();

    public StorageChange Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        ushort blockAccessIndex = ctx.DecodeUShort();
        UInt256 newValue = ctx.DecodeUInt256();
        StorageChange storageChange = new()
        {
            BlockAccessIndex = blockAccessIndex,
            NewValue = newValue
        };

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return storageChange;
    }

    public int GetLength(StorageChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public void Encode(RlpStream stream, StorageChange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.BlockAccessIndex);
        stream.Encode(item.NewValue);
    }

    public static int GetContentLength(StorageChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.BlockAccessIndex) + Rlp.LengthOf(item.NewValue);
}
