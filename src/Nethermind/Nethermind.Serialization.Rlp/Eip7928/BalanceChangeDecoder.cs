// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BalanceChangeDecoder : IndexedChangeDecoder<BalanceChange>
{
    public static readonly BalanceChangeDecoder Instance = new();

    protected override BalanceChange DecodeFields(ref RlpReader ctx)
        => new(ctx.DecodeUInt(), ctx.DecodeUInt256());

    protected override void EncodeValue<TWriter>(ref TWriter writer, BalanceChange item)
        => writer.Encode(item.Value);

    protected override int GetValueLength(BalanceChange item)
        => Rlp.LengthOf(item.Value);
}
