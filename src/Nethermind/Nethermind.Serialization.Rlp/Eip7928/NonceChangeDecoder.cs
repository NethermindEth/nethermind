// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class NonceChangeDecoder : IndexedChangeDecoder<NonceChange>
{
    private static NonceChangeDecoder? _instance;
    public static NonceChangeDecoder Instance => _instance ??= new();

    protected override NonceChange DecodeFields(ref Rlp.ValueDecoderContext ctx)
        => new(ctx.DecodeUShort(), ctx.DecodeULong());

    protected override void EncodeValue(RlpStream stream, NonceChange item)
        => stream.Encode(item.Value);

    protected override int GetValueLength(NonceChange item)
        => Rlp.LengthOf(item.Value);
}
