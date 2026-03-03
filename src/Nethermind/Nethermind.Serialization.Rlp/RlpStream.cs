// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static readonly BlockInfoDecoder _blockInfoDecoder = new();
        private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private static readonly WithdrawalDecoder _withdrawalDecoder = new();
        private static readonly LogEntryDecoder _logEntryDecoder = LogEntryDecoder.Instance;

        internal static ReadOnlySpan<byte> SingleBytes => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127];
        internal static readonly byte[][] SingleByteArrays = [[0], [1], [2], [3], [4], [5], [6], [7], [8], [9], [10], [11], [12], [13], [14], [15], [16], [17], [18], [19], [20], [21], [22], [23], [24], [25], [26], [27], [28], [29], [30], [31], [32], [33], [34], [35], [36], [37], [38], [39], [40], [41], [42], [43], [44], [45], [46], [47], [48], [49], [50], [51], [52], [53], [54], [55], [56], [57], [58], [59], [60], [61], [62], [63], [64], [65], [66], [67], [68], [69], [70], [71], [72], [73], [74], [75], [76], [77], [78], [79], [80], [81], [82], [83], [84], [85], [86], [87], [88], [89], [90], [91], [92], [93], [94], [95], [96], [97], [98], [99], [100], [101], [102], [103], [104], [105], [106], [107], [108], [109], [110], [111], [112], [113], [114], [115], [116], [117], [118], [119], [120], [121], [122], [123], [124], [125], [126], [127]];

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
                WriteByte(Rlp.EmptyListByte);
                return;
            }
            IRlpStreamEncoder<T> decoder = Rlp.GetStreamEncoder<T>();
            int contentLength = decoder.GetContentLength(items);

            StartSequence(contentLength);

            foreach (T? item in items)
            {
                decoder.Encode(this, item, rlpBehaviors);
            }
        }
        public void Encode(Block value) => _blockDecoder.Encode(this, value);

        public void Encode(BlockHeader value) => _headerDecoder.Encode(this, value);

        public void Encode(Transaction value, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => _txDecoder.Encode(this, value, rlpBehaviors);

        public void Encode(Withdrawal value) => _withdrawalDecoder.Encode(this, value);

        public void Encode(LogEntry value) => _logEntryDecoder.Encode(this, value);

        public void Encode(BlockInfo value) => _blockInfoDecoder.Encode(this, value);

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
