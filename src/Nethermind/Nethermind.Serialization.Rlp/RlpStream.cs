// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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

namespace Nethermind.Serialization.Rlp
{
    public class RlpStream
    {
        private static readonly HeaderDecoder _headerDecoder = new();
        private static readonly BlockDecoder _blockDecoder = new();
        private static readonly BlockBodyDecoder _blockBodyDecoder = new();
        private static readonly BlockInfoDecoder _blockInfoDecoder = new();
        private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private static readonly ReceiptMessageDecoder _receiptDecoder = new();
        private static readonly WithdrawalDecoder _withdrawalDecoder = new();
        private static readonly LogEntryDecoder _logEntryDecoder = LogEntryDecoder.Instance;

        private readonly CappedArray<byte> _data;
        private int _position = 0;

        protected RlpStream()
        {
        }

        public long MemorySize => MemorySizes.SmallObjectOverhead
                                  + MemorySizes.Align(MemorySizes.ArrayOverhead + Length)
                                  + MemorySizes.Align(sizeof(int));

        public RlpStream(int length)
        {
            _data = new byte[length];
        }

        public RlpStream(byte[] data)
        {
            _data = data;
        }

        public RlpStream(in CappedArray<byte> data)
        {
            _data = data;
        }

        public void EncodeArray<T>(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                WriteByte(Rlp.NullObjectByte);
                return;
            }
            IRlpStreamDecoder<T> decoder = Rlp.GetStreamDecoder<T>();
            int contentLength = decoder.GetContentLength(items);

            StartSequence(contentLength);

            foreach (var item in items)
            {
                decoder.Encode(this, item, rlpBehaviors);
            }
        }
        public void Encode(Block value)
        {
            _blockDecoder.Encode(this, value);
        }

        public void Encode(BlockHeader value)
        {
            _headerDecoder.Encode(this, value);
        }

        public void Encode(Transaction value, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            _txDecoder.Encode(this, value, rlpBehaviors);
        }

        public void Encode(Withdrawal value) => _withdrawalDecoder.Encode(this, value);

        public void Encode(LogEntry value)
        {
            _logEntryDecoder.Encode(this, value);
        }

        public void Encode(BlockInfo value)
        {
            _blockInfoDecoder.Encode(this, value);
        }

