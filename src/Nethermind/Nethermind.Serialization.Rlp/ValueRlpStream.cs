// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public ref struct ValueRlpStream(in CappedArray<byte> data)
{
    private readonly ref readonly CappedArray<byte> _data = ref data;
    private int _position = 0;

    internal readonly string Description =>
        Data.AsSpan(0, Math.Min(Rlp.DebugMessageContentLength, Length)).ToHexString() ?? "0x";

    public readonly ref readonly CappedArray<byte> Data => ref _data;

    public int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    public readonly bool IsNull => Unsafe.IsNullRef(ref Unsafe.AsRef(in _data));
    public readonly bool IsNotNull => !IsNull;
    public readonly int Length => Data.Length;

    public int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
    {
        int positionStored = Position;
        int numberOfItems = 0;
        while (Position < (beforePosition ?? Length))
        {
            int prefix = ReadByte();
            if (prefix <= 128)
            {
            }
            else if (prefix <= 183)
            {
                int length = prefix - 128;
                SkipBytes(length);
            }
            else if (prefix < 192)
            {
                int lengthOfLength = prefix - 183;
                int length = DeserializeLength(lengthOfLength);
                if (length < 56)
                {
                    throw new RlpException("Expected length greater or equal 56 and was {length}");
                }

                SkipBytes(length);
            }
            else
            {
                Position--;
                int sequenceLength = ReadSequenceLength();
                SkipBytes(sequenceLength);
            }

            numberOfItems++;
            if (numberOfItems >= maxSearch)
            {
                break;
            }
        }

        Position = positionStored;
        return numberOfItems;
    }

    public void SkipLength()
    {
        SkipBytes(PeekPrefixAndContentLength().PrefixLength);
    }

    public readonly int PeekNextRlpLength()
    {
        (int a, int b) = PeekPrefixAndContentLength();
        return a + b;
    }

    public readonly (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
    {
        (int prefixLength, int contentLength) result;
        int prefix = PeekByte();
        if (prefix <= 128)
        {
            result = (0, 1);
        }
        else if (prefix <= 183)
        {
            result = (1, prefix - 128);
        }
        else if (prefix < 192)
        {
            int lengthOfLength = prefix - 183;
            if ((uint)lengthOfLength > 4)
            {
                // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                ThrowSequenceLengthTooLong();
            }

            int length = PeekDeserializeLength(1, lengthOfLength);
            if (length < 56)
            {
                ThrowLengthTooLong(length);
            }

            result = (lengthOfLength + 1, length);
        }
        else if (prefix <= 247)
        {
            result = (1, prefix - 192);
        }
        else
        {
            int lengthOfContentLength = prefix - 247;
            int contentLength = PeekDeserializeLength(1, lengthOfContentLength);
            if (contentLength < 56)
            {
                ThrowLengthTooLong(contentLength);
            }


            result = (lengthOfContentLength + 1, contentLength);
        }

        return result;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowSequenceLengthTooLong()
        {
            throw new RlpException("Expected length of length less or equal 4");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowLengthTooLong(int length)
        {
            throw new RlpException($"Expected length greater or equal 56 and was {length}");
        }
    }

    public int ReadSequenceLength()
    {
        int prefix = ReadByte();
        if (prefix < 192)
        {
            throw new RlpException(
                $"Expected a sequence prefix to be in the range of <192, 255> and got {prefix} at position {Position} in the message of length {Length} starting with {Description}");
        }

        if (prefix <= 247)
        {
            return prefix - 192;
        }

        int lengthOfContentLength = prefix - 247;
        int contentLength = DeserializeLength(lengthOfContentLength);
        if (contentLength < 56)
        {
            throw new RlpException($"Expected length greater or equal 56 and got {contentLength}");
        }

        return contentLength;
    }

    private int DeserializeLength(int lengthOfLength)
    {
        if (lengthOfLength == 0 || (uint)lengthOfLength > 4)
        {
            ThrowArgumentOutOfRangeException(lengthOfLength);
        }

        // Will use Unsafe.ReadUnaligned as we know the length of the span is same
        // as what we asked for and then explicitly check lengths, so can skip the
        // additional bounds checking from BinaryPrimitives.ReadUInt16BigEndian etc
        ref byte firstElement = ref MemoryMarshal.GetReference(Read(lengthOfLength));

        return DeserializeLengthRef(ref firstElement, lengthOfLength);
    }

    private readonly int PeekDeserializeLength(int offset, int lengthOfLength)
    {
        if (lengthOfLength == 0 || (uint)lengthOfLength > 4)
        {
            ThrowArgumentOutOfRangeException(lengthOfLength);
        }

        // Will use Unsafe.ReadUnaligned as we know the length of the span is same
        // as what we asked for and then explicitly check lengths, so can skip the
        // additional bounds checking from BinaryPrimitives.ReadUInt16BigEndian etc
        ref byte firstElement = ref MemoryMarshal.GetReference(Peek(offset, lengthOfLength));

        return DeserializeLengthRef(ref firstElement, lengthOfLength);
    }

    private static int DeserializeLengthRef(ref byte firstElement, int lengthOfLength)
    {
        int result = firstElement;
        if (result == 0)
        {
            ThrowInvalidData();
        }

        if (lengthOfLength == 1)
        {
            // Already read above
            // result = span[0];
        }
        else if (lengthOfLength == 2)
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref firstElement));
            }
            else
            {
                result = Unsafe.ReadUnaligned<ushort>(ref firstElement);
            }
        }
        else if (lengthOfLength == 3)
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1)))
                    | (result << 16);
            }
            else
            {
                result = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1))
                    | (result << 16);
            }
        }
        else
        {
            if (BitConverter.IsLittleEndian)
            {
                result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref firstElement));
            }
            else
            {
                result = Unsafe.ReadUnaligned<int>(ref firstElement);
            }
        }

        return result;

        [DoesNotReturn]
        static void ThrowInvalidData()
        {
            throw new RlpException("Length starts with 0");
        }
    }

    [DoesNotReturn]
    static void ThrowArgumentOutOfRangeException(int lengthOfLength)
    {
        throw new InvalidOperationException($"Invalid length of length = {lengthOfLength}");
    }

    public byte ReadByte()
    {
        return Data![_position++];
    }

    public readonly byte PeekByte()
    {
        return Data![_position];
    }

    private void SkipBytes(int length)
    {
        _position += length;
    }

    public Span<byte> Read(int length)
    {
        Span<byte> data = Data.AsSpan(_position, length);
        _position += length;
        return data;
    }

    public Hash256? DecodeKeccak()
    {
        int prefix = ReadByte();
        if (prefix == 128)
        {
            return null;
        }

        if (prefix != 128 + 32)
        {
            throw new RlpException(
                $"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {Position} in the message of length {Length} starting with {Description}");
        }

        Span<byte> keccakSpan = Read(32);
        if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
        {
            return Keccak.OfAnEmptyString;
        }

        if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
        {
            return Keccak.EmptyTreeHash;
        }

        return new Hash256(keccakSpan);
    }

    public bool DecodeValueKeccak(out ValueHash256 keccak)
    {
        Unsafe.SkipInit(out keccak);
        int prefix = ReadByte();
        if (prefix == 128)
        {
            return false;
        }

        if (prefix != 128 + 32)
        {
            throw new RlpException(
                $"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {Position} in the message of length {Length} starting with {Description}");
        }

        Span<byte> keccakSpan = Read(32);
        keccak = new ValueHash256(keccakSpan);
        return true;
    }

    public readonly Span<byte> PeekNextItem()
    {
        int length = PeekNextRlpLength();
        return Peek(length);
    }

    public readonly Span<byte> Peek(int length)
    {
        return Peek(0, length);
    }

    public readonly Span<byte> Peek(int offset, int length)
    {
        return Data.AsSpan(_position + offset, length);
    }

    public byte[] DecodeByteArray()
    {
        return Rlp.ByteSpanToArray(DecodeByteArraySpan());
    }

    public ReadOnlySpan<byte> DecodeByteArraySpan()
    {
        int prefix = ReadByte();
        ReadOnlySpan<byte> span = RlpStream.SingleBytes;
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
            ReadOnlySpan<byte> buffer = Read(length);
            if (buffer.Length == 1 && buffer[0] < 128)
            {
                ThrowUnexpectedValue(buffer[0]);
            }

            return buffer;
        }

        return DecodeLargerByteArraySpan(prefix);

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnexpectedValue(int buffer0)
        {
            throw new RlpException($"Unexpected byte value {buffer0}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ReadOnlySpan<byte> DecodeLargerByteArraySpan(int prefix)
    {
        if (prefix < 192)
        {
            int lengthOfLength = prefix - 183;
            if (lengthOfLength > 4)
            {
                // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                ThrowUnexpectedLengthOfLength();
            }

            int length = DeserializeLength(lengthOfLength);
            if (length < 56)
            {
                ThrowUnexpectedLength(length);
            }

            return Read(length);
        }

        ThrowUnexpectedPrefix(prefix);
        return default;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnexpectedPrefix(int prefix)
        {
            throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnexpectedLength(int length)
        {
            throw new RlpException($"Expected length greater or equal 56 and was {length}");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnexpectedLengthOfLength()
        {
            throw new RlpException("Expected length of length less or equal 4");
        }
    }

    public void SkipItem()
    {
        (int prefix, int content) = PeekPrefixAndContentLength();
        SkipBytes(prefix + content);
    }

    public void Reset()
    {
        Position = 0;
    }

    private const byte EmptyArrayByte = 128;

    private const byte EmptySequenceByte = 192;

    public override readonly string ToString()
    {
        return $"[{nameof(RlpStream)}|{Position}/{Length}]";
    }
}
