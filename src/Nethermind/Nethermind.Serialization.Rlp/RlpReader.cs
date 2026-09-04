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
    private const int AddressRlpPrefix = Rlp.EmptyByteArrayByte + Address.Size;

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

    public void SkipLength() => Position = RlpHelpers.SkipLength(Data, Position);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int PeekPrefixLength() => RlpHelpers.GetPrefixLength(Data[Position]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekNextRlpLength() => RlpHelpers.PeekNextRlpLength(Data, Position);

    public readonly ReadOnlySpan<byte> Peek(int length) => Peek(0, length);

    public readonly ReadOnlySpan<byte> Peek(int offset, int length) => Data.Slice(Position + offset, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int PrefixLength, int ContentLength) ReadPrefixAndContentLength()
    {
        Position = RlpHelpers.ReadPrefixAndContentLength(Data, Position, out int prefixLength, out int contentLength);
        return (prefixLength, contentLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
        => RlpHelpers.PeekPrefixAndContentLength(Data, Position);

    public int ReadSequenceLength()
    {
        Position = RlpHelpers.ReadSequenceLength(Data, Position, out int contentLength);
        return contentLength;
    }

    private int DeserializeLength(int lengthOfLength)
    {
        Position = RlpHelpers.DeserializeLength(Data, Position, lengthOfLength, out int length);
        return length;
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

    public Hash256 DecodeKeccak()
    {
        Position = RlpHelpers.DecodeKeccak(Data, Position, out Hash256 keccak);
        return keccak;
    }

    public Hash256? DecodeKeccakOrNull()
    {
        Position = RlpHelpers.DecodeKeccakOrNull(Data, Position, out Hash256? keccak);
        return keccak;
    }

    public ValueHash256? DecodeValueKeccak()
    {
        Position = RlpHelpers.DecodeValueKeccakOrNull(Data, Position, out ValueHash256? keccak);
        return keccak;
    }

    public ValueHash256 DecodeValueKeccakNonNull()
    {
        Position = RlpHelpers.DecodeValueKeccakNonNull(Data, Position, out ValueHash256 keccak);
        return keccak;
    }

    public bool TryDecodeValueKeccak(out ValueHash256 keccak)
    {
        Position = RlpHelpers.TryDecodeValueKeccak(Data, Position, out keccak, out bool hasValue);
        return hasValue;
    }

    public Hash256? DecodeZeroPrefixKeccak()
    {
        int prefix = PeekByte();
        if (prefix == Rlp.EmptyByteArrayByte)
        {
            ReadByte();
            return null;
        }

        ReadOnlySpan<byte> theSpan = DecodeByteArraySpan(RlpLimit.L32);
        Span<byte> keccakBytes = stackalloc byte[Hash256.Size];
        keccakBytes.Clear();
        theSpan.CopyTo(keccakBytes[(Hash256.Size - theSpan.Length)..]);
        return new Hash256(keccakBytes);
    }

    public Hash256 DecodeZeroPrefixKeccakNonNull() => DecodeZeroPrefixKeccak() ?? ThrowNullDecodedValue<Hash256>();

    public void DecodeKeccakStructRef(out Hash256StructRef keccak)
    {
        if (!ReadKeccakPrefix(allowNull: true))
        {
            keccak = new Hash256StructRef(Keccak.Zero.Bytes);
        }
        else
        {
            ReadOnlySpan<byte> keccakSpan = Read(Hash256.Size);
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
        if (prefix == Rlp.EmptyByteArrayByte)
        {
            ReadByte();
            keccak = new Hash256StructRef(Keccak.Zero.Bytes);
        }
        else if (prefix > RlpHelpers.KeccakRlpPrefix)
        {
            ReadByte();
            ThrowKeccakDecodeException(prefix);
            keccak = default;
        }
        else if (prefix == RlpHelpers.KeccakRlpPrefix)
        {
            ReadByte();
            ReadOnlySpan<byte> keccakSpan = Read(Hash256.Size);
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
            if (theSpan.Length < Hash256.Size)
            {
                buffer[..(Hash256.Size - theSpan.Length)].Clear();
            }
            theSpan.CopyTo(buffer[(Hash256.Size - theSpan.Length)..]);
            keccak = new Hash256StructRef(buffer);
        }
    }

    public Address DecodeAddress()
    {
        Position = RlpHelpers.DecodeAddress(Data, Position, allowNull: false, out Address? address);
        return address!;
    }

    public Address? DecodeAddressOrNull()
    {
        Position = RlpHelpers.DecodeAddress(Data, Position, allowNull: true, out Address? address);
        return address;
    }

    public void DecodeAddressStructRef(out AddressStructRef address)
    {
        if (!ReadAddressPrefix(allowNull: true))
        {
            address = new AddressStructRef(Address.Zero.Bytes);
            return;
        }

        address = new AddressStructRef(Read(Address.Size));
    }

    public void DecodeAddressStructRefNonNull(out AddressStructRef address)
    {
        int prefix = ReadByte();
        if (prefix == Rlp.EmptyByteArrayByte)
        {
            ThrowNullDecodedValue<Address>();
        }
        else if (prefix != Rlp.EmptyByteArrayByte + Address.Size)
        {
            RlpHelpers.ThrowUnexpectedPrefix(prefix);
        }

        address = new AddressStructRef(Read(Address.Size));
    }

    public UInt256 DecodeUInt256(int length = -1)
    {
        Position = RlpHelpers.DecodeUInt256(Data, Position, length, out UInt256 value);
        return value;
    }

    public EvmWord DecodeEvmWord()
    {
        Position = RlpHelpers.DecodeEvmWord(Data, Position, out EvmWord value);
        return value;
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

    public Bloom DecodeBloom()
    {
        ReadOnlySpan<byte> bloomBytes = DecodeByteArraySpan(RlpLimit.Bloom, Bloom.ByteLength);
        return CreateBloom(bloomBytes);
    }

    public Bloom? DecodeBloomOrNull()
    {
        Position = RlpHelpers.DecodeBloomSpan(Data, Position, out ReadOnlySpan<byte> bloomBytes);
        return bloomBytes.Length == 0 ? null : CreateBloom(bloomBytes);
    }

    public Bloom DecodeBloomNonNull() =>
        DecodeBloomOrNull() ?? ThrowNullDecodedValue<Bloom>();

    private static Bloom CreateBloom(ReadOnlySpan<byte> bloomBytes)
    {
        if (bloomBytes.Length != Bloom.ByteLength)
        {
            throw new RlpException("Incorrect bloom RLP");
        }

        return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes);
    }

    public void DecodeBloomStructRef(out BloomStructRef bloom) =>
        DecodeBloomStructRef(out bloom, out _);

    internal void DecodeBloomStructRef(out BloomStructRef bloom, out bool wasMissing)
    {
        wasMissing = false;
        Position = RlpHelpers.DecodeBloomSpan(Data, Position, out ReadOnlySpan<byte> bloomBytes);
        if (bloomBytes.Length == 0)
        {
            wasMissing = true;
            bloom = new BloomStructRef(Bloom.Empty.Bytes);
            return;
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
        Position = RlpHelpers.DecodeUInt(Data, Position, out uint value);
        return value;
    }

    public byte[] DecodeByteArray(RlpLimit? limit = null, int size = -1)
    {
        Position = RlpHelpers.DecodeByteArray(Data, Position, limit, size, out byte[] value);
        return value;
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
        Position = RlpHelpers.DecodeByteArraySpan(Data, Position, limit, size, out ReadOnlySpan<byte> value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipItem() => Position += PeekNextRlpLength();

    /// <summary>Advances the cursor past <paramref name="count"/> whole items.</summary>
    /// <remarks>Keeps the cursor in a local for the walk, so it is read and written once instead of once
    /// per item. The cursor is a 4-byte field access, which the zkVM charges about eight times an aligned
    /// 8-byte read and eleven times an 8-byte write, so a non-positive <paramref name="count"/> returns
    /// without touching it. A malformed item throws with the cursor still on the first item of the run.</remarks>
    public void SkipItems(int count)
    {
        if (count <= 0)
        {
            return;
        }

        Position = RlpHelpers.SkipItems(Data, Position, count);
    }

    public void Reset() => Position = 0;

    public bool DecodeBool()
    {
        Position = RlpHelpers.DecodeBool(Data, Position, out bool value);
        return value;
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
        Position = RlpHelpers.DecodePositiveInt(Data, Position, out int value);
        return value;
    }

    public long DecodePositiveLong()
    {
        Position = RlpHelpers.DecodePositiveLong(Data, Position, out long value);
        return value;
    }

    public ulong DecodeULong()
    {
        Position = RlpHelpers.DecodeULong(Data, Position, out ulong value);
        return value;
    }

    public byte[][] DecodeByteArrays(RlpLimit? limit = null, int innerSize = -1)
    {
        ReadOnlySpan<byte> data = Data;
        int position = RlpHelpers.ReadSequenceLength(data, Position, out int length);
        Position = position;
        if (length is 0)
        {
            return [];
        }

        int checkPosition = position + length;
        int itemsCountMax = (limit ?? RlpLimit.DefaultLimit).Limit + 1;
        int itemsCount = RlpHelpers.CountItems(data, position, checkPosition, itemsCountMax);
        Rlp.GuardLimit(itemsCount, data.Length - position, limit);
        byte[][] result = new byte[itemsCount][];

        for (int i = 0; i < itemsCount; i++)
        {
            position = RlpHelpers.DecodeByteArray(data, position, null, innerSize, out result[i]);
        }

        Position = position;
        Check(checkPosition);

        return result;
    }

    public ushort DecodeUShort()
    {
        Position = RlpHelpers.DecodeUShort(Data, Position, out ushort value);
        return value;
    }

    public byte DecodeByte()
    {
        Position = RlpHelpers.DecodeByte(Data, Position, out byte value);
        return value;
    }

    /// <summary>
    /// Decodes an RLP sequence using the legacy array API. New code should use
    /// <see cref="DecodeNonNullArray{T}"/> or <see cref="DecodeNullableArray{T}(IRlpDecoder{T}?, bool, T?, RlpLimit?)"/>
    /// to make element nullability explicit.
    /// </summary>
    public T?[] DecodeArray<T>(
        IRlpDecoder<T>? decoder = null,
        bool checkPositions = true,
        bool allowNulls = false,
        T? defaultElement = default,
        RlpLimit? limit = null)
        where T : class
        => allowNulls
            ? DecodeNullableArrayCore(decoder, checkPositions, defaultElement, limit)
            : DecodeNonNullArray(decoder, checkPositions, limit);

    /// <summary>Decodes a sequence of reference-type values and rejects null elements.</summary>
    /// <exception cref="RlpException">An element is null.</exception>
    public T[] DecodeNonNullArray<T>(IRlpDecoder<T>? decoder = null, bool checkPositions = true, RlpLimit? limit = null)
        where T : class
    {
        decoder ??= Rlp.GetDecoder<T>()
            ?? throw new RlpException($"{nameof(Rlp)} does not support length of {nameof(T)}");

        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        GuardLimit(count, limit);
        T[] result = new T[count];
        for (int i = 0; i < result.Length; i++)
        {
            if (PeekByte() == Rlp.EmptyListByte)
            {
                RlpHelpers.ThrowNullArrayElement(i);
            }

            result[i] = decoder.DecodeGuardNotNull(ref this);
        }

        if (checkPositions)
        {
            Check(positionCheck);
        }

        return result;
    }

    public T?[] DecodeNullableArray<T>(IRlpDecoder<T>? decoder = null, bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null)
        where T : class
        => DecodeNullableArrayCore(decoder, checkPositions, defaultElement, limit);

    private T?[] DecodeNullableArrayCore<T>(IRlpDecoder<T>? decoder, bool checkPositions, T? defaultElement, RlpLimit? limit)
        where T : class
    {
        decoder ??= Rlp.GetDecoder<T>()
            ?? throw new RlpException($"{nameof(Rlp)} does not support length of {nameof(T)}");

        ReadOnlySpan<byte> data = Data;
        int position = RlpHelpers.ReadSequenceLength(data, Position, out int sequenceLength);
        int positionCheck = position + sequenceLength;
        int count = RlpHelpers.CountItems(
            data, position, checkPositions ? positionCheck : data.Length, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        Rlp.GuardLimit(count, data.Length - position, limit);
        T?[] result = new T?[count];

        // The element decoder takes the reader by reference, so the cursor has to be in the field
        // across that call. Everything around it - the header, the null probe - stays in a local.
        for (int i = 0; i < result.Length; i++)
        {
            if (data[position] == Rlp.EmptyListByte)
            {
                result[i] = defaultElement;
                position++;
            }
            else
            {
                Position = position;
                result[i] = decoder.Decode(ref this);
                position = Position;
            }
        }

        Position = position;

        if (checkPositions)
        {
            Check(positionCheck);
        }

        return result;
    }

    /// <summary>Decodes a sequence whose element decoder threads the cursor by value.</summary>
    /// <remarks>
    /// Unlike the <see cref="IRlpDecoder{T}"/> and <see cref="DecodeRlpValue{T}"/> overloads, the whole
    /// walk runs on a local: <typeparamref name="TDecoder"/> is a constrained call, so no element
    /// boundary pushes the cursor back through <see cref="Position"/>.
    /// </remarks>
    public T?[] DecodeArray<T, TDecoder>(bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null)
        where TDecoder : ICursorRlpDecoder<T>
    {
        ReadOnlySpan<byte> data = Data;
        int position = RlpHelpers.ReadSequenceLength(data, Position, out int sequenceLength);
        int positionCheck = position + sequenceLength;
        int count = RlpHelpers.CountItems(
            data, position, checkPositions ? positionCheck : data.Length, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        Rlp.GuardLimit(count, data.Length - position, limit);
        T?[] result = new T?[count];

        for (int i = 0; i < result.Length; i++)
        {
            if (data[position] == Rlp.EmptyListByte)
            {
                result[i] = defaultElement;
                position++;
            }
            else
            {
                position = TDecoder.DecodeItem(data, position, out result[i]);
            }
        }

        Position = position;
        if (checkPositions)
        {
            Check(positionCheck);
        }

        return result;
    }

    public T?[] DecodeArray<T>(DecodeRlpValue<T?> decodeItem, bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null)
    {
        ReadOnlySpan<byte> data = Data;
        int position = RlpHelpers.ReadSequenceLength(data, Position, out int sequenceLength);
        int positionCheck = position + sequenceLength;
        int count = RlpHelpers.CountItems(
            data, position, checkPositions ? positionCheck : data.Length, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        Rlp.GuardLimit(count, data.Length - position, limit);
        T?[] result = new T?[count];

        // The element decoder takes the reader by reference, so the cursor has to be in the field
        // across that call. Everything around it - the header, the null probe - stays in a local.
        for (int i = 0; i < result.Length; i++)
        {
            if (data[position] == Rlp.EmptyListByte)
            {
                result[i] = defaultElement;
                position++;
            }
            else
            {
                Position = position;
                result[i] = decodeItem(ref this);
                position = Position;
            }
        }

        Position = position;

        if (checkPositions)
        {
            Check(positionCheck);
        }

        return result;
    }

    /// <summary>Decodes a sequence with the supplied decoder and rejects null elements.</summary>
    /// <exception cref="RlpException">An element is null and no reference-type default element was supplied.</exception>
    public T[] DecodeNonNullArray<T>(DecodeRlpValue<T> decodeItem, bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null)
    {
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        GuardLimit(count, limit);
        T[] result = new T[count];
        bool hasDefaultElement = defaultElement is not null && !typeof(T).IsValueType;
        for (int i = 0; i < result.Length; i++)
        {
            if (PeekByte() == Rlp.EmptyListByte)
            {
                if (!hasDefaultElement)
                {
                    RlpHelpers.ThrowNullArrayElement(i);
                }

                result[i] = defaultElement!;
                Position++;
            }
            else
            {
                T? value = decodeItem(ref this);
                if (value is null)
                {
                    RlpHelpers.ThrowNullArrayElement(i);
                }

                result[i] = value!;
            }
        }

        if (checkPositions)
        {
            Check(positionCheck);
        }

        return result;
    }

    public T?[] DecodeNullableArray<T>(DecodeRlpValue<T?> decodeItem, bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null)
        where T : class
    {
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(
            checkPositions ? positionCheck : null,
            (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        GuardLimit(count, limit);
        T?[] result = new T?[count];
        for (int i = 0; i < result.Length; i++)
        {
            if (PeekByte() == Rlp.EmptyListByte)
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

    /// <summary>Decodes a pooled sequence while preserving null elements for compatibility.</summary>
    /// <returns>A pooled list owned by the caller and requiring disposal.</returns>
    public ArrayPoolList<T?> DecodeArrayPoolList<T>(DecodeRlpValue<T?> decodeItem, bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null)
    {
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        GuardLimit(count, limit);
        ArrayPoolList<T?> result = new(count, count);
        int i = 0;
        try
        {
            for (; i < result.Count; i++)
            {
                if (PeekByte() == Rlp.EmptyListByte)
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

    /// <summary>Decodes a pooled sequence and rejects null elements.</summary>
    /// <param name="decodeEmptyList">When true, passes an RLP empty list to <paramref name="decodeItem"/> instead of treating it as a null element.</param>
    /// <returns>A pooled list owned by the caller and requiring disposal.</returns>
    /// <exception cref="RlpException">An element is null and no reference-type default element was supplied.</exception>
    public ArrayPoolList<T> DecodeNonNullArrayPoolList<T>(DecodeRlpValue<T> decodeItem, bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null, bool decodeEmptyList = false)
    {
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        GuardLimit(count, limit);
        ArrayPoolList<T> result = new(count, count);
        int i = 0;
        bool hasDefaultElement = defaultElement is not null && !typeof(T).IsValueType;
        try
        {
            for (; i < result.Count; i++)
            {
                if (!decodeEmptyList && PeekByte() == Rlp.EmptyListByte)
                {
                    if (!hasDefaultElement)
                    {
                        RlpHelpers.ThrowNullArrayElement(i);
                    }

                    result[i] = defaultElement!;
                    Position++;
                }
                else
                {
                    T? value = decodeItem(ref this);
                    if (value is null)
                    {
                        RlpHelpers.ThrowNullArrayElement(i);
                    }

                    result[i] = value!;
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

    public ArrayPoolList<T?> DecodeNullableArrayPoolList<T>(DecodeRlpValue<T?> decodeItem, bool checkPositions = true, T? defaultElement = default, RlpLimit? limit = null)
        where T : class
    {
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(
            checkPositions ? positionCheck : null,
            (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        GuardLimit(count, limit);
        ArrayPoolList<T?> result = new(count, count);
        int i = 0;
        try
        {
            for (; i < result.Count; i++)
            {
                if (PeekByte() == Rlp.EmptyListByte)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadFixedSizePrefix(int expectedPrefix, bool allowNull, out bool hasValue, out int prefix)
    {
        prefix = ReadByte();
        hasValue = prefix == expectedPrefix;
        return hasValue || allowNull && prefix == Rlp.EmptyByteArrayByte;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ReadKeccakPrefix(bool allowNull)
    {
        if (!TryReadFixedSizePrefix(RlpHelpers.KeccakRlpPrefix, allowNull, out bool hasValue, out int prefix))
        {
            ThrowKeccakDecodeException(prefix);
        }

        return hasValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ReadAddressPrefix(bool allowNull)
    {
        if (!TryReadFixedSizePrefix(AddressRlpPrefix, allowNull, out bool hasValue, out int prefix))
        {
            ThrowAddressDecodeException(prefix);
        }

        return hasValue;
    }

    [DoesNotReturn, StackTraceHidden]
    private static T ThrowNullDecodedValue<T>() => RlpHelpers.ThrowNullDecodedValue<T>();

    [DoesNotReturn, StackTraceHidden]
    private readonly void ThrowKeccakDecodeException(int prefix)
        => RlpHelpers.ThrowKeccakDecode(prefix, Position, Data.Length);

    [DoesNotReturn, StackTraceHidden]
    private readonly void ThrowAddressDecodeException(int prefix)
        => RlpHelpers.ThrowAddressDecode(prefix, Position, Data.Length);

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
