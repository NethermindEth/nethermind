// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public ref struct RefRlpStream
{
    public readonly Span<byte> Data;
    public int Position { get; set; }
    public int Length => Data.Length;

    public RefRlpStream(Span<byte> data)
    {
        Data = data;
    }

    public void StartSequence(int contentLength)
    {
        if (contentLength < RlpStream.SmallPrefixBarrier)
        {
            byte prefix = (byte)(192 + contentLength);
            WriteByte(prefix);
        }
        else
        {
            LongSequenceStart(contentLength);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LongSequenceStart(int contentLength)
    {
        var prefix = (byte)(247 + Rlp.LengthOfLength(contentLength));
        WriteByte(prefix);
        WriteEncodedLength(contentLength);
    }

    public void WriteByte(byte byteToWrite)
    {
        Data[Position++] = byteToWrite;
    }

    public void Write(scoped ReadOnlySpan<byte> bytesToWrite)
    {
        bytesToWrite.CopyTo(Data.Slice(Position, bytesToWrite.Length));
        Position += bytesToWrite.Length;
    }

    public void EncodeKeccak(in ReadOnlySpan<byte> keccak)
    {
        WriteByte(160);
        Write(keccak);
    }

    public void EncodeEmptyArray() => WriteByte(RlpStream.EmptyArrayByte);

    public void Encode(scoped ReadOnlySpan<byte> input)
    {
        if (input.Length == 0)
        {
            WriteByte(RlpStream.EmptyArrayByte);
        }
        else if (input.Length == 1 && input[0] < 128)
        {
            WriteByte(input[0]);
        }
        else if (input.Length < RlpStream.SmallPrefixBarrier)
        {
            byte smallPrefix = (byte)(input.Length + 128);
            WriteByte(smallPrefix);
            Write(input);
        }
        else
        {
            EncodeLong(input);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EncodeLong(scoped ReadOnlySpan<byte> input)
    {
        int lengthOfLength = Rlp.LengthOfLength(input.Length);
        byte prefix = (byte)(183 + lengthOfLength);
        WriteByte(prefix);
        WriteEncodedLength(input.Length);
        Write(input);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Encode(in UInt256 value)
    {
        if (value.IsZero)
        {
            WriteByte(RlpStream.EmptyArrayByte);
        }
        else
        {
            Vector256<byte> data;
            Unsafe.SkipInit(out data);
            Span<byte> bytes = MemoryMarshal.CreateSpan(ref Unsafe.As<Vector256<byte>, byte>(ref data), Vector256<byte>.Count);

            value.ToBigEndian(bytes);
            Encode(bytes.WithoutLeadingZeros());
        }
    }

    public void Encode(Hash256 hash)
    {
        Debug.Assert(hash is not null);
        var newPosition = Position + Rlp.LengthOfKeccakRlp;
        if ((uint)newPosition > (uint)Data.Length)
        {
            ThrowArgumentOutOfRangeException();
        }

        ref byte dest = ref Unsafe.Add(ref MemoryMarshal.GetReference(Data), Position);

        dest = 160;
        Unsafe.As<byte, ValueHash256>(ref Unsafe.Add(ref dest,  1)) = hash.ValueHash256;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowArgumentOutOfRangeException()
        {
            throw new ArgumentOutOfRangeException("Data");
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

    public override string ToString() => $"[{nameof(RlpStream)}|{Position}/{Length}]";

    public static UInt256 DecodeUInt256(ReadOnlySpan<byte> span)
    {
        byte byteValue = span[0];
        if (byteValue == 0)
        {
            ThrowNonCanonical();
        }

        if (byteValue < 128)
        {
            return byteValue;
        }

        ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan(span);

        if (byteSpan.Length > 32)
        {
            ThrowWrongSize();
        }

        if (byteSpan.Length > 1 && byteSpan[0] == 0)
        {
            ThrowNonCanonical();
        }

        return new UInt256(byteSpan, true);

        ReadOnlySpan<byte> DecodeByteArraySpan(ReadOnlySpan<byte> data)
        {
            int prefix = data[0];
            ReadOnlySpan<byte> span = SingleBytes();
            if ((uint)prefix < (uint)span.Length)
            {
                return span.Slice(prefix, 1);
            }

            if (prefix == 128)
            {
                return default;
            }

            if (prefix <= 183)
            {
                int length = prefix - 128;
                ReadOnlySpan<byte> buffer = data.Slice(1, length);
                if (buffer.Length == 1 && buffer[0] < 128)
                {
                    ThrowUnexpectedValue(buffer[0]);
                }

                return buffer;
            }

            ThrowUnexpectedValue(prefix);
            return default;

            static ReadOnlySpan<byte> SingleBytes() =>
            [
                0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
                28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53,
                54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
                80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104,
                105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125,
                126, 127
            ];

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowUnexpectedValue(int buffer0)
            {
                throw new Exception($"Unexpected byte value {buffer0}");
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNonCanonical()
        {
            throw new Exception($"Non-canonical UInt256 (leading zero bytes)");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowWrongSize()
        {
            throw new Exception("UInt256 cannot be longer than 32 bytes");
        }
    }
}
