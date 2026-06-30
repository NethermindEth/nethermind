// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

public ref struct RlpReader
{
    private readonly Memory<byte> _memory;
    private readonly bool _isMemoryBacked;
    private bool _isNotNull;

    public RlpReader(scoped in ReadOnlySpan<byte> data)
    {
        Data = data;
        Position = 0;
        _memory = default;
        _isMemoryBacked = false;
        _isNotNull = true;
    }

    public RlpReader(byte[]? data) : this((data ?? []).AsSpan())
    {
    }

    public RlpReader(Memory<byte> data)
    {
        Data = data.Span;
        Position = 0;
        _memory = data;
        _isMemoryBacked = true;
        _isNotNull = true;
    }

    public RlpReader(CappedArray<byte> data)
    {
        Data = data.AsSpan();
        Position = 0;
        _memory = default;
        _isMemoryBacked = false;
        _isNotNull = data.IsNotNull;
    }

    public ReadOnlySpan<byte> Data { get; }

    public readonly bool IsMemoryBacked => _isMemoryBacked;

    public readonly bool IsNull => !_isNotNull;

    public readonly bool IsNotNull => _isNotNull;

    public int Position { get; set; }

    public readonly int Length => Data.Length;

    public readonly bool IsSequenceNext() => Data[Position] >= 192;

    public readonly int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
        => RlpHelpers.CountItems(Data, Position, beforePosition ?? Data.Length, maxSearch);

    public void SkipLength() => Position += PeekPrefixLength();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int PeekPrefixLength() => RlpHelpers.GetPrefixLength(Data[Position]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextRlpLength() => RlpHelpers.PeekNextRlpLength(Data, Position);

    public readonly ReadOnlySpan<byte> Peek(int length) => Peek(0, length);

    public readonly ReadOnlySpan<byte> Peek(int offset, int length) => Data.Slice(Position + offset, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int PrefixLength, int ContentLength) ReadPrefixAndContentLength()
    {
        (int prefixLength, int contentLength) = RlpHelpers.PeekPrefixAndContentLength(Data, Position);
        Position += Math.Max(prefixLength, 1);
        return (prefixLength, contentLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
        => RlpHelpers.PeekPrefixAndContentLength(Data, Position);

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

        int result = RlpHelpers.DeserializeLengthRef(ref MemoryMarshal.GetReference(Data.Slice(Position, lengthOfLength)), lengthOfLength);
        Position += lengthOfLength;
        return result;
    }

    public byte ReadByte() => Data[Position++];

    public ReadOnlySpan<byte> Read(int length)
    {
        ReadOnlySpan<byte> data = Data.Slice(Position, length);
        Position += length;
        return data;
    }

    public Memory<byte> ReadMemory(int length)
    {
        if (!_isMemoryBacked)
        {
            return Read(length).ToArray();
        }

        Memory<byte> data = _memory.Slice(Position, length);
        Position += length;
        return data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Check(int nextCheck)
    {
        if (Position != nextCheck)
        {
            ThrowCheckpointFailed(nextCheck, Position);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void CheckEnd()
    {
        if (Position != Length)
        {
            ThrowCheckEndFailed(Position);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowCheckpointFailed(int expected, int position) =>
        throw new RlpException($"Data checkpoint failed. Expected {expected} and is {position}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowCheckEndFailed(int position) =>
        throw new RlpException($"Data checkpoint failed. Expected to reach the end of the sequence, but is at {position}");

    // Used to avoid allocating detailed error strings on receipt fallback decode paths.
    private class DecodeKeccakRlpException : RlpException
    {
        private readonly int _prefix;
        private readonly int _position;
        private readonly int _dataLength;
        private string? _message;

        public DecodeKeccakRlpException(string message, Exception inner) : base(message, inner)
        {
        }

        public DecodeKeccakRlpException(string message) : base(message)
        {
        }

        public DecodeKeccakRlpException(in int prefix, in int position, in int dataLength) : this(string.Empty)
        {
            _prefix = prefix;
            _position = position;
            _dataLength = dataLength;
        }

        public override string Message => _message ??= ConstructMessage();

        private string ConstructMessage() => $"Unexpected prefix of {_prefix} when decoding {nameof(Hash256)} at position {_position} in the message of length {_dataLength}.";
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
            ThrowKeccakDecodeException(prefix);
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

    public ValueHash256? DecodeValueKeccak()
    {
        int prefix = ReadByte();
        if (prefix == 128)
        {
            return null;
        }

        if (prefix != 128 + 32)
        {
            ThrowKeccakDecodeException(prefix);
        }

        ReadOnlySpan<byte> keccakSpan = Read(32);
        if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
        {
            return Keccak.OfAnEmptyString.ValueHash256;
        }

        if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
        {
            return Keccak.EmptyTreeHash.ValueHash256;
        }

        return new ValueHash256(keccakSpan);
    }

    public bool TryDecodeValueKeccak(out ValueHash256 keccak)
    {
        Unsafe.SkipInit(out keccak);

        int prefix = ReadByte();
        if (prefix == 128)
        {
            return false;
        }

        if (prefix != 128 + 32)
        {
            ThrowKeccakDecodeException(prefix);
        }

        keccak = new ValueHash256(Read(32));
        return true;
    }

    public Hash256? DecodeZeroPrefixKeccak()
    {
        int prefix = PeekByte();
        if (prefix == 128)
        {
            ReadByte();
            return null;
        }

        ReadOnlySpan<byte> theSpan = DecodeByteArraySpan(RlpLimit.L32);
        Span<byte> keccakBytes = stackalloc byte[32];
        keccakBytes.Clear();
        theSpan.CopyTo(keccakBytes[(32 - theSpan.Length)..]);
        return new Hash256(keccakBytes);
    }

    public void DecodeKeccakStructRef(out Hash256StructRef keccak)
    {
        int prefix = ReadByte();
        if (prefix == 128)
        {
            keccak = new Hash256StructRef(Keccak.Zero.Bytes);
        }
        else if (prefix != 128 + 32)
        {
            ThrowKeccakDecodeException(prefix);
            keccak = default;
        }
        else
        {
            ReadOnlySpan<byte> keccakSpan = Read(32);
            if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                keccak = new Hash256StructRef(Keccak.OfAnEmptyString.Bytes);
            }
            else if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
            {
                keccak = new Hash256StructRef(Keccak.EmptyTreeHash.Bytes);
            }
            else
            {
                keccak = new Hash256StructRef(keccakSpan);
            }
        }
    }

    public void DecodeZeroPrefixedKeccakStructRef(out Hash256StructRef keccak, Span<byte> buffer)
    {
        int prefix = PeekByte();
        if (prefix == 128)
        {
            ReadByte();
            keccak = new Hash256StructRef(Keccak.Zero.Bytes);
        }
        else if (prefix > 128 + 32)
        {
            ReadByte();
            ThrowKeccakDecodeException(prefix);
            keccak = default;
        }
        else if (prefix == 128 + 32)
        {
            ReadByte();
            ReadOnlySpan<byte> keccakSpan = Read(32);
            if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                keccak = new Hash256StructRef(Keccak.OfAnEmptyString.Bytes);
            }
            else if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
            {
                keccak = new Hash256StructRef(Keccak.EmptyTreeHash.Bytes);
            }
            else
            {
                keccak = new Hash256StructRef(keccakSpan);
            }
        }
        else
        {
            ReadOnlySpan<byte> theSpan = DecodeByteArraySpan(RlpLimit.L32);
            if (theSpan.Length < 32)
            {
                buffer[..(32 - theSpan.Length)].Clear();
            }
            theSpan.CopyTo(buffer[(32 - theSpan.Length)..]);
            keccak = new Hash256StructRef(buffer);
        }
    }

    public Address? DecodeAddress()
    {
        int prefix = ReadByte();
        if (prefix == 128)
        {
            return null;
        }

        if (prefix != 128 + 20)
        {
            RlpHelpers.ThrowUnexpectedPrefix(prefix);
        }

        return new Address(Read(20));
    }

    public void DecodeAddressStructRef(out AddressStructRef address)
    {
        int prefix = ReadByte();
        if (prefix == 128)
        {
            address = new AddressStructRef(Address.Zero.Bytes);
            return;
        }
        else if (prefix != 128 + 20)
        {
            RlpHelpers.ThrowUnexpectedPrefix(prefix);
        }

        address = new AddressStructRef(Read(20));
    }

    public UInt256 DecodeUInt256(int length = -1)
    {
        int position = Position;
        if (PeekByte() == 0)
        {
            RlpHelpers.ThrowNonCanonicalInteger(position);
        }

        ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan(RlpLimit.L32);
        if (byteSpan.Length > 32)
        {
            RlpHelpers.ThrowUnexpectedIntegerLength(position, byteSpan.Length);
        }

        if (length == -1)
        {
            if (byteSpan.Length > 1 && byteSpan[0] == 0)
            {
                RlpHelpers.ThrowNonCanonicalInteger(position);
            }
        }
        else if (byteSpan.Length != length)
        {
            RlpHelpers.ThrowInvalidLength(byteSpan.Length, length);
        }

        return new UInt256(byteSpan, true);
    }

    public EvmWord DecodeEvmWord()
    {
        int position = Position;
        if (PeekByte() == 0)
        {
            RlpHelpers.ThrowNonCanonicalInteger(position);
        }

        ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan(RlpLimit.L32);
        if (byteSpan.Length > 32)
        {
            RlpHelpers.ThrowUnexpectedIntegerLength(position, byteSpan.Length);
        }
        if (byteSpan.Length > 1 && byteSpan[0] == 0)
        {
            RlpHelpers.ThrowNonCanonicalInteger(position);
        }

        EvmWord result = default;
        Span<byte> dest = MemoryMarshal.CreateSpan(ref Unsafe.As<EvmWord, byte>(ref result), 32);
        byteSpan.CopyTo(dest.Slice(32 - byteSpan.Length));
        return result;
    }

    public BigInteger DecodeUBigInt()
    {
        int position = Position;
        ReadOnlySpan<byte> bytes = DecodeByteArraySpan(RlpLimit.L32);
        if (bytes.Length >= 1 && bytes[0] == 0)
        {
            RlpHelpers.ThrowNonCanonicalInteger(position);
        }
        return bytes.ToUnsignedBigInteger();
    }

    public Bloom? DecodeBloom()
    {
        ReadOnlySpan<byte> bloomBytes;

        // tks: not sure why but some nodes send us Blooms in a sequence form
        // https://github.com/NethermindEth/nethermind/issues/113
        if (Data[Position] == 249)
        {
            Position += 5; // tks: skip 249 1 2 129 127 and read 256 bytes
            bloomBytes = Read(Bloom.ByteLength);
        }
        else
        {
            bloomBytes = DecodeByteArraySpan(RlpLimit.Bloom);
            if (bloomBytes.Length == 0)
            {
                return null;
            }
        }

        if (bloomBytes.Length != Bloom.ByteLength)
        {
            throw new InvalidOperationException("Incorrect bloom RLP");
        }

        return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes);
    }

    public void DecodeBloomStructRef(out BloomStructRef bloom)
    {
        ReadOnlySpan<byte> bloomBytes;

        // tks: not sure why but some nodes send us Blooms in a sequence form
        // https://github.com/NethermindEth/nethermind/issues/113
        if (Data[Position] == 249)
        {
            Position += 5; // tks: skip 249 1 2 129 127 and read 256 bytes
            bloomBytes = Read(Bloom.ByteLength);
        }
        else
        {
            bloomBytes = DecodeByteArraySpan(RlpLimit.Bloom);
            if (bloomBytes.Length == 0)
            {
                bloom = new BloomStructRef(Bloom.Empty.Bytes);
                return;
            }
        }

        if (bloomBytes.Length != Bloom.ByteLength)
        {
            throw new InvalidOperationException("Incorrect bloom RLP");
        }

        bloom = bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? new BloomStructRef(Bloom.Empty.Bytes) : new BloomStructRef(bloomBytes);
    }

    public ReadOnlySpan<byte> PeekNextItem()
    {
        int length = PeekNextRlpLength();
        return Peek(length);
    }

    public uint DecodeUInt()
    {
        int position = Position;
        int prefix = ReadByte();

        switch (prefix)
        {
            case 0:
                return RlpHelpers.ThrowNonCanonicalInteger(position);
            case < 128:
                return (uint)prefix;
            case 128:
                return 0u;
        }

        int length = prefix - 128;
        if (length > 4)
        {
            RlpHelpers.ThrowUnexpectedIntegerLength(position, length);
        }

        uint result = 0;
        for (int i = 4; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= Data[Position + length - i];
                if (result == 0)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }
            }
        }

        if (result < 128)
        {
            RlpHelpers.ThrowNonCanonicalInteger(position);
        }

        Position += length;

        return result;
    }

    public byte[] DecodeByteArray(RlpLimit? limit = null, int size = -1)
    {
        ReadOnlySpan<byte> span = DecodeByteArraySpan(limit, size);
        if (span.Length == 0)
        {
            return [];
        }

        if (span.Length == 1)
        {
            int value = span[0];
            byte[][] arrays = RlpHelpers.SingleByteArrays;
            if ((uint)value < (uint)arrays.Length)
            {
                return arrays[value];
            }
        }

        return span.ToArray();
    }

    public Memory<byte> DecodeByteArrayMemory(RlpLimit? limit = null, int size = -1)
    {
        if (!_isMemoryBacked)
        {
            return DecodeByteArray(limit, size);
        }

        int position = Position;
        int prefix = ReadByte();
        if (prefix < Rlp.EmptyByteArrayByte)
        {
            GuardSize(actual: 1, expected: size);
            return _memory.Slice(position, 1);
        }

        if (prefix is Rlp.EmptyByteArrayByte)
        {
            GuardSize(actual: 0, expected: size);
            return Memory<byte>.Empty;
        }

        if (prefix <= 183)
        {
            int length = prefix - 128;
            GuardLimit(length, limit);
            GuardSize(actual: length, expected: size);

            Memory<byte> buffer = ReadMemory(length);

            if (length == 1 && buffer.Span[0] < 128)
            {
                RlpHelpers.ThrowNonCanonicalInteger(position);
            }

            return buffer;
        }

        return DecodeLargerByteArrayMemory(prefix, limit, size);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> DecodeLargerByteArrayMemory(int prefix, RlpLimit? limit = null, int size = -1)
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

            GuardSize(actual: length, expected: size);
            GuardLimit(length, limit);
            return ReadMemory(length);
        }

        RlpHelpers.ThrowUnexpectedPrefix(prefix);
        return default;
    }

    public ReadOnlySpan<byte> DecodeByteArraySpan(RlpLimit? limit = null, int size = -1)
    {
        int position = Position;
        int prefix = ReadByte();
        ReadOnlySpan<byte> span = RlpHelpers.SingleBytes;
        if ((uint)prefix < (uint)span.Length)
        {
            GuardSize(actual: 1, expected: size);
            return span.Slice(prefix, 1);
        }

        if (prefix is Rlp.EmptyByteArrayByte)
        {
            GuardSize(actual: 0, expected: size);
            return default;
        }

        if (prefix <= 183)
        {
            int length = prefix - 128;
            GuardLimit(length, limit);
            GuardSize(actual: length, expected: size);

            ReadOnlySpan<byte> buffer = Read(length);

            if (length == 1 && buffer[0] < 128)
            {
                RlpHelpers.ThrowNonCanonicalInteger(position);
            }

            return buffer;
        }

        return DecodeLargerByteArraySpan(prefix, limit, size);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ReadOnlySpan<byte> DecodeLargerByteArraySpan(int prefix, RlpLimit? limit = null, int size = -1)
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

            GuardSize(actual: length, expected: size);
            GuardLimit(length, limit);
            return Read(length);
        }

        RlpHelpers.ThrowUnexpectedPrefix(prefix);
        return default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem() => Position += PeekNextRlpLength();

    public void Reset() => Position = 0;

    public bool DecodeBool()
    {
        byte prefix = ReadByte();
        switch (prefix)
        {
            case 1:
                return true;
            case 128:
                return false;
            default:
                RlpHelpers.ThrowUnexpectedBoolValue(prefix);
                return false;
        }
    }

    public readonly byte PeekByte() => Data[Position];

    private readonly byte PeekByte(int offset) => Data[Position + offset];

    public void SkipBytes(int length) => Position += length;

    public string DecodeString(RlpLimit? limit = null)
    {
        ReadOnlySpan<byte> bytes = DecodeByteArraySpan(limit);
        return Encoding.UTF8.GetString(bytes);
    }

    public long DecodeLong() => (long)DecodeULong();

    public int DecodeInt() => (int)DecodeUInt();

    public int DecodePositiveInt()
    {
        int position = Position;
        int value = DecodeInt();
        if (value < 0)
            RlpHelpers.ThrowNegativeInteger(position, value);
        return value;
    }

    public long DecodePositiveLong()
    {
        int position = Position;
        long value = DecodeLong();
        if (value < 0)
            RlpHelpers.ThrowNegativeInteger(position, value);
        return value;
    }

    public ulong DecodeULong()
    {
        int position = Position;
        int prefix = ReadByte();

        switch (prefix)
        {
            case 0:
                return RlpHelpers.ThrowNonCanonicalInteger(position);
            case < 128:
                return (ulong)prefix;
            case 128:
                return 0;
        }

        int length = prefix - 128;
        if (length > 8)
        {
            RlpHelpers.ThrowUnexpectedIntegerLength(position, length);
        }

        ulong result = 0ul;
        for (int i = 8; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= PeekByte(length - i);
                if (result == 0)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }
            }
        }

        if (result < 128)
        {
            RlpHelpers.ThrowNonCanonicalInteger(position);
        }

        SkipBytes(length);

        return result;
    }

    public byte[][] DecodeByteArrays(RlpLimit? limit = null, int innerSize = -1)
    {
        int length = ReadSequenceLength();
        if (length is 0)
        {
            return [];
        }

        int checkPosition = Position + length;
        int itemsCountMax = (limit ?? RlpLimit.DefaultLimit).Limit + 1;
        int itemsCount = PeekNumberOfItemsRemaining(checkPosition, itemsCountMax);
        GuardLimit(itemsCount, limit);
        byte[][] result = new byte[itemsCount][];

        for (int i = 0; i < itemsCount; i++)
        {
            result[i] = DecodeByteArray(size: innerSize);
        }

        Check(checkPosition);

        return result;
    }

    public ushort DecodeUShort()
    {
        int position = Position;
        int prefix = ReadByte();

        switch (prefix)
        {
            case 0:
                RlpHelpers.ThrowNonCanonicalInteger(position);
                return 0;
            case < 128:
                return (ushort)prefix;
            case 128:
                return 0;
        }

        int length = prefix - 128;
        if (length > 2)
        {
            RlpHelpers.ThrowUnexpectedIntegerLength(position, length);
        }

        ushort result = 0;
        for (int i = 2; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= PeekByte(length - i);
                if (result == 0)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }
            }
        }

        if (result < 128)
        {
            RlpHelpers.ThrowNonCanonicalInteger(position);
        }

        SkipBytes(length);

        return result;
    }

    public byte DecodeByte()
    {
        int position = Position;
        byte byteValue = PeekByte();
        switch (byteValue)
        {
            case 0:
                RlpHelpers.ThrowNonCanonicalInteger(position);
                return 0;
            case < 128:
                SkipBytes(1);
                return byteValue;
            case 128:
                SkipBytes(1);
                return 0;
            case 129 when PeekByte(1) < 128:
                RlpHelpers.ThrowNonCanonicalInteger(position);
                return 0;
            case 129:
                SkipBytes(1);
                return ReadByte();
            default:
                RlpHelpers.ThrowUnexpectedByteValue(position, byteValue);
                return 0;
        }
    }

    /// <summary>
    /// Decodes an RLP sequence into a <typeparamref name="T"/>[], substituting <paramref name="defaultElement"/>
    /// for any element encoded as an empty list (<c>0xc0</c>) instead of invoking <paramref name="decoder"/>.
    /// </summary>
    /// <remarks>
    /// The empty-list-to-default substitution is only safe for reference types, hence the <c>class?</c> constraint.
    /// For a reference type, <c>default(T)</c> is <c>null</c>, which a caller can detect and reject. For a value
    /// type, <c>default(T)</c> is an ordinary zero value indistinguishable from legitimately-decoded data, so a
    /// malformed <c>0xc0</c> element would be silently accepted as zero rather than throwing — a real
    /// consensus-relevant decoding bug (see the EIP-7928 BAL decoder). Value-type arrays must therefore use
    /// <see cref="RlpDecoder{T}.DecodeArray"/>, which decodes every element and rejects <c>0xc0</c>.
    /// </remarks>
    public T[] DecodeArray<T>(IRlpDecoder<T>? decoder = null, bool checkPositions = true, bool allowNulls = false, T defaultElement = default, RlpLimit? limit = null)
        where T : class?
    {
        decoder ??= Rlp.GetDecoder<T>()
            ?? throw new RlpException($"{nameof(Rlp)} does not support length of {nameof(T)}");

        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
        GuardLimit(count, limit);
        T[] result = new T[count];
        for (int i = 0; i < result.Length; i++)
        {
            if (PeekByte() == Rlp.OfEmptyList[0])
            {
                if (!allowNulls)
                    RlpHelpers.ThrowNullArrayElement(i);

                result[i] = defaultElement;
                Position++;
            }
            else
            {
                result[i] = decoder.Decode(ref this);

                if (!allowNulls && result[i] is null)
                    RlpHelpers.ThrowNullArrayElement(i);
            }
        }

        if (checkPositions)
        {
            Check(positionCheck);
        }

        return result;
    }

    public T[] DecodeArray<T>(DecodeRlpValue<T> decodeItem, bool checkPositions = true, T defaultElement = default, RlpLimit? limit = null)
    {
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
        GuardLimit(count, limit);
        T[] result = new T[count];
        for (int i = 0; i < result.Length; i++)
        {
            if (PeekByte() == Rlp.OfEmptyList[0])
            {
                result[i] = defaultElement;
                Position++;
            }
            else
            {
                result[i] = decodeItem(ref this);
            }
        }

        if (checkPositions)
        {
            Check(positionCheck);
        }

        return result;
    }

    public ArrayPoolList<T> DecodeArrayPoolList<T>(DecodeRlpValue<T> decodeItem, bool checkPositions = true, T defaultElement = default, RlpLimit? limit = null)
    {
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
        GuardLimit(count, limit);
        ArrayPoolList<T> result = new(count, count);
        int i = 0;
        try
        {
            for (; i < result.Count; i++)
            {
                if (PeekByte() == Rlp.OfEmptyList[0])
                {
                    result[i] = defaultElement;
                    Position++;
                }
                else
                {
                    result[i] = decodeItem(ref this);
                }
            }

            if (checkPositions)
            {
                Check(positionCheck);
            }

            return result;
        }
        catch (RlpException)
        {
            Rlp.DisposeDecodedItemsAndList(result, i);
            throw;
        }
        catch (Exception e)
        {
            Rlp.DisposeDecodedItemsAndList(result, i);
            throw new RlpException($"Error decoding array of {typeof(T).Name}.", e);
        }
    }

    public readonly bool IsNextItemEmptyByteArray() => PeekByte() is Rlp.EmptyByteArrayByte;

    public readonly bool IsNextItemEmptyList() => PeekByte() is Rlp.EmptyListByte;

    [DoesNotReturn, StackTraceHidden]
    private readonly void ThrowKeccakDecodeException(int prefix)
        => throw new DecodeKeccakRlpException(prefix, Position, Data.Length);

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void GuardLimit(int count, RlpLimit? limit = null) =>
        Rlp.GuardLimit(count, Length - Position, limit);

    // ReSharper disable once MemberHidesStaticFromOuterClass
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GuardSize(int actual, int expected) =>
        Rlp.GuardSize(actual, expected);
}
