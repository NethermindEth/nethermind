// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

// If any of these is triggered in prod, then something went wrong, coz these are fairly slow path. These are only
// useful for easy tests.

public class ByteStreamDecoder : IRlpStreamDecoder<byte>
{
    public int GetLength(byte item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    public byte Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeByte();
    }

    public void Encode(RlpStream stream, byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public class ShortStreamDecoder : IRlpStreamDecoder<short>
{
    public int GetLength(short item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    public short Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return (short)rlpStream.DecodeLong();
    }

    public void Encode(RlpStream stream, short item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public class UShortStreamDecoder : IRlpStreamDecoder<ushort>
{
    public int GetLength(ushort item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf((long)item);
    }

    public ushort Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return (ushort)rlpStream.DecodeLong();
    }

    public void Encode(RlpStream stream, ushort item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public class IntStreamDecoder : IRlpStreamDecoder<int>
{
    public int GetLength(int item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    public int Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeInt();
    }

    public void Encode(RlpStream stream, int item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public class UIntStreamDecoder : IRlpStreamDecoder<uint>
{
    public int GetLength(uint item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf((long)item);
    }

    public uint Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeUInt();
    }

    public void Encode(RlpStream stream, uint item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}

public class ULongStreamDecoder : IRlpStreamDecoder<ulong>
{
    public int GetLength(ulong item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOf(item);
    }

    public ulong Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return rlpStream.DecodeUInt();
    }

    public void Encode(RlpStream stream, ulong item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.Encode(item);
    }
}
