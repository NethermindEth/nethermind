// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Eip7928;

public sealed class NonceChangeDecoder : IndexedChangeDecoder<NonceChange>, IIndexedChangeReader<NonceChange>
{
    public static readonly NonceChangeDecoder Instance = new();

    protected override NonceChange DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => Decode<NonceChangeDecoder>(ref reader, rlpBehaviors);

    static NonceChange IIndexedChangeReader<NonceChange>.ReadFields<TCanonicalChecks>(ref RlpReader fields)
        => new(fields.DecodeUInt<TCanonicalChecks>(), fields.DecodeULong<TCanonicalChecks>());

    protected override void EncodeValue(ref RlpWriter writer, NonceChange item)
        => writer.Encode(item.Value);

    protected override int GetValueLength(NonceChange item)
        => Rlp.LengthOf(item.Value);
}
