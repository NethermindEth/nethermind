// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

// If any of these is triggered in prod, then something went wrong, coz these are fairly slow path. These are only
// useful for easy tests.

public sealed class ByteStreamDecoder : RlpDecoder<byte>
{
    public override int GetLength(byte item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override byte DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? reader.DecodeByte<OffFlag>() : reader.DecodeByte();

    public override void Encode(ref RlpWriter writer, byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        writer.Encode(item);
}

public sealed class ShortStreamDecoder : RlpDecoder<short>
{
    public override int GetLength(short item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override short DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? (short)reader.DecodeLong<OffFlag>() : (short)reader.DecodeLong();

    public override void Encode(ref RlpWriter writer, short item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        writer.Encode(item);
}

public sealed class UShortStreamDecoder : RlpDecoder<ushort>
{
    public override int GetLength(ushort item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf((long)item);

    protected override ushort DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? (ushort)reader.DecodeLong<OffFlag>() : (ushort)reader.DecodeLong();

    public override void Encode(ref RlpWriter writer, ushort item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        writer.Encode(item);
}

public sealed class IntStreamDecoder : RlpDecoder<int>
{
    public override int GetLength(int item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override int DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? reader.DecodeInt<OffFlag>() : reader.DecodeInt();

    public override void Encode(ref RlpWriter writer, int item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        writer.Encode(item);
}

public sealed class UIntStreamDecoder : RlpDecoder<uint>
{
    public override int GetLength(uint item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf((long)item);

    protected override uint DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? reader.DecodeUInt<OffFlag>() : reader.DecodeUInt();

    public override void Encode(ref RlpWriter writer, uint item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        writer.Encode(item);
}

public sealed class ULongStreamDecoder : RlpDecoder<ulong>
{
    public override int GetLength(ulong item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override ulong DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? reader.DecodeULong<OffFlag>() : reader.DecodeULong();

    public override void Encode(ref RlpWriter writer, ulong item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        writer.Encode(item);
}
