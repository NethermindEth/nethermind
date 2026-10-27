// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

// If any of these is triggered in prod, then something went wrong, coz these are fairly slow path. These are only
// useful for easy tests.

public sealed class ByteStreamDecoder : RlpDecoder<byte>
{
    public override int PopulateLength(byte item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Rlp.LengthOf(item);

    protected override byte DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        decoderContext.DecodeByte();

    public override void Encode<TWriter>(ref TWriter writer, byte item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}

public sealed class ShortStreamDecoder : RlpDecoder<short>
{
    public override int PopulateLength(short item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Rlp.LengthOf(item);

    protected override short DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (short)decoderContext.DecodeLong();

    public override void Encode<TWriter>(ref TWriter writer, short item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}

public sealed class UShortStreamDecoder : RlpDecoder<ushort>
{
    public override int PopulateLength(ushort item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Rlp.LengthOf((long)item);

    protected override ushort DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (ushort)decoderContext.DecodeLong();

    public override void Encode<TWriter>(ref TWriter writer, ushort item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}

public sealed class IntStreamDecoder : RlpDecoder<int>
{
    public override int PopulateLength(int item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Rlp.LengthOf(item);

    protected override int DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        decoderContext.DecodeInt();

    public override void Encode<TWriter>(ref TWriter writer, int item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}

public sealed class UIntStreamDecoder : RlpDecoder<uint>
{
    public override int PopulateLength(uint item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Rlp.LengthOf((long)item);

    protected override uint DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        decoderContext.DecodeUInt();

    public override void Encode<TWriter>(ref TWriter writer, uint item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}

public sealed class ULongStreamDecoder : RlpDecoder<ulong>
{
    public override int PopulateLength(ulong item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Rlp.LengthOf(item);

    protected override ulong DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        decoderContext.DecodeULong();

    public override void Encode<TWriter>(ref TWriter writer, ulong item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => writer.Encode(item);
}
