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

public ref struct ValueRlpStream(SpanSource data)
{
    public readonly ReadOnlySpan<byte> Data = data.Span;
    private int _position = 0;

    internal readonly string Description =>
        Data[..Math.Min(Rlp.DebugMessageContentLength, Data.Length)].ToHexString() ?? "0x";

    public int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    public readonly bool IsNull => Unsafe.IsNullRef(ref MemoryMarshal.GetReference(Data));
    public readonly bool IsNotNull => !IsNull;
    public readonly int Length => Data.Length;

    public int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
    {
        int positionStored = _position;
        int numberOfItems = 0;
        while (_position < (beforePosition ?? Data.Length))
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
                _position--;
                int sequenceLength = ReadSequenceLength();
                SkipBytes(sequenceLength);
            }

            numberOfItems++;
            if (numberOfItems >= maxSearch)
            {
                break;
            }
        }

        _position = positionStored;
        return numberOfItems;
    }

    public void SkipLength()
    {
        SkipBytes(PeekPrefixLength());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int PeekPrefixLength()
    {
        return RlpHelpers.CalculatePrefixLength(PeekByte());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int PeekNextRlpLength()
    {
        int prefix = PeekByte();
        return prefix switch
        {
            // Single byte (0x00..0x7f). The byte is its own content. Prefix = 0, Content = 1.
            < 128 => 1,
            // Short string (0x80..0xb7). Prefix = 1, Content = prefix - 0x80 (0..55).
            <= 183 => 1 + prefix - 0x80,
            // Long string (0xb8..0xbf). Content length >= 56. The next (prefix-0xb7) bytes encode the length.
            < 192 => PeekLongStringRlpLength(prefix),
            // Short list (0xc0..0xf7). Prefix = 1, Content = prefix - 0xc0 (0..55).
            <= 247 => 1 + prefix - 0xc0,
            // Long list (0xf8..0xff). Content >= 56. The next (prefix-0xf7) bytes encode the length.
            _ => PeekLongListRlpLength(prefix),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
    {
        int prefix = PeekByte();
        return prefix switch
        {
            // Single byte (0x00..0x7f). The byte is its own content. Prefix = 0, Content = 1.
            < 128 => (0, 1),
            // Short string (0x80..0xb7). Prefix = 1, Content = prefix - 0x80 (0..55).
            <= 183 => (1, prefix - 0x80),
            // Long string (0xb8..0xbf). Content length >= 56. The next (prefix-0xb7) bytes encode the length.
            < 192 => PeekLongStringPrefixAndContentLength(prefix),
            // Short list (0xc0..0xf7). Prefix = 1, Content = prefix - 0xc0 (0..55).
            <= 247 => (1, prefix - 0xc0),
            // Long list (0xf8..0xff). Content >= 56. The next (prefix-0xf7) bytes encode the length.
            _ => PeekLongListPrefixAndContentLength(prefix),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly int PeekLongStringRlpLength(int prefix)
    {
        int lengthOfLength = prefix - 183;
        if ((uint)lengthOfLength > 4)
        {
            // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
            RlpHelpers.ThrowSequenceLengthTooLong();
        }

        int length = PeekDeserializeLength(1, lengthOfLength);
        if (length < 56)
        {
            RlpHelpers.ThrowLengthTooLong(length);
        }

        return lengthOfLength + 1 + length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly (int prefixLength, int contentLength) PeekLongStringPrefixAndContentLength(int prefix)
    {
        int lengthOfLength = prefix - 183;
        if ((uint)lengthOfLength > 4)
        {
            // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
            RlpHelpers.ThrowSequenceLengthTooLong();
        }

        int length = PeekDeserializeLength(1, lengthOfLength);
        if (length < 56)
        {
            RlpHelpers.ThrowLengthTooLong(length);
        }

        return (lengthOfLength + 1, length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly int PeekLongListRlpLength(int prefix)
    {
        int lengthOfContentLength = prefix - 247;
        int contentLength = PeekDeserializeLength(1, lengthOfContentLength);
        if (contentLength < 56)
        {
            RlpHelpers.ThrowLengthTooLong(contentLength);
        }

        return lengthOfContentLength + 1 + contentLength;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly (int prefixLength, int contentLength) PeekLongListPrefixAndContentLength(int prefix)
    {
        int lengthOfContentLength = prefix - 247;
        int contentLength = PeekDeserializeLength(1, lengthOfContentLength);
        if (contentLength < 56)
        {
            RlpHelpers.ThrowLengthTooLong(contentLength);
        }

        return (lengthOfContentLength + 1, contentLength);
    }



    public int ReadSequenceLength()
    {
        int prefix = ReadByte();
        if (prefix < 192)
        {
            throw new RlpException(
                $"Expected a sequence prefix to be in the range of <192, 255> and got {prefix} at position {_position} in the message of length {Data.Length} starting with {Description}");
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
            RlpHelpers.ThrowArgumentOutOfRangeException(lengthOfLength);
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
            RlpHelpers.ThrowArgumentOutOfRangeException(lengthOfLength);
        }

        // Will use Unsafe.ReadUnaligned as we know the length of the span is same
        // as what we asked for and then explicitly check lengths, so can skip the
        // additional bounds checking from BinaryPrimitives.ReadUInt16BigEndian etc
        ref byte firstElement = ref MemoryMarshal.GetReference(Peek(offset, lengthOfLength));

        return DeserializeLengthRef(ref firstElement, lengthOfLength);
    }

    private static int DeserializeLengthRef(ref byte firstElement, int lengthOfLength)
    {
        return RlpHelpers.DeserializeLengthRef(ref firstElement, lengthOfLength);
    }



    public byte ReadByte()
    {
        return Data[_position++];
    }

    public readonly byte PeekByte()
    {
        return Data[_position];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBytes(int length)
    {
        _position += length;
    }

    public ReadOnlySpan<byte> Read(int length)
    {
        ReadOnlySpan<byte> data = Data.Slice(_position, length);
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
                $"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {_position} in the message of length {Data.Length} starting with {Description}");
        }

        ReadOnlySpan<byte> keccakSpan = Read(32);
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
                $"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {_position} in the message of length {Data.Length} starting with {Description}");
        }

        ReadOnlySpan<byte> keccakSpan = Read(32);
        keccak = new ValueHash256(keccakSpan);
        return true;
    }

    public readonly ReadOnlySpan<byte> PeekNextItem()
    {
        int length = PeekNextRlpLength();
        return Peek(length);
    }

    public readonly ReadOnlySpan<byte> Peek(int length)
    {
        return Peek(0, length);
    }

    public readonly ReadOnlySpan<byte> Peek(int offset, int length)
    {
        return Data.Slice(_position + offset, length);
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

        [DoesNotReturn, StackTraceHidden]
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

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedPrefix(int prefix)
        {
            throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedLength(int length)
        {
            throw new RlpException($"Expected length greater or equal 56 and was {length}");
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnexpectedLengthOfLength()
        {
            throw new RlpException("Expected length of length less or equal 4");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem()
    {
        SkipBytes(PeekNextRlpLength());
    }

    public void Reset()
    {
        _position = 0;
    }

    private const byte EmptyArrayByte = 128;

    private const byte EmptySequenceByte = 192;

    public override readonly string ToString()
    {
        return $"[{nameof(RlpStream)}|{_position}/{Data.Length}]";
    }
}
