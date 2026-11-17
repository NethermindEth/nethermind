// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Eip7928;

public sealed class StorageChangeDecoder : IndexedChangeDecoder<StorageChange>, IIndexedChangeReader<StorageChange>
{
    public static readonly StorageChangeDecoder Instance = new();

    protected override StorageChange DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => Decode<StorageChangeDecoder>(ref reader, rlpBehaviors);

    static StorageChange IIndexedChangeReader<StorageChange>.ReadFields<TCanonicalChecks>(ref RlpReader fields)
        => new(fields.DecodeUInt<TCanonicalChecks>(), fields.DecodeUInt256<TCanonicalChecks>());

    protected override void EncodeValue(ref RlpWriter writer, StorageChange item)
        => writer.Encode(item.Value);

    protected override int GetValueLength(StorageChange item)
        => Rlp.LengthOf(item.Value);
}
