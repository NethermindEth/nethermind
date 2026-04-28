// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class UInt256Decoder : IRlpValueDecoder<UInt256>, IRlpStreamEncoder<UInt256>
{
    private static UInt256Decoder? _instance = null;
    public static UInt256Decoder Instance => _instance ??= new();

    public int GetLength(UInt256 item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item);

    public UInt256 Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors) => ctx.DecodeUInt256();

    public void Encode(RlpStream stream, UInt256 item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => stream.Encode(item);
}
