// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

// If any of these is triggered in prod, then something went wrong, coz these are fairly slow path. These are only
// useful for easy tests.

public sealed class ByteStreamDecoder : RlpStreamDecoder<byte>
{
    public override int GetLength(byte item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    protected override byte DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeByte();
    }

    public override void Encode(RlpStream stream, byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public sealed class ShortStreamDecoder : RlpStreamDecoder<short>
{
    public override int GetLength(short item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    protected override short DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return (short)rlpStream.DecodeLong();
    }

    public override void Encode(RlpStream stream, short item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public sealed class UShortStreamDecoder : RlpStreamDecoder<ushort>
{
    public override int GetLength(ushort item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf((long)item);
    }

    protected override ushort DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return (ushort)rlpStream.DecodeLong();
    }

    public override void Encode(RlpStream stream, ushort item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public sealed class IntStreamDecoder : RlpStreamDecoder<int>
{
    public override int GetLength(int item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    protected override int DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeInt();
    }

    public override void Encode(RlpStream stream, int item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public sealed class UIntStreamDecoder : RlpStreamDecoder<uint>
{
    public override int GetLength(uint item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf((long)item);
    }

    protected override uint DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeUInt();
    }

    public override void Encode(RlpStream stream, uint item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public sealed class ULongStreamDecoder : RlpStreamDecoder<ulong>
{
    public override int GetLength(ulong item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    protected override ulong DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeUInt();
    }

    public override void Encode(RlpStream stream, ulong item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}
