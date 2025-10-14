// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BalanceChangeDecoder : IRlpValueDecoder<BalanceChange>, IRlpStreamDecoder<BalanceChange>
{
    private static BalanceChangeDecoder? _instance = null;
    public static BalanceChangeDecoder Instance => _instance ??= new();

    public int GetLength(BalanceChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public BalanceChange Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        // var tmp = ctx.Data[ctx.Position..].ToArray();

        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        // tmp = tmp[..(length + 1)];
        // Console.WriteLine("balance change:" + length);
        // Console.WriteLine(Bytes.ToHexString(tmp));

        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = ctx.DecodeUShort(),
            PostBalance = ctx.DecodeUInt256()
        };

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return balanceChange;
    }

    public BalanceChange Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        BalanceChange res = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, BalanceChange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        Console.WriteLine("Encoding balance change: " + item);
        stream.Encode(item.BlockAccessIndex);
        stream.Encode(item.PostBalance);
    }

    public static int GetContentLength(BalanceChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.BlockAccessIndex) + Rlp.LengthOf(item.PostBalance);
}
