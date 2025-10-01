// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class StorageChangeDecoder : IRlpValueDecoder<StorageChange>, IRlpStreamDecoder<StorageChange>
{
    private static StorageChangeDecoder? _instance = null;
    public static StorageChangeDecoder Instance => _instance ??= new();

    public StorageChange Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        // var tmp = ctx.Data[ctx.Position..].ToArray();

        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        // tmp = tmp[..(length + 1)];
        // Console.WriteLine("storage change:" + length);
        // Console.WriteLine(Bytes.ToHexString(tmp));

        ushort blockAccessIndex = ctx.DecodeUShort();
        byte[] newValue = ctx.DecodeByteArray();
        if (newValue.Length != 32)
        {
            throw new RlpException("Invalid storage value, should be 32 bytes.");
        }

        StorageChange storageChange = new()
        {
            BlockAccessIndex = blockAccessIndex,
            NewValue = new(newValue)
        };

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return storageChange;
    }

    public int GetLength(StorageChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public StorageChange Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        StorageChange res = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, StorageChange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.BlockAccessIndex);
        stream.Encode(item.NewValue.Unwrap());
    }

    public static int GetContentLength(StorageChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.BlockAccessIndex) + Rlp.LengthOf(item.NewValue.Unwrap());
}
