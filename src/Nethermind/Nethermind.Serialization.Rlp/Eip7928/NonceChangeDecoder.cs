// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class NonceChangeDecoder : IndexedChangeDecoder<NonceChange>
{
    public static readonly NonceChangeDecoder Instance = new();

    protected override NonceChange DecodeFields(ref RlpReader ctx)
        => new(ctx.DecodeUInt(), ctx.DecodeULong());

    protected override void EncodeValue<TWriter>(ref TWriter writer, NonceChange item)
        => writer.Encode(item.Value);

    protected override int GetValueLength(NonceChange item)
        => Rlp.LengthOf(item.Value);
}
