// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public delegate void ValueRlpWriteSink(object sink, ReadOnlySpan<byte> bytesToWrite);

public delegate void ValueRlpWriteByteSink(object sink, byte byteToWrite);

/// <summary>
/// Value-type RLP writer for pre-sized buffers.
/// </summary>
/// <remarks>
/// The writer does not allocate or grow its backing span. Use <see cref="WrittenSpan"/> for the encoded output; the
/// full <see cref="Data"/> span may contain unwritten bytes.
/// </remarks>
public ref struct ValueRlpWriter
{
    private Span<byte> _data;
    private object? _sink;
    private ValueRlpWriteSink? _writeSink;
    private ValueRlpWriteByteSink? _writeByteSink;
    private int _position;

    /// <summary>
    /// Initializes a writer over a caller-owned output span.
    /// </summary>
    public ValueRlpWriter(Span<byte> data)
    {
        _data = data;
        _sink = null;
        _writeSink = null;
        _writeByteSink = null;
        _position = 0;
    }

    /// <summary>
    /// Initializes a writer over a caller-owned output array.
    /// </summary>
    public ValueRlpWriter(byte[] data)
        : this(data.AsSpan())
    {
    }

    /// <summary>
    /// Initializes a writer over a caller-owned capped array.
    /// </summary>
    public ValueRlpWriter(in CappedArray<byte> data)
        : this(data.AsSpan())
    {
    }

    /// <summary>
    /// Initializes a writer over a caller-owned capped array.
    /// </summary>
    public ValueRlpWriter(int length)
        : this(new byte[length])
    {
    }

    /// <summary>
    /// Initializes a writer adapter over an existing callback sink.
    /// </summary>
    public ValueRlpWriter(object sink, ValueRlpWriteSink writeSink, ValueRlpWriteByteSink writeByteSink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(writeSink);
        ArgumentNullException.ThrowIfNull(writeByteSink);

        _data = Span<byte>.Empty;
        _sink = sink;
        _writeSink = writeSink;
        _writeByteSink = writeByteSink;
        _position = 0;
    }

    /// <summary>
    /// Full caller-provided output span.
    /// </summary>
    public readonly Span<byte> Data =>
        _sink is null ? _data : throw new InvalidOperationException("Data is available only for span-backed writers.");

    /// <summary>
    /// Bytes written so far.
    /// </summary>
    public readonly ReadOnlySpan<byte> WrittenSpan =>
        _sink is null ? _data[.._position] : throw new InvalidOperationException("WrittenSpan is available only for span-backed writers.");

    /// <summary>
    /// Current write position in <see cref="Data"/>.
    /// </summary>
    public int Position
    {
        readonly get => _sink is null ? _position : ThrowSinkPositionNotSupported();
        set
        {
            if (_sink is null)
            {
                _position = value;
            }
            else
            {
                ThrowSinkPositionNotSupported();
            }
        }
    }

    public readonly int Length => _sink is null ? _data.Length : ThrowSinkPositionNotSupported();

    public void StartByteArray(int contentLength, bool firstByteLessThan128)
    {
        switch (contentLength)
        {
            case 0:
                WriteByte(EmptyArrayByte);
                break;
            case 1 when firstByteLessThan128:
                break;
            case < RlpHelpers.SmallPrefixBarrier:
                {
                    byte smallPrefix = (byte)(contentLength + 128);
                    WriteByte(smallPrefix);
                    break;
                }
            default:
                {
                    int lengthOfLength = Rlp.LengthOfLength(contentLength);
                    byte prefix = (byte)(183 + lengthOfLength);
                    WriteByte(prefix);
                    WriteEncodedLength(contentLength);
                    break;
                }
        }
    }

    public void StartSequence(int contentLength)
    {
        if (contentLength < RlpHelpers.SmallPrefixBarrier)
        {
            WriteByte((byte)(192 + contentLength));
        }
        else
        {
            WriteByte((byte)(247 + Rlp.LengthOfLength(contentLength)));
            WriteEncodedLength(contentLength);
        }
    }

    private void WriteEncodedLength(int value)
    {
        switch (value)
        {
            case < 1 << 8:
                WriteByte((byte)value);
                return;
            case < 1 << 16:
                WriteByte((byte)(value >> 8));
                WriteByte((byte)value);
                return;
            case < 1 << 24:
                WriteByte((byte)(value >> 16));
                WriteByte((byte)(value >> 8));
                WriteByte((byte)value);
                return;
            default:
                WriteByte((byte)(value >> 24));
                WriteByte((byte)(value >> 16));
                WriteByte((byte)(value >> 8));
                WriteByte((byte)value);
                return;
        }
    }

    public void WriteByte(byte byteToWrite)
    {
        if (_sink is { } sink)
        {
            _writeByteSink!(sink, byteToWrite);
            return;
        }

        _data[_position++] = byteToWrite;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(byte[] bytesToWrite) => Write(bytesToWrite.AsSpan());

    public void Write(scoped ReadOnlySpan<byte> bytesToWrite)
    {
        if (_sink is { } sink)
        {
            _writeSink!(sink, bytesToWrite);
            return;
        }

        bytesToWrite.CopyTo(_data.Slice(_position, bytesToWrite.Length));
        _position += bytesToWrite.Length;
    }

    public void WriteByteArrayList(IByteArrayList? list)
    {
        if (list is null || list.Count == 0)
        {
            EncodeNullObject();
            return;
        }

        if (list is IRlpWrapper rlpWrapper)
        {
            rlpWrapper.Write(ref this);
            return;
        }

        int contentLength = 0;
        for (int i = 0; i < list.Count; i++)
        {
            contentLength += Rlp.LengthOf(list[i]);
        }

        StartSequence(contentLength);
        for (int i = 0; i < list.Count; i++)
        {
            Encode(list[i]);
        }
    }

    private void WriteZero(int length)
    {
        if (_sink is { } sink)
        {
            WriteZero(sink, _writeSink!, length);
            return;
        }

        _data.Slice(_position, length).Clear();
        _position += length;
    }

    private static void WriteZero(object sink, ValueRlpWriteSink writeSink, int length)
    {
        Span<byte> zeros = stackalloc byte[Math.Min(length, 256)];
        zeros.Clear();
        while (length > 0)
        {
            int chunkLength = Math.Min(length, zeros.Length);
            writeSink(sink, zeros[..chunkLength]);
            length -= chunkLength;
        }
    }

    public void Encode(Hash256? keccak)
    {
        if (keccak is null)
        {
            WriteByte(EmptyArrayByte);
        }
        else if (ReferenceEquals(keccak, Keccak.EmptyTreeHash))
        {
            Write(Rlp.OfEmptyTreeHash.Bytes);
        }
        else if (ReferenceEquals(keccak, Keccak.OfAnEmptyString))
        {
            Write(Rlp.OfEmptyStringHash.Bytes);
        }
        else
        {
            WriteByte(160);
            Write(keccak.Bytes);
        }
    }

    public void Encode(in ValueHash256? keccak)
    {
        if (keccak is null)
        {
            WriteByte(EmptyArrayByte);
        }
        else
        {
            WriteByte(160);
            Write(keccak.Value.Bytes);
        }
    }

    public void Encode(Hash256[] keccaks)
    {
        if (keccaks is null)
        {
            EncodeNullObject();
        }
        else
        {
            StartSequence(Rlp.LengthOf(keccaks));
            for (int i = 0; i < keccaks.Length; i++)
            {
                Encode(keccaks[i]);
            }
        }
    }

    public void Encode(ValueHash256[] keccaks)
    {
        if (keccaks is null)
        {
            EncodeNullObject();
        }
        else
        {
            StartSequence(Rlp.LengthOf(keccaks));
            for (int i = 0; i < keccaks.Length; i++)
            {
                Encode(keccaks[i]);
            }
        }
    }

    public void Encode(IReadOnlyList<Hash256> keccaks)
    {
        if (keccaks is null)
        {
            EncodeNullObject();
        }
        else
        {
            StartSequence(Rlp.LengthOf(keccaks));
            int count = keccaks.Count;
            for (int i = 0; i < count; i++)
            {
                Encode(keccaks[i]);
            }
        }
    }

    public void Encode(IReadOnlyList<ValueHash256> keccaks)
    {
        if (keccaks is null)
        {
            EncodeNullObject();
        }
        else
        {
            StartSequence(Rlp.LengthOf(keccaks));
            int count = keccaks.Count;
            for (int i = 0; i < count; i++)
            {
                Encode(keccaks[i]);
            }
        }
    }

    public void Encode(Address? address)
    {
        if (address is null)
        {
            WriteByte(EmptyArrayByte);
        }
        else
        {
            WriteByte(148);
            Write(address.Bytes);
        }
    }

    public void Encode(Rlp? rlp)
    {
        if (rlp is null)
        {
            WriteByte(EmptyArrayByte);
        }
        else
        {
            Write(rlp.Bytes);
        }
    }

    public void Encode(Bloom? bloom)
    {
        if (ReferenceEquals(bloom, Bloom.Empty))
        {
            WriteByte(185);
            WriteByte(1);
            WriteByte(0);
            WriteZero(256);
        }
        else if (bloom is null)
        {
            WriteByte(EmptyArrayByte);
        }
        else
        {
            WriteByte(185);
            WriteByte(1);
            WriteByte(0);
            Write(bloom.Bytes);
        }
    }

    public void Encode(byte value)
    {
        if (value == 0)
        {
            WriteByte(128);
        }
        else if (value < 128)
        {
            WriteByte(value);
        }
        else
        {
            WriteByte(129);
            WriteByte(value);
        }
    }

    public void Encode(bool value) => Encode(value ? (byte)1 : (byte)0);

    public void Encode(int value) => Encode((ulong)(long)value);

    public void Encode(uint value) => Encode((ulong)value);

    public void Encode(long value) => Encode((ulong)value);

    [SkipLocalsInit]
    public void Encode(ulong value)
    {
        if (value < 128)
        {
            byte singleByte = value > 0 ? (byte)value : EmptyArrayByte;
            WriteByte(singleByte);
            return;
        }

        int leadingZeroBytes = BitOperations.LeadingZeroCount(value) >> 3;
        int valueLength = sizeof(ulong) - leadingZeroBytes;

        value = BinaryPrimitives.ReverseEndianness(value);
        Span<byte> valueSpan = MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, byte>(ref value), sizeof(ulong));
        Span<byte> output = stackalloc byte[1 + sizeof(ulong)];

        byte prefix = (byte)(0x80 + valueLength);
        if (leadingZeroBytes > 0)
        {
            valueSpan[leadingZeroBytes - 1] = prefix;
            output = valueSpan.Slice(leadingZeroBytes - 1, 1 + valueLength);
        }
        else
        {
            output[0] = prefix;
            valueSpan.Slice(leadingZeroBytes, valueLength).CopyTo(output.Slice(1));
            output = output.Slice(0, 1 + valueLength);
        }

        Write(output);
    }

    public void Encode(in UInt256 value, int length = -1)
    {
        if (value.IsZero && length == -1)
        {
            WriteByte(EmptyArrayByte);
        }
        else
        {
            Span<byte> bytes = stackalloc byte[32];
            value.ToBigEndian(bytes);
            Encode(length != -1 ? bytes.Slice(bytes.Length - length, length) : bytes.WithoutLeadingZeros());
        }
    }

    public void Encode(in EvmWord value)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<EvmWord, byte>(ref Unsafe.AsRef(in value)), 32);
        int nonZero = bytes.IndexOfAnyExcept((byte)0);
        if (nonZero < 0)
        {
            WriteByte(EmptyArrayByte);
        }
        else
        {
            Encode(bytes.Slice(nonZero));
        }
    }

    public void Encode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            WriteByte(128);
        }
        else
        {
            Encode(Encoding.ASCII.GetBytes(value));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(byte[] input) => Encode(input.AsSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(Memory<byte>? input)
    {
        if (input is null)
        {
            WriteByte(EmptyArrayByte);
            return;
        }

        Encode(input.Value.Span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(in ReadOnlyMemory<byte> input) => Encode(input.Span);

    public void Encode(scoped ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            WriteByte(EmptyArrayByte);
        }
        else if (input.Length == 1 && input[0] < 128)
        {
            WriteByte(input[0]);
        }
        else if (input.Length < RlpHelpers.SmallPrefixBarrier)
        {
            byte smallPrefix = (byte)(input.Length + 128);
            WriteByte(smallPrefix);
            Write(input);
        }
        else
        {
            int lengthOfLength = Rlp.LengthOfLength(input.Length);
            byte prefix = (byte)(183 + lengthOfLength);
            WriteByte(prefix);
            WriteEncodedLength(input.Length);
            Write(input);
        }
    }

    public void Encode(byte[][] arrays)
    {
        int itemsLength = 0;
        foreach (byte[] array in arrays)
        {
            itemsLength += Rlp.LengthOf(array);
        }

        StartSequence(itemsLength);
        foreach (byte[] array in arrays)
        {
            Encode(array);
        }
    }

    public void Reset() => Position = 0;

    public void EncodeNullObject() => WriteByte(EmptySequenceByte);

    public void EncodeEmptyByteArray() => WriteByte(EmptyArrayByte);

    private const byte EmptyArrayByte = 128;
    private const byte EmptySequenceByte = 192;

    public override readonly string ToString() => _sink is null
        ? $"[{nameof(ValueRlpWriter)}|{_position}/{Length}]"
        : $"[{nameof(ValueRlpWriter)}|{_sink.GetType().Name}]";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ThrowSinkPositionNotSupported()
        => throw new InvalidOperationException("Position and Length are unavailable for sink-backed writers.");
}
