// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class StorageReadDecoder : IRlpValueDecoder<StorageRead>, IRlpStreamEncoder<StorageRead>
{
    private static StorageReadDecoder? _instance = null;
    public static StorageReadDecoder Instance => _instance ??= new();

    public int GetLength(StorageRead item, RlpBehaviors rlpBehaviors)
        => GetContentLength(item, rlpBehaviors);

    public StorageRead Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors) => new(ctx.DecodeUInt256());

    public void Encode(RlpStream stream, StorageRead item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => stream.Encode(item.Key);

    public static int GetContentLength(StorageRead item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Key);
}
