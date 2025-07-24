// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Nethermind.Serialization.FluentRlp;

public delegate void RefRlpWriterAction<in TContext>(ref RlpWriter arg, TContext value)
    where TContext : allows ref struct;

public delegate void RefRlpWriterAction(ref RlpWriter arg);

public ref struct RlpWriter
{
    private const bool LengthMode = false;
    private const bool ContentMode = true;

    private bool _mode;

    public int Length { get; private set; }

    private IBufferWriter<byte> _buffer;

    public static RlpWriter LengthWriter()
    {
        return new RlpWriter
        {
            _mode = LengthMode
        };
    }

    public static RlpWriter ContentWriter(IBufferWriter<byte> buffer)
    {
        return new RlpWriter
        {
            _mode = ContentMode,
            _buffer = buffer
        };
    }

    public void Write<T>(T value) where T : unmanaged, IBinaryInteger<T>
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

    private unsafe void LengthWrite<T>(T value) where T : unmanaged, IBinaryInteger<T>
    {
        var size = sizeof(T);
        Span<byte> bigEndian = stackalloc byte[size];
        value.WriteBigEndian(bigEndian);
        bigEndian = bigEndian.TrimStart((byte)0);

        if (bigEndian.Length == 0)
        {
            Length++;
        }
        else if (bigEndian.Length == 1 && bigEndian[0] < 0x80)
        {
            Length++;
        }
        else
        {
            LengthWrite(bigEndian);
        }
    }

    private unsafe void ContentWrite<T>(T value) where T : unmanaged, IBinaryInteger<T>
    {
        var size = sizeof(T);
        Span<byte> bigEndian = stackalloc byte[size];
        value.WriteBigEndian(bigEndian);
        bigEndian = bigEndian.TrimStart((byte)0);

        if (bigEndian.Length == 0)
        {
            _buffer.Write([(byte)0x80]);
        }
        else if (bigEndian.Length == 1 && bigEndian[0] < 0x80)
        {
            _buffer.Write(bigEndian[..1]);
        }
        else
        {
            ContentWrite(bigEndian);
        }
    }

    public void Write(scoped ReadOnlySpan<byte> value)
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

    private void LengthWrite(scoped ReadOnlySpan<byte> value) => UNSAFE_FixedLengthWrite(value.Length);

    // TODO: Figure out how to make this method accessible only to the source generator
    public bool UNSAFE_FixedLengthWrite(int length)
    {
        if (_mode != LengthMode) return false;

        if (length < 55)
        {
            Length++;
        }
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, length);
            binaryLength = binaryLength.TrimStart((byte)0);
            Length += 1 + binaryLength.Length;
        }

        Length += length;

        return true;
    }

    private void ContentWrite(scoped ReadOnlySpan<byte> value)
    {
        if (value.Length < 55)
        {
            _buffer.Write([(byte)(0x80 + value.Length)]);
        }
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, value.Length);
            binaryLength = binaryLength.TrimStart((byte)0);

            _buffer.Write([(byte)(0xB7 + binaryLength.Length)]);
            _buffer.Write(binaryLength);
        }

        _buffer.Write(value);
    }

    public void WriteSequence(RefRlpWriterAction action)
        => WriteSequence(action, static (ref RlpWriter w, RefRlpWriterAction action) => action(ref w));

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
            _buffer.Write([(byte)(0xC0 + lengthWriter.Length)]);
        }
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(Int32)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, lengthWriter.Length);
            binaryLength = binaryLength.TrimStart((byte)0);

            _buffer.Write([(byte)(0xF7 + binaryLength.Length)]);
            _buffer.Write(binaryLength);
        }

        action(ref this, ctx);
    }
}
