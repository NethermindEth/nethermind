// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class CodeChangeDecoder : IRlpValueDecoder<CodeChange>, IRlpStreamDecoder<CodeChange>
{
    private static CodeChangeDecoder? _instance = null;
    public static CodeChangeDecoder Instance => _instance ??= new();
    private static readonly RlpLimit _codeLimit = new(Eip7928Constants.MaxCodeSize, "", ReadOnlyMemory<char>.Empty);

    public CodeChange Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        ushort blockAccessIndex = ctx.DecodeUShort();
        byte[] newCode = ctx.DecodeByteArray(_codeLimit);

        CodeChange codeChange = new()
        {
            BlockAccessIndex = blockAccessIndex,
            NewCode = newCode
        };

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return codeChange;
    }

    public int GetLength(CodeChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public CodeChange Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Span<byte> span = rlpStream.PeekNextItem();
        Rlp.ValueDecoderContext ctx = new(span);
        CodeChange res = Decode(ref ctx, rlpBehaviors);
        rlpStream.SkipItem();

        return res;
    }

    public void Encode(RlpStream stream, CodeChange item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.BlockAccessIndex);
        stream.Encode(item.NewCode);
    }

    public static int GetContentLength(CodeChange item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.BlockAccessIndex) + Rlp.LengthOf(item.NewCode);
}
