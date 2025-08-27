// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class NonceChangeDecoder : IRlpValueDecoder<NonceChange>, IRlpStreamDecoder<NonceChange>
{
    // ushort + ulong
    private const int Length = 2 + 8;

    private static NonceChangeDecoder? _instance = null;
    public static NonceChangeDecoder Instance => _instance ??= new();

    public NonceChange Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
        => new()
        {
            BlockAccessIndex = ctx.DecodeUShort(),
            NewNonce = ctx.DecodeULong()
        };

    public int GetLength(NonceChange item, RlpBehaviors rlpBehaviors) => Length;

    public NonceChange Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        NonceChange res= Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, NonceChange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(Length);
        stream.Encode(item.BlockAccessIndex);
        stream.Encode(item.NewNonce);
    }
}
