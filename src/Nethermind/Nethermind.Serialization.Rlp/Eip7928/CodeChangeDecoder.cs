// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class CodeChangeDecoder : IRlpValueDecoder<CodeChange>, IRlpStreamDecoder<CodeChange>
{
    private static CodeChangeDecoder? _instance = null;
    public static CodeChangeDecoder Instance => _instance ??= new();

    public CodeChange Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
        => new()
        {
            BlockAccessIndex = ctx.DecodeUShort(),
            NewCode = ctx.DecodeByteArray()
        };

    public int GetLength(CodeChange item, RlpBehaviors rlpBehaviors) => 2 + item.NewCode.Length;

    public CodeChange Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        CodeChange response = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return response;
    }

    public void Encode(RlpStream stream, CodeChange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetLength(item, rlpBehaviors));
        stream.Encode(item.BlockAccessIndex);
        stream.Encode(item.NewCode);
    }
}
