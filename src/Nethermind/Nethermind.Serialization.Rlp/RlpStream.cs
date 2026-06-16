// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Serialization.Rlp
{
    public class RlpStream
    {
        private static readonly HeaderDecoder _headerDecoder = new();
        private static readonly BlockDecoder _blockDecoder = new();
        private static readonly BlockInfoDecoder _blockInfoDecoder = new();
        private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private static readonly WithdrawalDecoder _withdrawalDecoder = new();
        private static readonly BlockAccessListDecoder _blockAccessListDecoder = BlockAccessListDecoder.Instance;
        private static readonly LogEntryDecoder _logEntryDecoder = LogEntryDecoder.Instance;
        private static readonly ValueRlpWriteSink _writeToRlpStream = WriteToRlpStream;
        private static readonly ValueRlpWriteByteSink _writeByteToRlpStream = WriteByteToRlpStream;

        private readonly CappedArray<byte> _data;
        private int _position = 0;

        protected RlpStream()
        {
        }

        public long MemorySize => MemorySizes.SmallObjectOverhead
                                  + MemorySizes.Align(MemorySizes.ArrayOverhead + Length)
                                  + MemorySizes.Align(sizeof(int));

        public RlpStream(int length) => _data = new byte[length];

        public RlpStream(byte[] data) => _data = data;

        public RlpStream(in CappedArray<byte> data) => _data = data;

        public void EncodeArray<T>(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                WriteByte(Rlp.EmptyListByte);
                return;
            }
            IRlpDecoder<T> decoder = Rlp.GetDecoder<T>();
            int contentLength = decoder.GetContentLength(items);

            StartSequence(contentLength);

            foreach (T? item in items)
            {
                if (item is null)
                {
                    WriteByte(Rlp.EmptyListByte);
                }
                else
                {
                    ValueRlpWriter writer = CreateWriter(out bool advancePosition);
                    decoder.Encode(ref writer, item, rlpBehaviors);
                    Advance(ref writer, advancePosition);
                }
            }
        }
        public void Encode(Block value)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _blockDecoder.Encode(ref writer, value);
            Advance(ref writer, advancePosition);
        }

        public void Encode(BlockHeader value)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _headerDecoder.Encode(ref writer, value);
            Advance(ref writer, advancePosition);
        }

        public void Encode(Transaction value, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _txDecoder.Encode(ref writer, value, rlpBehaviors);
            Advance(ref writer, advancePosition);
        }

        public void Encode(Withdrawal value)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _withdrawalDecoder.Encode(ref writer, value);
            Advance(ref writer, advancePosition);
        }

        public void Encode(LogEntry value)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _logEntryDecoder.Encode(ref writer, value);
            Advance(ref writer, advancePosition);
        }

        public void Encode(BlockInfo value)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _blockInfoDecoder.Encode(ref writer, value);
            Advance(ref writer, advancePosition);
        }

        public void Encode(ReadOnlyBlockAccessList value)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _blockAccessListDecoder.Encode(ref writer, value);
            Advance(ref writer, advancePosition);
        }

        public void Encode(GeneratedBlockAccessList value)
        {
            ValueRlpWriter writer = CreateWriter(out bool advancePosition);
            _blockAccessListDecoder.Encode(ref writer, value);
            Advance(ref writer, advancePosition);
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
            byte prefix;
            if (contentLength < RlpHelpers.SmallPrefixBarrier)
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

        public virtual void WriteByte(byte byteToWrite) => Data[_position++] = byteToWrite;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(byte[] bytesToWrite) => Write(bytesToWrite.AsSpan());

        public virtual void Write(ReadOnlySpan<byte> bytesToWrite)
        {
            bytesToWrite.CopyTo(Data.AsSpan(_position, bytesToWrite.Length));
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
                Write(rlpWrapper);
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

        private void Write(IRlpWrapper rlpWrapper)
        {
            if (GetType() == typeof(RlpStream))
            {
                ValueRlpWriter writer = new(Data.AsSpan(Position, rlpWrapper.RlpLength));
                rlpWrapper.Write(ref writer);
                Position += writer.Position;
                return;
            }

            ValueRlpWriter streamWriter = CreateWriter(out bool advancePosition);
            rlpWrapper.Write(ref streamWriter);
            Advance(ref streamWriter, advancePosition);
        }

        private ValueRlpWriter CreateWriter(out bool advancePosition)
        {
            advancePosition = GetType() == typeof(RlpStream);
            return advancePosition
                ? new ValueRlpWriter(Data.AsSpan(Position, Length - Position))
                : new ValueRlpWriter(this, _writeToRlpStream, _writeByteToRlpStream);
        }

        private void Advance(ref ValueRlpWriter writer, bool advancePosition)
        {
            if (advancePosition)
            {
                Position += writer.Position;
            }
        }

        private static void WriteToRlpStream(object sink, ReadOnlySpan<byte> bytesToWrite) =>
            ((RlpStream)sink).Write(bytesToWrite);

        private static void WriteByteToRlpStream(object sink, byte byteToWrite) =>
            ((RlpStream)sink).WriteByte(byteToWrite);

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
                int length = Rlp.LengthOf(keccaks);
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
                int length = Rlp.LengthOf(keccaks);
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
                int length = Rlp.LengthOf(keccaks);
                StartSequence(length);
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
                int length = Rlp.LengthOf(keccaks);
                StartSequence(length);
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

        protected virtual void WriteZero(int length)
        {
            Data.AsSpan(Position, length).Clear();
            Position += length;
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
                // todo: can avoid allocation here but benefit is rare
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
        public void Encode(in ReadOnlyMemory<byte> input)
            => Encode(input.Span);

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

        public override string ToString() => $"[{nameof(RlpStream)}|{Position}/{Length}]";
    }
}