        public void StartByteArray(int contentLength, bool firstByteLessThan128)
        {
            switch (contentLength)
            {
                case 0:
                    WriteByte(EmptyArrayByte);
                    break;
                case 1 when firstByteLessThan128:
                    // the single byte of content will be written without any prefix
                    break;
                case < SmallPrefixBarrier:
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
            byte prefix;
            if (contentLength < SmallPrefixBarrier)
            {
                prefix = (byte)(192 + contentLength);
                WriteByte(prefix);
            }
            else
            {
                prefix = (byte)(247 + Rlp.LengthOfLength(contentLength));
                WriteByte(prefix);
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

        public virtual void WriteByte(byte byteToWrite)
        {
            Data[_position++] = byteToWrite;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(byte[] bytesToWrite)
        {
            Write(bytesToWrite.AsSpan());
        }

        public virtual void Write(ReadOnlySpan<byte> bytesToWrite)
        {
            bytesToWrite.CopyTo(Data.AsSpan(_position, bytesToWrite.Length));
            _position += bytesToWrite.Length;
        }

        protected virtual string Description =>
            Data.AsSpan(0, Math.Min(Rlp.DebugMessageContentLength, Length)).ToHexString() ?? "0x";

        public ref readonly CappedArray<byte> Data => ref _data;

        public virtual int Position
        {
            get
            {
                return _position;
            }
            set
            {
                _position = value;
            }
        }

        public virtual int Length => Data!.Length;

        public virtual bool HasBeenRead => Position >= Data!.Length;

        public bool IsSequenceNext()
        {
            return PeekByte() >= 192;
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
                var length = Rlp.LengthOf(keccaks);
                StartSequence(length);
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
                var length = Rlp.LengthOf(keccaks);
                StartSequence(length);
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
                var length = Rlp.LengthOf(keccaks);
                StartSequence(length);
                var count = keccaks.Count;
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
                var length = Rlp.LengthOf(keccaks);
                StartSequence(length);
                var count = keccaks.Count;
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

        protected virtual void WriteZero(int length)
        {
            Position += 256;
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

        public void Encode(long value) => Encode((ulong)value);

        [SkipLocalsInit]
        public void Encode(ulong value)
        {
            if (value < 128)
            {
                // Single-byte optimization for [0..127]
                byte singleByte = value > 0 ? (byte)value : EmptyArrayByte;
                WriteByte(singleByte);
                return;
            }

            // Count leading zero bytes
            int leadingZeroBytes = BitOperations.LeadingZeroCount(value) >> 3;
            int valueLength = sizeof(ulong) - leadingZeroBytes;

            value = BinaryPrimitives.ReverseEndianness(value);
            Span<byte> valueSpan = MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, byte>(ref value), sizeof(ulong));
            // Ok to stackalloc even if we don't use with SkipLocalsInit
            Span<byte> output = stackalloc byte[1 + sizeof(ulong)];

            byte prefix = (byte)(0x80 + valueLength);
            if (leadingZeroBytes > 0)
            {
                // Reuse space in valueSpan for prefix rather than copying
                valueSpan[leadingZeroBytes - 1] = prefix;
                output = valueSpan.Slice(leadingZeroBytes - 1, 1 + valueLength);
            }
            else
            {
                // Build final output: prefix + value bytes
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

        public void Encode(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteByte(128);
            }
            else
            {
                // todo: can avoid allocation here but benefit is rare
                Encode(Encoding.ASCII.GetBytes(value));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Encode(byte[] input)
        {
            Encode(input.AsSpan());
        }

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

        public void Encode(ReadOnlySpan<byte> input)
        {
            if (input.IsEmpty)
            {
                WriteByte(EmptyArrayByte);
            }
            else if (input.Length == 1 && input[0] < 128)
            {
                WriteByte(input[0]);
            }
            else if (input.Length < SmallPrefixBarrier)
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
                    if (length < SmallPrefixBarrier)
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

        public int PeekNextRlpLength()
        {
            (int a, int b) = PeekPrefixAndContentLength();
            return a + b;
        }

        public (int PrefixLength, int ContentLength) ReadPrefixAndContentLength()
        {
            (int prefixLength, int contentLength) result = PeekPrefixAndContentLength();
            Position += result.prefixLength;
            return result;
        }

        public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
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
                if (lengthOfLength > 4)
                {
                    // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                    throw new RlpException("Expected length of length less or equal 4");
                }

                int length = PeekDeserializeLength(1, lengthOfLength);
                if (length < SmallPrefixBarrier)
                {
                    throw new RlpException($"Expected length greater or equal 56 and was {length}");
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
                if (contentLength < SmallPrefixBarrier)
                {
                    throw new RlpException($"Expected length greater or equal 56 and got {contentLength}");
                }


                result = (lengthOfContentLength + 1, contentLength);
            }

            return result;
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
            if (contentLength < SmallPrefixBarrier)
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

        private int PeekDeserializeLength(int offset, int lengthOfLength)
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

        public virtual byte ReadByte()
        {
            return Data![_position++];
        }

        public virtual byte PeekByte()
        {
            return Data![_position];
        }

        protected virtual byte PeekByte(int offset)
        {
            return Data![_position + offset];
        }

        protected virtual void SkipBytes(int length)
        {
            _position += length;
        }

        public virtual Span<byte> Read(int length)
        {
            Span<byte> data = Data.AsSpan(_position, length);
            _position += length;
            return data;
        }

        public void Check(int nextCheck)
        {
            if (Position != nextCheck)
            {
                throw new RlpException($"Data checkpoint failed. Expected {nextCheck} and is {Position}");
            }
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

        public Address? DecodeAddress()
        {
            int prefix = ReadByte();
            if (prefix == 128)
            {
                return null;
            }

            if (prefix != 128 + 20)
            {
                throw new RlpException(
                    $"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {Position} in the message of length {Length} starting with {Description}");
            }

            byte[] buffer = Read(20).ToArray();
            return new Address(buffer);
        }

        public UInt256 DecodeUInt256(int length = -1)
        {
            byte byteValue = PeekByte();

            if (byteValue == 0)
            {
                throw new RlpException($"Non-canonical UInt256 (leading zero bytes) at position {Position}");
            }

            if (byteValue < 128)
            {
                SkipBytes(1);
                return byteValue;
            }

            ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan();

            if (byteSpan.Length > 32)
            {
                throw new RlpException("UInt256 cannot be longer than 32 bytes");
            }

            if (length == -1)
            {
                if (byteSpan.Length > 1 && byteSpan[0] == 0)
                {
                    throw new RlpException($"Non-canonical UInt256 (leading zero bytes) at position {Position}");
                }
            }
            else if (byteSpan.Length != length)
            {
                throw new RlpException($"Invalid length at position {Position}");
            }

            return new UInt256(byteSpan, true);
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
            if (PeekByte() == 249)
            {
                SkipBytes(5); // tks: skip 249 1 2 129 127 and read 256 bytes
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
                throw new RlpException("Incorrect bloom RLP");
            }

            return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes.ToArray());
        }

        public Span<byte> PeekNextItem()
        {
            int length = PeekNextRlpLength();
            return Peek(length);
        }

        public Span<byte> Peek(int length)
        {
            return Peek(0, length);
        }

        public virtual Span<byte> Peek(int offset, int length)
        {
            return Data.AsSpan(_position + offset, length);
        }

        public bool IsNextItemEmptyArray()
        {
            return PeekByte() == Rlp.EmptyArrayByte;
        }

        public bool IsNextItemNull()
        {
            return PeekByte() == Rlp.NullObjectByte;
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
                if (length < SmallPrefixBarrier)
                {
                    throw new RlpException("Expected length greater or equal 56 and was {length}");
                }

                bool result = PeekByte() == 1;
                SkipBytes(length);
                return result;
            }

            throw new RlpException(
                $"Unexpected prefix of {prefix} when decoding a byte array at position {Position} in the message of length {Length} starting with {Description}");
        }

        public T[] DecodeArray<T>(Func<RlpStream, T> decodeItem, bool checkPositions = true,
            T defaultElement = default)
        {
            int positionCheck = ReadSequenceLength() + Position;
            int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : (int?)null);
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
                    result[i] = decodeItem(this);
                }
            }

            return result;
        }

        public ArrayPoolList<T> DecodeArrayPoolList<T>(Func<RlpStream, T> decodeItem, bool checkPositions = true,
            T defaultElement = default)
        {
            int positionCheck = ReadSequenceLength() + Position;
            int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : (int?)null);
            var result = new ArrayPoolList<T>(count, count);
            for (int i = 0; i < result.Count; i++)
            {
                if (PeekByte() == Rlp.OfEmptySequence[0])
                {
                    result[i] = defaultElement;
                    Position++;
                }
                else
                {
                    result[i] = decodeItem(this);
                }
            }

            return result;
        }

        public string DecodeString()
        {
            ReadOnlySpan<byte> bytes = DecodeByteArraySpan();
            return Encoding.UTF8.GetString(bytes);
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
                    result |= PeekByte(length - i);
                    if (result == 0)
                    {
                        throw new RlpException($"Non-canonical integer (leading zero bytes) at position {Position}");
                    }
                }
            }

            SkipBytes(length);

            return result;
        }

        public uint DecodeUInt()
        {
            ReadOnlySpan<byte> bytes = DecodeByteArraySpan();
            if (bytes.Length > 1 && bytes[0] == 0)
            {
                throw new RlpException($"Non-canonical UInt (leading zero bytes) at position {Position}");
            }
            return bytes.Length == 0 ? 0 : bytes.ReadEthUInt32();
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

            ulong result = 0;
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

        public ulong DecodeUlong()
        {
            ReadOnlySpan<byte> bytes = DecodeByteArraySpan();
            if (bytes.Length > 1 && bytes[0] == 0)
            {
                throw new RlpException($"Non-canonical ulong (leading zero bytes) at position {Position}");
            }
            return bytes.Length == 0 ? 0L : bytes.ReadEthUInt64();
        }

        public byte[] DecodeByteArray()
        {
            return Rlp.ByteSpanToArray(DecodeByteArraySpan());
        }

        public ArrayPoolList<byte> DecodeByteArrayPoolList()
        {
            return Rlp.ByteSpanToArrayPool(DecodeByteArraySpan());
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
                if (length < SmallPrefixBarrier)
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

        public void EncodeNullObject()
        {
            WriteByte(EmptySequenceByte);
        }

        public void EncodeEmptyByteArray()
        {
            WriteByte(EmptyArrayByte);
        }

        private const byte EmptyArrayByte = 128;
        private const byte EmptySequenceByte = 192;
        private const int SmallPrefixBarrier = 56;

        public override string ToString()
        {
            return $"[{nameof(RlpStream)}|{Position}/{Length}]";
        }

        public byte[][] DecodeByteArrays()
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

        internal static ReadOnlySpan<byte> SingleBytes => new byte[128] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127 };
        internal static readonly byte[][] SingleByteArrays = new byte[128][] { [0], [1], [2], [3], [4], [5], [6], [7], [8], [9], [10], [11], [12], [13], [14], [15], [16], [17], [18], [19], [20], [21], [22], [23], [24], [25], [26], [27], [28], [29], [30], [31], [32], [33], [34], [35], [36], [37], [38], [39], [40], [41], [42], [43], [44], [45], [46], [47], [48], [49], [50], [51], [52], [53], [54], [55], [56], [57], [58], [59], [60], [61], [62], [63], [64], [65], [66], [67], [68], [69], [70], [71], [72], [73], [74], [75], [76], [77], [78], [79], [80], [81], [82], [83], [84], [85], [86], [87], [88], [89], [90], [91], [92], [93], [94], [95], [96], [97], [98], [99], [100], [101], [102], [103], [104], [105], [106], [107], [108], [109], [110], [111], [112], [113], [114], [115], [116], [117], [118], [119], [120], [121], [122], [123], [124], [125], [126], [127] };
    }
}
