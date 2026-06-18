// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public static class RlpWriterExtensions
{
    private const byte EmptyArrayByte = 128;
    private const byte EmptySequenceByte = 192;

    extension<TWriter>(ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        public void StartByteArray(int contentLength, bool firstByteLessThan128)
        {
            switch (contentLength)
            {
                case 0:
                    writer.WriteByte(EmptyArrayByte);
                    break;
                case 1 when firstByteLessThan128:
                    break;
                case < RlpHelpers.SmallPrefixBarrier:
                    {
                        byte smallPrefix = (byte)(contentLength + 128);
                        writer.WriteByte(smallPrefix);
                        break;
                    }
                default:
                    {
                        int lengthOfLength = Rlp.LengthOfLength(contentLength);
                        byte prefix = (byte)(183 + lengthOfLength);
                        writer.WriteByte(prefix);
                        writer.WriteEncodedLength(contentLength);
                        break;
                    }
            }
        }

        public void StartSequence(int contentLength)
        {
            if (contentLength < RlpHelpers.SmallPrefixBarrier)
            {
                writer.WriteByte((byte)(192 + contentLength));
            }
            else
            {
                writer.WriteByte((byte)(247 + Rlp.LengthOfLength(contentLength)));
                writer.WriteEncodedLength(contentLength);
            }
        }

        private void WriteEncodedLength(int value)
        {
            switch (value)
            {
                case < 1 << 8:
                    writer.WriteByte((byte)value);
                    return;
                case < 1 << 16:
                    writer.WriteByte((byte)(value >> 8));
                    writer.WriteByte((byte)value);
                    return;
                case < 1 << 24:
                    writer.WriteByte((byte)(value >> 16));
                    writer.WriteByte((byte)(value >> 8));
                    writer.WriteByte((byte)value);
                    return;
                default:
                    writer.WriteByte((byte)(value >> 24));
                    writer.WriteByte((byte)(value >> 16));
                    writer.WriteByte((byte)(value >> 8));
                    writer.WriteByte((byte)value);
                    return;
            }
        }

        public void WriteByteArrayList(IByteArrayList? list)
        {
            if (list is null || list.Count == 0)
            {
                writer.EncodeNullObject();
                return;
            }

            if (list is IRlpWrapper rlpWrapper)
            {
                rlpWrapper.Write(ref writer);
                return;
            }

            int contentLength = 0;
            for (int i = 0; i < list.Count; i++)
            {
                contentLength += Rlp.LengthOf(list[i]);
            }

            writer.StartSequence(contentLength);
            for (int i = 0; i < list.Count; i++)
            {
                writer.Encode(list[i]);
            }
        }

        public void Encode(Hash256? keccak)
        {
            if (keccak is null)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else if (ReferenceEquals(keccak, Keccak.EmptyTreeHash))
            {
                writer.Write(Rlp.OfEmptyTreeHash.Bytes);
            }
            else if (ReferenceEquals(keccak, Keccak.OfAnEmptyString))
            {
                writer.Write(Rlp.OfEmptyStringHash.Bytes);
            }
            else
            {
                writer.WriteByte(160);
                writer.Write(keccak.Bytes);
            }
        }

        public void Encode(in ValueHash256? keccak)
        {
            if (keccak is null)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else
            {
                writer.WriteByte(160);
                writer.Write(keccak.Value.Bytes);
            }
        }

        public void Encode(Hash256[] keccaks)
        {
            if (keccaks is null)
            {
                writer.EncodeNullObject();
            }
            else
            {
                writer.StartSequence(Rlp.LengthOf(keccaks));
                for (int i = 0; i < keccaks.Length; i++)
                {
                    writer.Encode(keccaks[i]);
                }
            }
        }

        public void Encode(ValueHash256[] keccaks)
        {
            if (keccaks is null)
            {
                writer.EncodeNullObject();
            }
            else
            {
                writer.StartSequence(Rlp.LengthOf(keccaks));
                for (int i = 0; i < keccaks.Length; i++)
                {
                    writer.Encode(keccaks[i]);
                }
            }
        }

        public void Encode(IReadOnlyList<Hash256> keccaks)
        {
            if (keccaks is null)
            {
                writer.EncodeNullObject();
            }
            else
            {
                writer.StartSequence(Rlp.LengthOf(keccaks));
                int count = keccaks.Count;
                for (int i = 0; i < count; i++)
                {
                    writer.Encode(keccaks[i]);
                }
            }
        }

        public void Encode(IReadOnlyList<ValueHash256> keccaks)
        {
            if (keccaks is null)
            {
                writer.EncodeNullObject();
            }
            else
            {
                writer.StartSequence(Rlp.LengthOf(keccaks));
                int count = keccaks.Count;
                for (int i = 0; i < count; i++)
                {
                    writer.Encode(keccaks[i]);
                }
            }
        }

        public void Encode(Address? address)
        {
            if (address is null)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else
            {
                writer.WriteByte(148);
                writer.Write(address.Bytes);
            }
        }

        public void Encode(Rlp? rlp)
        {
            if (rlp is null)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else
            {
                writer.Write(rlp.Bytes);
            }
        }

        public void Encode(Bloom? bloom)
        {
            if (ReferenceEquals(bloom, Bloom.Empty))
            {
                writer.WriteByte(185);
                writer.WriteByte(1);
                writer.WriteByte(0);
                writer.WriteZero(256);
            }
            else if (bloom is null)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else
            {
                writer.WriteByte(185);
                writer.WriteByte(1);
                writer.WriteByte(0);
                writer.Write(bloom.Bytes);
            }
        }

        public void Encode(byte value)
        {
            if (value == 0)
            {
                writer.WriteByte(128);
            }
            else if (value < 128)
            {
                writer.WriteByte(value);
            }
            else
            {
                writer.WriteByte(129);
                writer.WriteByte(value);
            }
        }

        public void Encode(bool value) => writer.Encode(value ? (byte)1 : (byte)0);

        public void Encode(int value) => writer.Encode((ulong)(long)value);

        public void Encode(uint value) => writer.Encode((ulong)value);

        public void Encode(long value) => writer.Encode((ulong)value);

        [SkipLocalsInit]
        public void Encode(ulong value)
        {
            if (value < 128)
            {
                byte singleByte = value > 0 ? (byte)value : EmptyArrayByte;
                writer.WriteByte(singleByte);
                return;
            }

            int leadingZeroBytes = BitOperations.LeadingZeroCount(value) >> 3;
            int valueLength = sizeof(ulong) - leadingZeroBytes;

            value = BinaryPrimitives.ReverseEndianness(value);
            Span<byte> valueSpan = MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, byte>(ref value), sizeof(ulong));
            Span<byte> output = stackalloc byte[1 + sizeof(ulong)];

            byte prefix = (byte)(0x80 + valueLength);
            if (leadingZeroBytes > 0)
            {
                valueSpan[leadingZeroBytes - 1] = prefix;
                output = valueSpan.Slice(leadingZeroBytes - 1, 1 + valueLength);
            }
            else
            {
                output[0] = prefix;
                valueSpan.Slice(leadingZeroBytes, valueLength).CopyTo(output.Slice(1));
                output = output.Slice(0, 1 + valueLength);
            }

            writer.Write(output);
        }

        public void Encode(in UInt256 value, int length = -1)
        {
            if (value.IsZero && length == -1)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else
            {
                Span<byte> bytes = stackalloc byte[32];
                value.ToBigEndian(bytes);
                writer.Encode(length != -1 ? bytes.Slice(bytes.Length - length, length) : bytes.WithoutLeadingZeros());
            }
        }

        public void Encode(in EvmWord value)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<EvmWord, byte>(ref Unsafe.AsRef(in value)), 32);
            int nonZero = bytes.IndexOfAnyExcept((byte)0);
            if (nonZero < 0)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else
            {
                writer.Encode(bytes.Slice(nonZero));
            }
        }

        public void Encode(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                writer.WriteByte(128);
            }
            else
            {
                writer.Encode(Encoding.ASCII.GetBytes(value));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Encode(byte[] input) => writer.Encode(input.AsSpan());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Encode(Memory<byte>? input)
        {
            if (input is null)
            {
                writer.WriteByte(EmptyArrayByte);
                return;
            }

            writer.Encode(input.Value.Span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Encode(in ReadOnlyMemory<byte> input) => writer.Encode(input.Span);

        public void Encode(scoped ReadOnlySpan<byte> input)
        {
            if (input.IsEmpty)
            {
                writer.WriteByte(EmptyArrayByte);
            }
            else if (input.Length == 1 && input[0] < 128)
            {
                writer.WriteByte(input[0]);
            }
            else if (input.Length < RlpHelpers.SmallPrefixBarrier)
            {
                byte smallPrefix = (byte)(input.Length + 128);
                writer.WriteByte(smallPrefix);
                writer.Write(input);
            }
            else
            {
                int lengthOfLength = Rlp.LengthOfLength(input.Length);
                byte prefix = (byte)(183 + lengthOfLength);
                writer.WriteByte(prefix);
                writer.WriteEncodedLength(input.Length);
                writer.Write(input);
            }
        }

        public void Encode(byte[][] arrays)
        {
            int itemsLength = 0;
            foreach (byte[] array in arrays)
            {
                itemsLength += Rlp.LengthOf(array);
            }

            writer.StartSequence(itemsLength);
            foreach (byte[] array in arrays)
            {
                writer.Encode(array);
            }
        }

        public void EncodeNullObject() => writer.WriteByte(EmptySequenceByte);

        public void EncodeEmptyByteArray() => writer.WriteByte(EmptyArrayByte);
    }
}
