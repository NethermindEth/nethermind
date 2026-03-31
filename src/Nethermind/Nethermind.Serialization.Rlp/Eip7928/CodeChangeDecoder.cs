// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class CodeChangeDecoder : IndexedChangeDecoder<CodeChange>
{
    private static CodeChangeDecoder? _instance;
    public static CodeChangeDecoder Instance => _instance ??= new();
    private static readonly RlpLimit _codeLimit = new(Eip7928Constants.MaxCodeSize, "", ReadOnlyMemory<char>.Empty);

    protected override CodeChange DecodeFields(ref Rlp.ValueDecoderContext ctx)
        => new(ctx.DecodeUShort(), ctx.DecodeByteArray(_codeLimit));

    protected override void EncodeValue(RlpStream stream, CodeChange item)
        => stream.Encode(item.NewCode);

    protected override int GetValueLength(CodeChange item)
        => Rlp.LengthOf(item.NewCode);
}
