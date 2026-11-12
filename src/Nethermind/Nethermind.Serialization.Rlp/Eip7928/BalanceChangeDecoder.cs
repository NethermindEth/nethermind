// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Eip7928;

public sealed class BalanceChangeDecoder : IndexedChangeDecoder<BalanceChange>, IIndexedChangeReader<BalanceChange>
{
    public static readonly BalanceChangeDecoder Instance = new();

    protected override BalanceChange DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => Decode<BalanceChangeDecoder>(ref reader, rlpBehaviors);

    static BalanceChange IIndexedChangeReader<BalanceChange>.ReadFields<TCanonicalChecks>(ref RlpReader fields)
        => new(fields.DecodeUInt<TCanonicalChecks>(), fields.DecodeUInt256<TCanonicalChecks>());

    protected override void EncodeValue(ref RlpWriter writer, BalanceChange item)
        => writer.Encode(item.Value);

    protected override int GetValueLength(BalanceChange item)
        => Rlp.LengthOf(item.Value);
}
