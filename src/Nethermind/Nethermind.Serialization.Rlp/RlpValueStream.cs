// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public ref struct RlpValueStream : IRlpStream
{
    public RlpValueStream(scoped in ReadOnlySpan<byte> data)
    {
        Data = data;
        Position = 0;
    }

    public RlpValueStream(Memory<byte> memory, bool sliceMemory = false)
    {
        Memory = memory;
        Data = memory.Span;
        Position = 0;

        // Slice memory is turned off by default. Because if you are not careful and being explicit about it,
        // you can end up with a memory leak.
        _sliceMemory = sliceMemory;
    }

    public Memory<byte>? Memory { get; }

    private readonly bool _sliceMemory = false;

    public ReadOnlySpan<byte> Data { get; }

    public readonly bool IsEmpty => Data.IsEmpty;

    public int Position { get; set; }

    public readonly int Length => Data.Length;

    public readonly bool ShouldSliceMemory => _sliceMemory;

    public readonly bool IsSequenceNext() => Data[Position] >= 192;

    public int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
    {
        int positionStored = Position;
        int numberOfItems = 0;
        while (Position < (beforePosition ?? Data.Length))
        {
            int prefix = ReadByte();
            switch (prefix)
            {
                case <= 128:
                    break;
                case <= 183:
                    {
                        int length = prefix - 128;
                        Position += length;
                        break;
                    }
                case < 192:
                    {
                        int lengthOfLength = prefix - 183;
                        int length = DeserializeLength(lengthOfLength);
                        if (length < 56)
                        {
                            throw new RlpException("Expected length greater or equal 56 and was {length}");
                        }

                        Position += length;
                        break;
                    }
                default:
                    {
                        Position--;
                        int sequenceLength = ReadSequenceLength();
                        Position += sequenceLength;
                        break;
                    }
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
        Position += PeekPrefixAndContentLength().PrefixLength;
    }

    public int PeekNextRlpLength()
    {
        (int a, int b) = PeekPrefixAndContentLength();
        return a + b;
    }

    public ReadOnlySpan<byte> Peek(int length)
    {
        ReadOnlySpan<byte> item = Read(length);
        Position -= item.Length;
        return item;
    }

    public (int PrefixLength, int ContentLength) ReadPrefixAndContentLength()
    {
        int prefix = ReadByte();
        switch (prefix)
        {
            case <= 128:
                return (0, 1);
            case <= 183:
                return (1, prefix - 128);
            case < 192:
                {
                    int lengthOfLength = prefix - 183;
                    if (lengthOfLength > 4)
                    {
                        // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                        throw new RlpException("Expected length of length less or equal 4");
                    }

                    int length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56 and was {length}");
                    }

                    return (lengthOfLength + 1, length);
                }
            case <= 247:
                return (1, prefix - 192);
            default:
                {
                    int lengthOfContentLength = prefix - 247;
                    int contentLength = DeserializeLength(lengthOfContentLength);
                    if (contentLength < 56)
                    {
                        throw new RlpException($"Expected length greater or equal 56 and got {contentLength}");
                    }


                    return (lengthOfContentLength + 1, contentLength);
                }
        }
    }


    public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
    {
        int memorizedPosition = Position;
        (int PrefixLength, int ContentLength) result = ReadPrefixAndContentLength();

        Position = memorizedPosition;
        return result;
    }

    public int ReadSequenceLength()
    {
        int prefix = ReadByte();
        if (prefix < 192)
        {
            throw new RlpException($"Expected a sequence prefix to be in the range of <192, 255> and got {prefix} at position {Position} in the message of length {Data.Length} starting with {Data[..Math.Min(Rlp.DebugMessageContentLength, Data.Length)].ToHexString()}");
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
        if (Data[Position] == 0)
        {
            throw new RlpException("Length starts with 0");
        }

        int result = lengthOfLength switch
        {
            1 => Data[Position],
            2 => Data[Position + 1] | (Data[Position] << 8),
            3 => Data[Position + 2] | (Data[Position + 1] << 8) | (Data[Position] << 16),
            4 => Data[Position + 3] | (Data[Position + 2] << 8) | (Data[Position + 1] << 16) | (Data[Position] << 24),
            _ => throw new InvalidOperationException($"Invalid length of length = {lengthOfLength}")
        };

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
        if (_sliceMemory && Memory.HasValue) return ReadSlicedMemory(length);
        return Read(length).ToArray();
    }

    private Memory<byte> ReadSlicedMemory(int length)
    {
        Memory<byte> data = Memory!.Value.Slice(Position, length);
        Position += length;
        return data;
    }

    public readonly void Check(int nextCheck)
    {
        if (Position != nextCheck)
        {
            throw new RlpException($"Data checkpoint failed. Expected {nextCheck} and is {Position}");
        }
    }

    // This class was introduce to reduce allocations when deserializing receipts. In order to deserialize receipts we first try to deserialize it in new format and then in old format.
    // If someone didn't do migration this will result in excessive allocations and GC of the not needed strings.
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
            throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
        }

        ReadOnlySpan<byte> keccakSpan = Read(32);
        return keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes) ? Keccak.OfAnEmptyString
            : keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes) ? Keccak.EmptyTreeHash
            : new Hash256(keccakSpan);
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
            throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
        }

        ReadOnlySpan<byte> keccakSpan = Read(32);
        return keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes) ? Keccak.OfAnEmptyString.ValueHash256
            : keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes) ? Keccak.EmptyTreeHash.ValueHash256
            : new ValueHash256(keccakSpan);
    }

    public Hash256? DecodeZeroPrefixKeccak()
    {
        int prefix = PeekByte();
        if (prefix == 128)
        {
            ReadByte();
            return null;
        }

        ReadOnlySpan<byte> theSpan = DecodeByteArraySpan();
        byte[] keccakByte = new byte[32];
        theSpan.CopyTo(keccakByte.AsSpan(32 - theSpan.Length));
        return new Hash256(keccakByte);
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
            throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
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
            throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
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
            ReadOnlySpan<byte> theSpan = DecodeByteArraySpan();
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
            ThrowInvalidPrefix(ref this, prefix);
        }

        byte[] buffer = Read(20).ToArray();
        return new Address(buffer);

        static void ThrowInvalidPrefix(ref RlpValueStream ctx, int prefix)
        {
            throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {ctx.Position} in the message of length {ctx.Data.Length} starting with {ctx.Data[..Math.Min(Rlp.DebugMessageContentLength, ctx.Data.Length)].ToHexString()}");
        }
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
            ThrowInvalidPrefix(ref this, prefix);
        }

        address = new AddressStructRef(Read(20));
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowInvalidPrefix(ref RlpValueStream ctx, int prefix)
    {
        throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {ctx.Position} in the message of length {ctx.Data.Length} starting with {ctx.Data[..Math.Min(Rlp.DebugMessageContentLength, ctx.Data.Length)].ToHexString()}");
    }

    public UInt256 DecodeUInt256(int length = -1)
    {
        ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan();
        if (byteSpan.Length > 32)
        {
            ThrowDataTooLong();
        }

        if (length == -1)
        {
            if (byteSpan.Length > 1 && byteSpan[0] == 0)
            {
                ThrowNonCanonicalUInt256(Position);
            }
        }
        else if (byteSpan.Length != length)
        {
            ThrowInvalidLength(Position);
        }


        return new UInt256(byteSpan, true);

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowDataTooLong() => throw new RlpException("UInt256 cannot be longer than 32 bytes");

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNonCanonicalUInt256(int position) => throw new RlpException($"Non-canonical UInt256 (leading zero bytes) at position {position}");

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInvalidLength(int position) => throw new RlpException($"Invalid length at position {position}");
    }

    public BigInteger DecodeUBigInt()
    {
        ReadOnlySpan<byte> bytes = DecodeByteArraySpan();
        if (bytes.Length > 1 && bytes[0] == 0)
        {
            throw new RlpException($"Non-canonical UBigInt (leading zero bytes) at position {Position}");
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
            bloomBytes = Read(256);
        }
        else
        {
            bloomBytes = DecodeByteArraySpan();
            if (bloomBytes.Length == 0)
            {
                return null;
            }
        }

        if (bloomBytes.Length != 256)
        {
            throw new InvalidOperationException("Incorrect bloom RLP");
        }

        return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes.ToArray());
    }

    public void DecodeBloomStructRef(out BloomStructRef bloom)
    {
        ReadOnlySpan<byte> bloomBytes;

        // tks: not sure why but some nodes send us Blooms in a sequence form
        // https://github.com/NethermindEth/nethermind/issues/113
        if (Data[Position] == 249)
        {
            Position += 5; // tks: skip 249 1 2 129 127 and read 256 bytes
            bloomBytes = Read(256);
        }
        else
        {
            bloomBytes = DecodeByteArraySpan();
            if (bloomBytes.Length == 0)
            {
                bloom = new BloomStructRef(Bloom.Empty.Bytes);
                return;
            }
        }

        if (bloomBytes.Length != 256)
        {
            throw new InvalidOperationException("Incorrect bloom RLP");
        }

        bloom = bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? new BloomStructRef(Bloom.Empty.Bytes) : new BloomStructRef(bloomBytes);
    }

    public ReadOnlySpan<byte> PeekNextItem()
    {
        int length = PeekNextRlpLength();
        ReadOnlySpan<byte> item = Read(length);
        Position -= item.Length;
        return item;
    }

    public readonly bool IsNextItemNull()
    {
        return Data[Position] == 192;
    }

    public int DecodeInt()
    {
        int prefix = ReadByte();

        switch (prefix)
        {
            case 0:
                throw new RlpException($"Non-canonical integer (leading zero bytes) at position {Position}");
            case < 128:
                return prefix;
            case 128:
                return 0;
        }

        int length = prefix - 128;
        if (length > 4)
        {
            throw new RlpException($"Unexpected length of int value: {length}");
        }

        int result = 0;
        for (int i = 4; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= Data[Position + length - i];
                if (result == 0)
                {
                    throw new RlpException($"Non-canonical integer (leading zero bytes) at position {Position}");
                }
            }
        }

        Position += length;

        return result;
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
            if (length == 1 && buffer[0] < 128)
            {
                ThrowUnexpectedValue(buffer[0]);
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
    }

    public Memory<byte>? DecodeByteArrayMemory()
    {
        if (!_sliceMemory)
        {
            return DecodeByteArraySpan().ToArray();
        }

        if (Memory is null)
        {
            ThrowNotMemoryBacked();
        }

        int prefix = ReadByte();

        switch (prefix)
        {
            case < 128:
                return Memory.Value.Slice(Position - 1, 1);
            case 128:
                return Array.Empty<byte>();
            case <= 183:
                {
                    int length = prefix - 128;
                    Memory<byte> buffer = ReadSlicedMemory(length);
                    Span<byte> asSpan = buffer.Span;
                    if (length == 1 && asSpan[0] < 128)
                    {
                        ThrowUnexpectedValue(asSpan[0]);
                    }

                    return buffer;
                }
            case < 192:
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

                    return ReadSlicedMemory(length);
                }
        }

        ThrowUnexpectedPrefix(prefix);
        return default;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNotMemoryBacked() => throw new RlpException("Rlp not backed by a Memory<byte>");
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowUnexpectedPrefix(int prefix) => throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowUnexpectedLength(int length)
    {
        throw new RlpException($"Expected length greater or equal 56 and was {length}");
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowUnexpectedValue(int buffer0) => throw new RlpException($"Unexpected byte value {buffer0}");

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowUnexpectedLengthOfLength() => throw new RlpException("Expected length of length less or equal 4");

    public void SkipItem()
    {
        (int prefix, int content) = PeekPrefixAndContentLength();
        Position += prefix + content;
    }

    public void Reset()
    {
        Position = 0;
    }

    public bool DecodeBool()
    {
        int prefix = ReadByte();
        if (prefix <= 128)
        {
            return prefix == 1;
        }

        if (prefix <= 183)
        {
            int length = prefix - 128;
            if (length == 1 && PeekByte() < 128)
            {
                throw new RlpException($"Unexpected byte value {PeekByte()}");
            }

            bool result = PeekByte() == 1;
            SkipBytes(length);
            return result;
        }

        if (prefix < 192)
        {
            int lengthOfLength = prefix - 183;
            if (lengthOfLength > 4)
            {
                // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                throw new RlpException("Expected length of length less or equal 4");
            }

            int length = DeserializeLength(lengthOfLength);
            if (length < 56)
            {
                throw new RlpException("Expected length greater or equal 56 and was {length}");
            }

            bool result = PeekByte() == 1;
            SkipBytes(length);
            return result;
        }

        throw new RlpException($"Unexpected prefix of {prefix} when decoding a byte array at position {Position} in the message of length {Length} starting with {Description}");
    }

    private readonly string Description => Data[..Math.Min(Rlp.DebugMessageContentLength, Length)].ToHexString();

    public readonly byte PeekByte() => Data[Position];

    private readonly byte PeekByte(int offset) => Data[Position + offset];

    private void SkipBytes(int length)
    {
        Position += length;
    }

    public string DecodeString()
    {
        ReadOnlySpan<byte> bytes = DecodeByteArraySpan();
        return Encoding.UTF8.GetString(bytes);
    }

    public long DecodeLong()
    {
        int prefix = ReadByte();

        switch (prefix)
        {
            case 0:
                throw new RlpException($"Non-canonical long (leading zero bytes) at position {Position}");
            case < 128:
                return prefix;
            case 128:
                return 0;
        }

        int length = prefix - 128;
        if (length > 8)
        {
            throw new RlpException($"Unexpected length of long value: {length}");
        }

        long result = 0;
        for (int i = 8; i > 0; i--)
        {
            result <<= 8;
            if (i <= length)
            {
                result |= PeekByte(length - i);
                if (result == 0)
                {
                    throw new RlpException($"Non-canonical long (leading zero bytes) at position {Position}");
                }
            }
        }

        SkipBytes(length);

        return result;
    }

    public ulong DecodeULong()
    {
        int prefix = ReadByte();

        switch (prefix)
        {
            case 0:
                throw new RlpException($"Non-canonical ulong (leading zero bytes) at position {Position}");
            case < 128:
                return (ulong)prefix;
            case 128:
                return 0;
        }

        int length = prefix - 128;
        if (length > 8)
        {
            throw new RlpException($"Unexpected length of long value: {length}");
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
                    throw new RlpException($"Non-canonical ulong (leading zero bytes) at position {Position}");
                }
            }
        }

        SkipBytes(length);

        return result;
    }

    internal byte[][] DecodeByteArrays()
    {
        int length = ReadSequenceLength();
        if (length is 0)
        {
            return [];
        }

        int itemsCount = PeekNumberOfItemsRemaining(Position + length);
        byte[][] result = new byte[itemsCount][];

        for (int i = 0; i < itemsCount; i++)
        {
            result[i] = DecodeByteArray();
        }

        return result;
    }

    public byte DecodeByte()
    {
        byte byteValue = PeekByte();
        if (byteValue < 128)
        {
            SkipBytes(1);
            return byteValue;
        }

        if (byteValue == 128)
        {
            SkipBytes(1);
            return 0;
        }

        if (byteValue == 129)
        {
            SkipBytes(1);
            return ReadByte();
        }

        throw new RlpException($"Unexpected value while decoding byte {byteValue}");
    }

    public T[] DecodeArray<T>(IRlpValueDecoder<T>? decoder = null, bool checkPositions = true,
        T defaultElement = default)
    {
        if (decoder is null)
        {
            decoder = Rlp.GetValueDecoder<T>();
            if (decoder is null)
            {
                throw new RlpException($"{nameof(Rlp)} does not support length of {nameof(T)}");
            }
        }
        int positionCheck = ReadSequenceLength() + Position;
        int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
        T[] result = new T[count];
        for (int i = 0; i < result.Length; i++)
        {
            if (PeekByte() == Rlp.OfEmptySequence[0])
            {
                result[i] = defaultElement;
                Position++;
            }
            else
            {
                result[i] = decoder.Decode(ref this);
            }
        }

        return result;
    }

    public readonly bool IsNextItemEmptyArray() => PeekByte() == Rlp.EmptyArrayByte;
}
