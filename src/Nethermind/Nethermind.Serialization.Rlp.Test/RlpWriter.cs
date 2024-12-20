// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.Rlp.Test;

public interface IRlpWriter
{
    void Write<T>(T value) where T: IBinaryInteger<T>, ISignedNumber<T>;
    void Write(ReadOnlySpan<byte> value);
    void WriteList(Action<IRlpWriter> action);
}

public sealed class RlpContentWriter : IRlpWriter
{
    private readonly byte[] _buffer;
    private int _position;

    public RlpContentWriter(byte[] buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public void Write<T>(T value) where T : IBinaryInteger<T>, ISignedNumber<T>
    {
        var size = Marshal.SizeOf<T>();
        Span<byte> bigEndian = stackalloc byte[size];
        value.WriteBigEndian(bigEndian);
        bigEndian = bigEndian.TrimStart((byte)0);

        if (bigEndian.Length == 0)
        {
            _buffer[_position++] = 0;
        } else if (bigEndian.Length == 1 && bigEndian[0] < 0x80)
        {
            _buffer[_position++] = bigEndian[0];
        }
        else
        {
            Write(bigEndian);
        }
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        if (value.Length < 55)
        {
            _buffer[_position++] = (byte)(0x80 + value.Length);
        }
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, value.Length);
            binaryLength = binaryLength.TrimStart((byte)0);
            _buffer[_position++] = (byte)(0xB7 + binaryLength.Length);
            binaryLength.CopyTo(_buffer.AsSpan()[_position..]);
            _position += binaryLength.Length;
        }

        value.CopyTo(_buffer.AsSpan()[_position..]);
        _position += value.Length;
    }

    public void WriteList(Action<IRlpWriter> action)
    {
        var lengthWriter = new RlpLengthWriter();
        action(lengthWriter);
        if (lengthWriter.Length < 55)
        {
            _buffer[_position++] = (byte)(0xC0 + lengthWriter.Length);
        }
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(Int32)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, lengthWriter.Length);
            binaryLength = binaryLength.TrimStart((byte)0);
            _buffer[_position++] = (byte)(0xF7 + binaryLength.Length);
            binaryLength.CopyTo(_buffer.AsSpan()[_position..]);
            _position += binaryLength.Length;
        }

        action(this);
    }
}

public sealed class RlpLengthWriter : IRlpWriter
{
    public int Length { get; private set; }

    public RlpLengthWriter()
    {
        Length = 0;
    }

    public void Write<T>(T value) where T : IBinaryInteger<T>, ISignedNumber<T>
    {
        var size = Marshal.SizeOf<T>();
        Span<byte> bigEndian = stackalloc byte[size];
        value.WriteBigEndian(bigEndian);
        bigEndian = bigEndian.TrimStart((byte)0);

        if (bigEndian.Length == 0)
        {
            Length++;
        } else if (bigEndian.Length == 1 && bigEndian[0] < 0x80)
        {
            Length++;
        }
        else
        {
            Write(bigEndian);
        }
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        if (value.Length < 55)
        {
            Length++;
        }
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, value.Length);
            binaryLength = binaryLength.TrimStart((byte)0);
            Length += 1 + binaryLength.Length;
        }

        Length += value.Length;
    }

    public void WriteList(Action<IRlpWriter> action)
    {
        var inner = new RlpLengthWriter();
        action(inner);
        if (inner.Length < 55)
        {
            Length += 1 + inner.Length;
        }
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(Int32)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, inner.Length);
            binaryLength = binaryLength.TrimStart((byte)0);
            Length += 1 + inner.Length + binaryLength.Length;
        }
    }
}
