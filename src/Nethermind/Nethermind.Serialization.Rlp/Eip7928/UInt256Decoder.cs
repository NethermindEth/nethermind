// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class UInt256Decoder : RlpDecoder<UInt256>
{
    public static readonly UInt256Decoder Instance = new();

    public override int GetLength(UInt256 item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item);

    protected override UInt256 DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors) => ctx.DecodeUInt256();

    public override void Encode<TWriter>(ref TWriter writer, UInt256 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}
