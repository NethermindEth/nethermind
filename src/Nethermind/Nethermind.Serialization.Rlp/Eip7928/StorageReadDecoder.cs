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
        => GetContentLength(item, rlpBehaviors);

    public StorageRead Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        byte[] key = ctx.DecodeByteArray();
        if (key.Length != 32)
        {
            throw new RlpException("Invalid storage key, should be 32 bytes.");
        }

        StorageRead storageRead = new()
        {
            Key = new(key),
        };

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
        => stream.Encode(item.Key.Unwrap());

    public static int GetContentLength(StorageRead item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Key.Unwrap());
}
