// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class BalanceChangeDecoder : IndexedChangeDecoder<BalanceChange>
{
    private static BalanceChangeDecoder? _instance;
    public static BalanceChangeDecoder Instance => _instance ??= new();

    protected override BalanceChange DecodeFields(ref Rlp.ValueDecoderContext ctx)
        => new(ctx.DecodeUShort(), ctx.DecodeUInt256());

    protected override void EncodeValue(RlpStream stream, BalanceChange item)
        => stream.Encode(item.Value);

    protected override int GetValueLength(BalanceChange item)
        => Rlp.LengthOf(item.Value);
}
