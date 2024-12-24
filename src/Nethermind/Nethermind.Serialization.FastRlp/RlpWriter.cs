// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.FastRlp;

public delegate void RefRlpWriterAction<in T>(ref RlpWriter arg, T value) where T: allows ref struct;

public delegate void RefRlpWriterAction(ref RlpWriter arg);

public ref struct RlpWriter
{
    private const bool LengthMode = false;
    private const bool ContentMode = true;

    private bool _mode;

    public int Length { get; private set; }

    private byte[] _buffer;
    private int _position;

    public static RlpWriter LengthWriter()
    {
        return new RlpWriter
        {
            _mode = LengthMode
        };
    }

    public static RlpWriter ContentWriter(byte[] buffer)
    {
        return new RlpWriter
        {
            _mode = ContentMode,
            _buffer = buffer
        };
    }

    public void Write<T>(T value) where T : IBinaryInteger<T>, ISignedNumber<T>
    {
        switch (_mode)
        {
            case LengthMode:
                LengthWrite(value);
                break;
            case ContentMode:
                ContentWrite(value);
                break;
        }
    }

    private void LengthWrite<T>(T value) where T : IBinaryInteger<T>, ISignedNumber<T>
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
            LengthWrite(bigEndian);
        }
    }

    private void ContentWrite<T>(T value) where T : IBinaryInteger<T>, ISignedNumber<T>
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
            ContentWrite(bigEndian);
        }
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        switch (_mode)
        {
            case LengthMode:
                LengthWrite(value);
                break;
            case ContentMode:
                ContentWrite(value);
                break;
        }
    }

    private void LengthWrite(scoped ReadOnlySpan<byte> value)
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

    private void ContentWrite(scoped ReadOnlySpan<byte> value)
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

    public void WriteSequence(RefRlpWriterAction action)
    {
        switch (_mode)
        {
            case LengthMode:
                LengthWriteSequence(action);
                break;
            case ContentMode:
                ContentWriteSequence(action);
                break;
        }
    }

    private void LengthWriteSequence(RefRlpWriterAction action)
    {
        var inner = LengthWriter();
        action(ref inner);
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

    private void ContentWriteSequence(RefRlpWriterAction action)
    {
        var lengthWriter = LengthWriter();
        action(ref lengthWriter);
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

        action(ref this);
    }

    // TODO: Figure out how to unify overloads
    public void WriteSequence<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
    {
        switch (_mode)
        {
            case LengthMode:
                LengthWriteSequence(ctx, action);
                break;
            case ContentMode:
                ContentWriteSequence(ctx, action);
                break;
        }
    }

    private void LengthWriteSequence<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
    {
        var inner = LengthWriter();
        action(ref inner, ctx);
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

    private void ContentWriteSequence<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
    {
        var lengthWriter = LengthWriter();
        action(ref lengthWriter, ctx);
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

        action(ref this, ctx);
    }
}
