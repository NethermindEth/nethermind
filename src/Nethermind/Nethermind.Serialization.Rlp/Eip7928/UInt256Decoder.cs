// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Eip7928;

public sealed class UInt256Decoder : RlpDecoder<UInt256>
{
    public static readonly UInt256Decoder Instance = new();

    public override int GetLength(UInt256 item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item);

    protected override UInt256 DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? reader.DecodeUInt256<OffFlag>() : reader.DecodeUInt256();

    public override void Encode(ref RlpWriter writer, UInt256 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}
