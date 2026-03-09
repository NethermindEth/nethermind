// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public readonly int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
        => RlpHelpers.CountItems(Data, _position, beforePosition ?? Data.Length, maxSearch);

    public void SkipLength() => SkipBytes(PeekPrefixLength());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int PeekPrefixLength() => RlpHelpers.GetPrefixLength(PeekByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int PeekNextRlpLength() => RlpHelpers.PeekNextRlpLength(Data, _position);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
        => RlpHelpers.PeekPrefixAndContentLength(Data, _position);

    public int ReadSequenceLength()
    {
        int prefix = ReadByte();
        if (prefix < 192)
        {
            RlpHelpers.ThrowUnexpectedPrefix(prefix);
        }

        if (prefix <= 247)
        {
            return prefix - 192;
        }

        int lengthOfContentLength = prefix - 247;
        int contentLength = DeserializeLength(lengthOfContentLength);
        if (contentLength < RlpHelpers.SmallPrefixBarrier)
        {
            RlpHelpers.ThrowUnexpectedLength(contentLength);
        }

        return contentLength;
    }

    private int DeserializeLength(int lengthOfLength)
    {
        if (lengthOfLength == 0 || (uint)lengthOfLength > 4)
        {
            RlpHelpers.ThrowInvalidLength(lengthOfLength);
        }

        ref byte firstElement = ref MemoryMarshal.GetReference(Read(lengthOfLength));
        return RlpHelpers.DeserializeLengthRef(ref firstElement, lengthOfLength);
    }


    public byte ReadByte() => Data[_position++];

    public readonly byte PeekByte() => Data[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBytes(int length) => _position += length;

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
            RlpHelpers.ThrowUnexpectedPrefix(prefix);
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
            RlpHelpers.ThrowUnexpectedPrefix(prefix);
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

    public readonly ReadOnlySpan<byte> Peek(int length) => Peek(0, length);

    public readonly ReadOnlySpan<byte> Peek(int offset, int length) => Data.Slice(_position + offset, length);

    public byte[] DecodeByteArray() => Rlp.ByteSpanToArray(DecodeByteArraySpan());

    public ReadOnlySpan<byte> DecodeByteArraySpan()
    {
        int prefix = ReadByte();
        ReadOnlySpan<byte> span = RlpStream.SingleBytes;
        if ((uint)prefix < (uint)span.Length)
        {
            return span.Slice(prefix, 1);
        }

        if (prefix == Rlp.EmptyByteArrayByte)
        {
            return default;
        }

        if (prefix <= 183)
        {
            int length = prefix - Rlp.EmptyByteArrayByte;
            ReadOnlySpan<byte> buffer = Read(length);
            if (buffer.Length == 1 && buffer[0] < 128)
            {
                RlpHelpers.ThrowUnexpectedByteValue(buffer[0]);
            }

            return buffer;
        }

        return DecodeLargerByteArraySpan(prefix);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ReadOnlySpan<byte> DecodeLargerByteArraySpan(int prefix)
    {
        if (prefix < 192)
        {
            int lengthOfLength = prefix - 183;
            if (lengthOfLength > 4)
            {
                RlpHelpers.ThrowSequenceLengthTooLong();
            }

            int length = DeserializeLength(lengthOfLength);
            if (length < RlpHelpers.SmallPrefixBarrier)
            {
                RlpHelpers.ThrowUnexpectedLength(length);
            }

            return Read(length);
        }

        RlpHelpers.ThrowUnexpectedPrefix(prefix);
        return default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem() => SkipBytes(PeekNextRlpLength());

    public void Reset() => _position = 0;

    public override readonly string ToString() => $"[{nameof(RlpStream)}|{_position}/{Data.Length}]";
}
