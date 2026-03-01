// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

// If any of these is triggered in prod, then something went wrong, coz these are fairly slow path. These are only
// useful for easy tests.

public sealed class ByteStreamDecoder : RlpValueDecoder<byte>
{
    public override int GetLength(byte item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override byte DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        rlpStream.DecodeByte();

    protected override byte DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        decoderContext.DecodeByte();

    public override void Encode(RlpStream stream, byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        stream.Encode(item);
}

public sealed class ShortStreamDecoder : RlpValueDecoder<short>
{
    public override int GetLength(short item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override short DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (short)rlpStream.DecodeLong();

    protected override short DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (short)decoderContext.DecodeLong();

    public override void Encode(RlpStream stream, short item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        stream.Encode(item);
}

public sealed class UShortStreamDecoder : RlpValueDecoder<ushort>
{
    public override int GetLength(ushort item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf((long)item);

    protected override ushort DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (ushort)rlpStream.DecodeLong();

    protected override ushort DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (ushort)decoderContext.DecodeLong();

    public override void Encode(RlpStream stream, ushort item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        stream.Encode(item);
}

public sealed class IntStreamDecoder : RlpValueDecoder<int>
{
    public override int GetLength(int item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override int DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        rlpStream.DecodeInt();

    protected override int DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        decoderContext.DecodeInt();

    public override void Encode(RlpStream stream, int item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        stream.Encode(item);
}

public sealed class UIntStreamDecoder : RlpValueDecoder<uint>
{
    public override int GetLength(uint item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf((long)item);

    protected override uint DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        rlpStream.DecodeUInt();

    protected override uint DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        (uint)decoderContext.DecodeInt();

    public override void Encode(RlpStream stream, uint item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        stream.Encode(item);
}

public sealed class ULongStreamDecoder : RlpValueDecoder<ulong>
{
    public override int GetLength(ulong item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item);

    protected override ulong DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        rlpStream.DecodeUlong();

    protected override ulong DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        decoderContext.DecodeULong();

    public override void Encode(RlpStream stream, ulong item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        stream.Encode(item);
}
