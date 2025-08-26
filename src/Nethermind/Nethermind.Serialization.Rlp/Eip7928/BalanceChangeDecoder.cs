// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BalanceChangeDecoder : IRlpValueDecoder<BalanceChange>, IRlpStreamDecoder<BalanceChange>
{
    // ushort + UInt256
    private const int Length = 2 + 32;

    private static BalanceChangeDecoder? _instance = null;
    public static BalanceChangeDecoder Instance => _instance ??= new();

    public BalanceChange Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
        => new()
        {
            BlockAccessIndex = ctx.DecodeUShort(),
            PostBalance = ctx.DecodeUInt256()
        };

    public int GetLength(BalanceChange item, RlpBehaviors rlpBehaviors) => Length;

    public BalanceChange Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        BalanceChange response = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return response;
    }

    public void Encode(RlpStream stream, BalanceChange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(Length);
        stream.Encode(item.BlockAccessIndex);
        stream.Encode(item.PostBalance);
    }
}
