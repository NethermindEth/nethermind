// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class StorageReadDecoder : IRlpValueDecoder<StorageRead>, IRlpStreamDecoder<StorageRead>
{
    private static StorageReadDecoder? _instance = null;
    public static StorageReadDecoder Instance => _instance ??= new();

    public int GetLength(StorageRead item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public StorageRead Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        StorageRead storageRead = new()
        {
            Key = ctx.DecodeByteArray(),
        };

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return storageRead;
    }

    public StorageRead Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        StorageRead res = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, StorageRead item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Key);
    }

    public static int GetContentLength(StorageRead item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Key);
}
