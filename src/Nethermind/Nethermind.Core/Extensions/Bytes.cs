// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using System.Text;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions
{
    public static unsafe partial class Bytes
    {
        public static readonly IEqualityComparer<byte[]> EqualityComparer = new BytesEqualityComparer();
        public static readonly IEqualityComparer<byte[]?> NullableEqualityComparer = new NullableBytesEqualityComparer();
        public static readonly BytesComparer Comparer = new();
        // The ReadOnlyMemory<byte> needs to be initialized = or it will be created each time.
        public static ReadOnlyMemory<byte> ZeroByte = new byte[] { 0 };
        public static ReadOnlyMemory<byte> OneByte = new byte[] { 1 };
        public static ReadOnlyMemory<byte> TwoByte = new byte[] { 2 };
        // The Jit converts a ReadOnlySpan<byte> => new byte[] to a data section load, no allocation.
        public static ReadOnlySpan<byte> ZeroByteSpan => new byte[] { 0 };
        public static ReadOnlySpan<byte> OneByteSpan => new byte[] { 1 };

        public const string ZeroHexValue = "0x0";
        public const string ZeroValue = "0";
        public const string EmptyHexValue = "0x";

        private class BytesEqualityComparer : EqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
        {
            public byte[] Create(ReadOnlySpan<byte> alternate)
                => alternate.ToArray();

            public override bool Equals(byte[]? x, byte[]? y)
                => AreEqual(x, y);

            public bool Equals(ReadOnlySpan<byte> alternate, byte[] other)
                => AreEqual(alternate, other.AsSpan());

            public override int GetHashCode(byte[] obj) => new ReadOnlySpan<byte>(obj).FastHash();

            public int GetHashCode(ReadOnlySpan<byte> alternate) => alternate.FastHash();
        }

        private class NullableBytesEqualityComparer : EqualityComparer<byte[]?>
        {
            public override bool Equals(byte[]? x, byte[]? y)
            {
                return AreEqual(x, y);
            }

            public override int GetHashCode(byte[]? obj) => new ReadOnlySpan<byte>(obj).FastHash();
        }

        public class BytesComparer : Comparer<byte[]>
        {
            public override int Compare(byte[]? x, byte[]? y)
            {
                if (ReferenceEquals(x, y)) return 0;

                if (x is null)
                {
                    return y is null ? 0 : 1;
                }

                if (y is null)
                {
                    return -1;
                }

                if (x.Length == 0)
                {
                    return y.Length == 0 ? 0 : 1;
                }

                for (int i = 0; i < x.Length; i++)
                {
                    if (y.Length <= i)
                    {
                        return -1;
                    }

                    int result = x[i].CompareTo(y[i]);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return y.Length > x.Length ? 1 : 0;
            }

            public static int Compare(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
            {
                if (Unsafe.AreSame(ref MemoryMarshal.GetReference(x), ref MemoryMarshal.GetReference(y)) &&
                    x.Length == y.Length)
                {
                    return 0;
                }

                if (x.Length == 0)
                {
                    return y.Length == 0 ? 0 : 1;
                }

                for (int i = 0; i < x.Length; i++)
                {
                    if (y.Length <= i)
                    {
                        return -1;
                    }

                    int result = x[i].CompareTo(y[i]);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return y.Length > x.Length ? 1 : 0;
            }
        }

        public static readonly byte[] Zero32 = new byte[32];

        public static readonly byte[] Empty = [];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetBit(this byte b, int bitNumber)
        {
            return (b & (1 << (7 - bitNumber))) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetBit(this ref byte b, int bitNumber)
        {
            int mask = (1 << (7 - bitNumber));
            b |= (byte)mask;
        }

        public static int GetHighestSetBitIndex(this byte b)
            => 32 - BitOperations.LeadingZeroCount((uint)b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreEqual(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            if (Unsafe.AreSame(ref MemoryMarshal.GetReference(a1), ref MemoryMarshal.GetReference(a2)) &&
                a1.Length == a2.Length)
            {
                return true;
            }

            // this works for nulls
            return a1.SequenceEqual(a2);
        }

        public static bool IsZero(this byte[] bytes)
        {
            return bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0;
        }

        public static bool IsZero(this Span<byte> bytes)
        {
            return bytes.IndexOfAnyExcept((byte)0) < 0;
        }

        public static bool IsZero(this ReadOnlySpan<byte> bytes)
        {
            return bytes.IndexOfAnyExcept((byte)0) < 0;
        }

        public static int LeadingZerosCount(this Span<byte> bytes, int startIndex = 0)
        {
            int nonZeroIndex = bytes[startIndex..].IndexOfAnyExcept((byte)0);
            return nonZeroIndex < 0 ? bytes.Length - startIndex : nonZeroIndex;
        }

        public static int TrailingZerosCount(this byte[] bytes)
        {
            int lastIndex = bytes.AsSpan().LastIndexOfAnyExcept((byte)0);
            return lastIndex < 0 ? bytes.Length : bytes.Length - lastIndex - 1;
        }

        public static ReadOnlySpan<byte> WithoutLeadingZeros(this byte[] bytes)
        {
            return bytes.AsSpan().WithoutLeadingZeros();
        }

        public static ReadOnlySpan<byte> WithoutLeadingZerosOrEmpty(this byte[] bytes)
        {
            if (bytes is null || bytes.Length == 0) return [];
            return bytes.AsSpan().WithoutLeadingZeros();
        }

        public static ReadOnlySpan<byte> WithoutLeadingZerosOrEmpty(this Span<byte> bytes) =>
            ((ReadOnlySpan<byte>)bytes).WithoutLeadingZeros();

        public static ReadOnlySpan<byte> WithoutLeadingZeros(this Span<byte> bytes)
        {
            return ((ReadOnlySpan<byte>)bytes).WithoutLeadingZeros();
        }

        public static ReadOnlySpan<byte> WithoutLeadingZeros(this ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return ZeroByteSpan;

            int nonZeroIndex = bytes.IndexOfAnyExcept((byte)0);
            // Keep one or it will be interpreted as null
            return nonZeroIndex < 0 ? bytes[^1..] : bytes[nonZeroIndex..];
        }

        public static byte[] Concat(byte prefix, byte[] bytes)
        {
            byte[] result = new byte[1 + bytes.Length];
            result[0] = prefix;
            Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
            return result;
        }

        public static byte[] PadLeft(this byte[] bytes, int length, byte padding = 0)
            => bytes.Length == length ? bytes : bytes.AsSpan().PadLeft(length, padding);

        public static byte[] PadLeft(this Span<byte> bytes, int length, byte padding = 0) =>
            ((ReadOnlySpan<byte>)bytes).PadLeft(length, padding);

        public static byte[] PadLeft(this ReadOnlySpan<byte> bytes, int length, byte padding = 0)
        {
            if (bytes.Length == length)
            {
                return bytes.ToArray();
            }

            if (bytes.Length > length)
            {
                return bytes[..length].ToArray();
            }

            byte[] result = new byte[length];
            bytes.CopyTo(result.AsSpan(length - bytes.Length));

            if (padding != 0)
            {
                for (int i = 0; i < length - bytes.Length; i++)
                {
                    result[i] = padding;
                }
            }

            return result;
        }

        public static byte[] PadRight(this byte[] bytes, int length)
        {
            if (bytes.Length == length)
            {
                return (byte[])bytes.Clone();
            }

            if (bytes.Length > length)
            {
                return bytes.Slice(0, length);
            }

            byte[] result = new byte[length];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public static byte[] Concat(byte[] part1, byte[] part2)
        {
            byte[] result = new byte[part1.Length + part2.Length];
            part1.CopyTo(result, 0);
            part2.CopyTo(result.AsSpan(part1.Length));
            return result;
        }

        public static byte[] Concat(params byte[][] parts)
        {
            return Concat(parts.AsSpan());
        }

        public static byte[] Concat(ReadOnlySpan<byte[]> bytes)
        {
            int totalLength = 0;
            foreach (byte[] byteArray in bytes)
            {
                totalLength += byteArray.Length;
            }

            byte[] result = new byte[totalLength];
            int offset = 0;

            foreach (byte[] byteArray in bytes)
            {
                Array.Copy(byteArray, 0, result, offset, byteArray.Length);
                offset += byteArray.Length;
            }

            return result;
        }

        public static byte[] Concat(ReadOnlySpan<byte> part1, ReadOnlySpan<byte> part2)
        {
            byte[] result = new byte[part1.Length + part2.Length];
            part1.CopyTo(result);
            part2.CopyTo(result.AsSpan(part1.Length));
            return result;
        }

        public static byte[] Concat(ReadOnlySpan<byte> part1, ReadOnlySpan<byte> part2, ReadOnlySpan<byte> part3)
        {
            byte[] result = new byte[part1.Length + part2.Length + part3.Length];
            part1.CopyTo(result);
            part2.CopyTo(result.AsSpan(part1.Length));
            part3.CopyTo(result.AsSpan(part1.Length + part2.Length));
            return result;
        }

        public static byte[] Concat(ReadOnlySpan<byte> part1, ReadOnlySpan<byte> part2, ReadOnlySpan<byte> part3, ReadOnlySpan<byte> part4)
        {
            byte[] result = new byte[part1.Length + part2.Length + part3.Length + part4.Length];
            part1.CopyTo(result);
            part2.CopyTo(result.AsSpan(part1.Length));
            part3.CopyTo(result.AsSpan(part1.Length + part2.Length));
            part4.CopyTo(result.AsSpan(part1.Length + part2.Length + part3.Length));
            return result;
        }

        public static byte[] Concat(byte[] bytes, byte suffix)
        {
            byte[] result = new byte[bytes.Length + 1];
            result[^1] = suffix;
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public static byte[] Concat(ReadOnlySpan<byte> bytes, byte suffix)
        {
            byte[] result = new byte[bytes.Length + 1];
            result[^1] = suffix;
            bytes.CopyTo(result);
            return result;
        }

        public static byte[] Reverse(byte[] bytes)
        {
            byte[] result = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                result[i] = bytes[bytes.Length - i - 1];
            }

            return result;
        }

        public static void ReverseInPlace(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length / 2; i++)
            {
                (bytes[i], bytes[bytes.Length - i - 1]) = (bytes[bytes.Length - i - 1], bytes[i]);
            }
        }

        public static BigInteger ToUnsignedBigInteger(this byte[] bytes)
        {
            return ToUnsignedBigInteger(bytes.AsSpan());
        }

        public static BigInteger ToUnsignedBigInteger(this Span<byte> bytes)
        {
            return ToUnsignedBigInteger((ReadOnlySpan<byte>)bytes);
        }

        public static BigInteger ToUnsignedBigInteger(this ReadOnlySpan<byte> bytes)
        {
            return new(bytes, true, true);
        }

        public static ReadOnlySpan<byte> Trim(this ReadOnlySpan<byte> bytes, int length)
            => bytes.Length > length ? bytes.Slice(bytes.Length - length, length) : bytes;

        public static short ReadEthInt16(this ReadOnlySpan<byte> bytes)
        {
            bytes = bytes.Trim(2);

            return bytes.Length switch
            {
                2 => BinaryPrimitives.ReadInt16BigEndian(bytes),
                1 => bytes[0],
                _ => 0
            };
        }

        public static ushort ReadEthUInt16(this ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 2)
            {
                bytes = bytes.Slice(bytes.Length - 2, 2);
            }

            return bytes.Length switch
            {
                2 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
                1 => bytes[0],
                _ => 0
            };
        }

        public static ushort ReadEthUInt16LittleEndian(this Span<byte> bytes)
        {
            if (bytes.Length > 2)
            {
                bytes = bytes.Slice(bytes.Length - 2, 2);
            }

            return bytes.Length switch
            {
                2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                1 => bytes[0],
                _ => 0
            };
        }

        public static uint ReadEthUInt32(this Span<byte> bytes)
        {
            return ReadEthUInt32((ReadOnlySpan<byte>)bytes);
        }

        public static uint ReadEthUInt32(this ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 4)
            {
                bytes = bytes.Slice(bytes.Length - 4, 4);
            }

            if (bytes.Length == 4)
            {
                return BinaryPrimitives.ReadUInt32BigEndian(bytes);
            }

            Span<byte> fourBytes = stackalloc byte[4];
            bytes.CopyTo(fourBytes[(4 - bytes.Length)..]);
            return BinaryPrimitives.ReadUInt32BigEndian(fourBytes);
        }

        public static int ReadEthInt32(this Span<byte> bytes)
        {
            return ReadEthInt32((ReadOnlySpan<byte>)bytes);
        }

        public static int ReadEthInt32(this ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 4)
            {
                bytes = bytes.Slice(bytes.Length - 4, 4);
            }

            if (bytes.Length == 4)
            {
                return BinaryPrimitives.ReadInt32BigEndian(bytes);
            }

            Span<byte> fourBytes = stackalloc byte[4];
            bytes.CopyTo(fourBytes[(4 - bytes.Length)..]);
            return BinaryPrimitives.ReadInt32BigEndian(fourBytes);
        }

        public static ulong ReadEthUInt64(this Span<byte> bytes)
        {
            return ReadEthUInt64((ReadOnlySpan<byte>)bytes);
        }

        public static ulong ReadEthUInt64(this ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 8)
            {
                bytes = bytes.Slice(bytes.Length - 8, 8);
            }

            if (bytes.Length == 8)
            {
                return BinaryPrimitives.ReadUInt64BigEndian(bytes);
            }

            Span<byte> eightBytes = stackalloc byte[8];
            bytes.CopyTo(eightBytes[(8 - bytes.Length)..]);
            return BinaryPrimitives.ReadUInt64BigEndian(eightBytes);
        }

        public static BigInteger ToSignedBigInteger(this byte[] bytes, int byteLength)
        {
            if (bytes.Length == byteLength)
            {
                return new BigInteger(bytes.AsSpan(), false, true);
            }

            Debug.Assert(bytes.Length <= byteLength,
                $"{nameof(ToSignedBigInteger)} expects {nameof(byteLength)} parameter to be less than length of the {bytes}");
            bool needToExpand = bytes.Length != byteLength;
            byte[] bytesToUse = needToExpand ? new byte[byteLength] : bytes;
            if (needToExpand)
            {
                Buffer.BlockCopy(bytes, 0, bytesToUse, byteLength - bytes.Length, bytes.Length);
            }

            byte[] signedResult = new byte[byteLength];
            for (int i = 0; i < byteLength; i++)
            {
                signedResult[byteLength - i - 1] = bytesToUse[i];
            }

            return new BigInteger(signedResult);
        }

        public static UInt256 ToUInt256(this byte[] bytes)
        {
            return new(bytes, true);
        }

        private static byte Reverse(byte b)
        {
            b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
            b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
            b = (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
            return b;
        }

        public static byte[] ToBytes(this BitArray bits)
        {
            if (bits.Length % 8 != 0)
            {
                throw new ArgumentException(nameof(bits));
            }

            byte[] bytes = new byte[bits.Length / 8];
            bits.CopyTo(bytes, 0);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Reverse(bytes[i]);
            }

            return bytes;
        }

        public static string ToBitString(this BitArray bits)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < bits.Count; i++)
            {
                char c = bits[i] ? '1' : '0';
                sb.Append(c);
            }

            return sb.ToString();
        }

        public static BitArray ToBigEndianBitArray256(this Span<byte> bytes)
        {
            byte[] inverted = new byte[32];
            int startIndex = 32 - bytes.Length;
            for (int i = startIndex; i < inverted.Length; i++)
            {
                inverted[i] = Reverse(bytes[i - startIndex]);
            }

            return new BitArray(inverted);
        }

        public static string ToHexString(this byte[] bytes)
        {
            return ByteArrayToHexViaLookup32(bytes, false, false, false);
        }

        public static void StreamHex(this byte[] bytes, TextWriter streamWriter)
        {
            bytes.AsSpan().StreamHex(streamWriter);
        }

        public static void StreamHex(this Span<byte> bytes, TextWriter streamWriter)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                uint val = Lookup32[bytes[i]];
                streamWriter.Write((char)val);
                streamWriter.Write((char)(val >> 16));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ToHexString(this byte[] bytes, bool withZeroX, bool noLeadingZeros = false, bool withEip55Checksum = false) =>
            ByteArrayToHexViaLookup32(bytes, withZeroX, noLeadingZeros, withEip55Checksum);

        private readonly struct StateSmall
        {
            public StateSmall(byte[] bytes, bool withZeroX)
            {
                Bytes = bytes;
                WithZeroX = withZeroX;
            }

            public readonly byte[] Bytes;
            public readonly bool WithZeroX;
        }

        private readonly struct StateSmallMemory
        {
            public StateSmallMemory(Memory<byte> bytes, bool withZeroX)
            {
                Bytes = bytes;
                WithZeroX = withZeroX;
            }

            public readonly Memory<byte> Bytes;
            public readonly bool WithZeroX;
        }

        private struct StateOld
        {
            public StateOld(byte[] bytes, int leadingZeros, bool withZeroX, bool withEip55Checksum)
            {
                Bytes = bytes;
                LeadingZeros = leadingZeros;
                WithZeroX = withZeroX;
                WithEip55Checksum = withEip55Checksum;
            }

            public int LeadingZeros;
            public byte[] Bytes;
            public bool WithZeroX;
            public bool WithEip55Checksum;
        }

        private readonly struct State
        {
            public State(byte[] bytes, int leadingZeros, bool withZeroX)
            {
                Bytes = bytes;
                LeadingZeros = leadingZeros;
                WithZeroX = withZeroX;
            }

            public readonly byte[] Bytes;
            public readonly int LeadingZeros;
            public readonly bool WithZeroX;
        }

        [DebuggerStepThrough]
        public static string ByteArrayToHexViaLookup32Safe(byte[] bytes, bool withZeroX)
        {
            if (bytes.Length == 0)
            {
                return withZeroX ? "0x" : string.Empty;
            }

            int length = bytes.Length * 2 + (withZeroX ? 2 : 0);
            StateSmall stateToPass = new(bytes, withZeroX);

            return string.Create(length, stateToPass, static (chars, state) =>
            {
                ref char charsRef = ref MemoryMarshal.GetReference(chars);

                byte[] bytes = state.Bytes;
                if (bytes.Length == 0)
                {
                    if (state.WithZeroX)
                    {
                        chars[1] = 'x';
                        chars[0] = '0';
                    }

                    return;
                }

                OutputBytesToCharHex(ref bytes[0], state.Bytes.Length, ref charsRef, state.WithZeroX, leadingZeros: 0);
            });
        }

        [DebuggerStepThrough]
        private static string ByteArrayToHexViaLookup32(byte[] bytes, bool withZeroX, bool skipLeadingZeros,
            bool withEip55Checksum)
        {
            int leadingZerosFirstCheck = skipLeadingZeros ? CountLeadingNibbleZeros(bytes) : 0;
            int length = bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZerosFirstCheck;
            if (skipLeadingZeros && length == (withZeroX ? 2 : 0))
            {
                return withZeroX ? ZeroHexValue : ZeroValue;
            }

            State stateToPass = new(bytes, leadingZerosFirstCheck, withZeroX);

            return withEip55Checksum
                ? ByteArrayToHexViaLookup32Checksum(length, stateToPass)
                : string.Create(length, stateToPass, static (chars, state) =>
                {
                    int skip = state.LeadingZeros / 2;
                    byte[] bytes = state.Bytes;
                    if (bytes.Length == 0)
                    {
                        if (state.WithZeroX)
                        {
                            chars[1] = 'x';
                            chars[0] = '0';
                        }

                        return;
                    }

                    ref byte input = ref Unsafe.Add(ref bytes[0], skip);
                    ref char charsRef = ref MemoryMarshal.GetReference(chars);
                    OutputBytesToCharHex(ref input, state.Bytes.Length, ref charsRef, state.WithZeroX, state.LeadingZeros);
                });
        }

        public static void OutputBytesToByteHex(this Span<byte> bytes, Span<byte> hex, bool extraNibble)
        {
            ((ReadOnlySpan<byte>)bytes).OutputBytesToByteHex(hex, extraNibble);
        }

        public static void OutputBytesToByteHex(this ReadOnlySpan<byte> bytes, Span<byte> hex, bool extraNibble)
        {
            int toProcess = bytes.Length;
            if (hex.Length != (toProcess * 2) - (extraNibble ? 1 : 0))
            {
                ThrowArgumentOutOfRangeException();
            }

            ref byte input = ref MemoryMarshal.GetReference(bytes);
            ref ushort lookup32 = ref Lookup16[0];
            ref ushort output = ref Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(hex));
            if (extraNibble)
            {
                // Odd number of hex bytes, handle the first
                // seperately so loop can work in pairs
                ushort val = Unsafe.Add(ref lookup32, input);
                Unsafe.As<ushort, byte>(ref output) = (byte)(val >> 8);

                output = ref Unsafe.AddByteOffset(ref output, 1);
                input = ref Unsafe.Add(ref input, 1);
                toProcess--;
            }

            while (toProcess >= 8)
            {
                output = Unsafe.Add(ref lookup32, input);
                Unsafe.Add(ref output, 1) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 1));
                Unsafe.Add(ref output, 2) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 2));
                Unsafe.Add(ref output, 3) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 3));
                Unsafe.Add(ref output, 4) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 4));
                Unsafe.Add(ref output, 5) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 5));
                Unsafe.Add(ref output, 6) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 6));
                Unsafe.Add(ref output, 7) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 7));

                output = ref Unsafe.Add(ref output, 8);
                input = ref Unsafe.Add(ref input, 8);

                toProcess -= 8;
            }

            while (toProcess > 0)
            {
                output = Unsafe.Add(ref lookup32, input);

                output = ref Unsafe.Add(ref output, 1);
                input = ref Unsafe.Add(ref input, 1);

                toProcess -= 1;
            }

            [DoesNotReturn]
            static void ThrowArgumentOutOfRangeException()
            {
                throw new ArgumentOutOfRangeException();
            }
        }

        public static void OutputBytesToCharHex(ref byte input, int length, ref char charsRef, bool withZeroX, int leadingZeros)
        {
            if (withZeroX)
            {
                charsRef = '0';
                Unsafe.Add(ref charsRef, 1) = 'x';
                charsRef = ref Unsafe.Add(ref charsRef, 2);
            }

            int skip = leadingZeros / 2;
            if ((leadingZeros & 1) != 0)
            {
                skip++;
                // Odd number of hex chars, handle the first
                // seperately so loop can work in pairs
                uint val = Unsafe.Add(ref Lookup32[0], input);
                charsRef = (char)(val >> 16);

                charsRef = ref Unsafe.Add(ref charsRef, 1);
                input = ref Unsafe.Add(ref input, 1);
            }

            int toProcess = length - skip;
            if ((AdvSimd.Arm64.IsSupported || Ssse3.IsSupported) && toProcess >= 4)
            {
                // From HexConvertor.EncodeToUtf16_Vector128 in dotnet/runtime however that isn't exposed
                // in an accessible api that will give the lowercase output directly
                Vector128<byte> shuffleMask = Vector128.Create(
                    0xFF, 0xFF, 0, 0xFF, 0xFF, 0xFF, 1, 0xFF,
                    0xFF, 0xFF, 2, 0xFF, 0xFF, 0xFF, 3, 0xFF);

                Vector128<byte> asciiTable = Vector128.Create((byte)'0', (byte)'1', (byte)'2', (byte)'3',
                    (byte)'4', (byte)'5', (byte)'6', (byte)'7',
                    (byte)'8', (byte)'9', (byte)'a', (byte)'b',
                    (byte)'c', (byte)'d', (byte)'e', (byte)'f');

                nuint pos = 0;
                Debug.Assert(toProcess >= 4);

                // it's used to ensure we can process the trailing elements in the same SIMD loop (with possible overlap)
                // but we won't double compute for any evenly divisible by 4 length since we
                // compare pos > lengthSubVector128 rather than pos >= lengthSubVector128
                nuint lengthSubVector128 = (nuint)toProcess - (nuint)Vector128<int>.Count;
                ref byte destRef = ref Unsafe.As<char, byte>(ref charsRef);
                do
                {
                    // Read 32bits from "bytes" span at "pos" offset
                    uint block = Unsafe.ReadUnaligned<uint>(
                        ref Unsafe.Add(ref input, pos));

                    // TODO: Remove once cross-platform Shuffle is landed
                    // https://github.com/dotnet/runtime/issues/63331
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> mask)
                    {
                        if (Ssse3.IsSupported)
                        {
                            return Ssse3.Shuffle(value, mask);
                        }
                        else if (!AdvSimd.Arm64.IsSupported)
                        {
                            ThrowHelper.ThrowNotSupportedException();
                        }

                        return AdvSimd.Arm64.VectorTableLookup(value, mask);
                    }

                    // Calculate nibbles
                    Vector128<byte> lowNibbles = Shuffle(
                        Vector128.CreateScalarUnsafe(block).AsByte(), shuffleMask);

                    // ExtractVector128 is not entirely the same as ShiftRightLogical128BitLane, but it works here since
                    // first two bytes in lowNibbles are guaranteed to be zeros
                    Vector128<byte> shifted = Sse2.IsSupported ? Sse2.ShiftRightLogical128BitLane(lowNibbles, 2) : AdvSimd.ExtractVector128(lowNibbles, lowNibbles, 2);

                    Vector128<byte> highNibbles = Vector128.ShiftRightLogical(shifted.AsInt32(), 4).AsByte();

                    // Lookup the hex values at the positions of the indices
                    Vector128<byte> indices = (lowNibbles | highNibbles) & Vector128.Create((byte)0xF);
                    Vector128<byte> hex = Shuffle(asciiTable, indices);

                    // The high bytes (0x00) of the chars have also been converted
                    // to ascii hex '0', so clear them out.
                    hex &= Vector128.Create((ushort)0xFF).AsByte();
                    hex.StoreUnsafe(ref destRef, pos * 4); // we encode 4 bytes as a single char (0x0-0xF)
                    pos += (nuint)Vector128<int>.Count;

                    if (pos == (nuint)toProcess)
                    {
                        return;
                    }

                    // Overlap with the current chunk for trailing elements
                    if (pos > lengthSubVector128)
                    {
                        pos = lengthSubVector128;
                    }
                } while (true);
            }
            else
            {
                ref uint lookup32 = ref Lookup32[0];
                ref uint output = ref Unsafe.As<char, uint>(ref charsRef);
                while (toProcess >= 8)
                {
                    output = Unsafe.Add(ref lookup32, input);
                    Unsafe.Add(ref output, 1) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 1));
                    Unsafe.Add(ref output, 2) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 2));
                    Unsafe.Add(ref output, 3) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 3));
                    Unsafe.Add(ref output, 4) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 4));
                    Unsafe.Add(ref output, 5) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 5));
                    Unsafe.Add(ref output, 6) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 6));
                    Unsafe.Add(ref output, 7) = Unsafe.Add(ref lookup32, Unsafe.Add(ref input, 7));

                    output = ref Unsafe.Add(ref output, 8);
                    input = ref Unsafe.Add(ref input, 8);

                    toProcess -= 8;
                }

                while (toProcess > 0)
                {
                    output = Unsafe.Add(ref lookup32, input);

                    output = ref Unsafe.Add(ref output, 1);
                    input = ref Unsafe.Add(ref input, 1);

                    toProcess -= 1;
                }
            }
        }

        private static string ByteArrayToHexViaLookup32Checksum(int length, State stateToPass)
        {
            return string.Create(length, stateToPass, static (chars, state) =>
            {
                // this path is rarely used - only in wallets
                byte[] bytesArray = state.Bytes;
                string hashHex = Keccak.Compute(bytesArray.ToHexString(false)).ToString(false);
                Span<byte> bytes = bytesArray;

                if (state.WithZeroX)
                {
                    chars[1] = 'x';
                    chars[0] = '0';
                    chars = chars[2..];
                }

                bool odd = state.LeadingZeros % 2 == 1;
                int oddity = odd ? 1 : 0;

                uint[] lookup32 = Lookup32;
                for (int i = 0; i < chars.Length; i += 2)
                {
                    uint val = lookup32[bytes[(i + state.LeadingZeros) / 2]];
                    if (i != 0 || !odd)
                    {
                        char char1 = (char)val;
                        chars[i - oddity] =
                            char.IsLetter(char1) && hashHex![i] > '7'
                                ? char.ToUpper(char1)
                                : char1;
                    }

                    char char2 = (char)(val >> 16);
                    chars[i + 1 - oddity] =
                        char.IsLetter(char2) && hashHex![i + 1] > '7'
                            ? char.ToUpper(char2)
                            : char2;
                }
            });
        }

        internal static uint[] Lookup32 = CreateLookup32("x2");
        internal static ushort[] Lookup16 = CreateLookup16("x2");

        private static ushort[] CreateLookup16(string format)
        {
            ushort[] result = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString(format);
                result[i] = (ushort)(s[0] + (s[1] << 8));
            }

            return result;
        }

        private static uint[] CreateLookup32(string format)
        {
            uint[] result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString(format);
                result[i] = s[0] + ((uint)s[1] << 16);
            }

            return result;
        }

        public static int CountLeadingNibbleZeros(this ReadOnlySpan<byte> bytes)
        {
            int firstNonZero = bytes.IndexOfAnyExcept((byte)0);
            if (firstNonZero < 0)
            {
                return bytes.Length * 2;
            }

            int leadingZeros = firstNonZero * 2;
            if ((bytes[firstNonZero] & 0b1111_0000) == 0)
            {
                leadingZeros++;
            }

            return leadingZeros;
        }

        public static int CountZeros(this Span<byte> data)
            => CountZeros((ReadOnlySpan<byte>)data);

        public static int CountZeros(this ReadOnlySpan<byte> data)
        {
            int totalZeros = 0;
            if (Vector512.IsHardwareAccelerated && data.Length >= Vector512<byte>.Count)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(data);
                int i = 0;
                for (; i < data.Length - Vector512<byte>.Count; i += Vector512<byte>.Count)
                {
                    Vector512<byte> dataVector = Unsafe.ReadUnaligned<Vector512<byte>>(ref Unsafe.Add(ref bytes, i));
                    ulong flags = Vector512.Equals(dataVector, default).ExtractMostSignificantBits();
                    totalZeros += BitOperations.PopCount(flags);
                }

                data = data[i..];
            }
            if (Vector256.IsHardwareAccelerated && data.Length >= Vector256<byte>.Count)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(data);
                int i = 0;
                for (; i < data.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
                {
                    Vector256<byte> dataVector = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref bytes, i));
                    uint flags = Vector256.Equals(dataVector, default).ExtractMostSignificantBits();
                    totalZeros += BitOperations.PopCount(flags);
                }

                data = data[i..];
            }
            if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(data);
                int i = 0;
                for (; i < data.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
                {
                    Vector128<byte> dataVector = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), i));
                    uint flags = Vector128.Equals(dataVector, default).ExtractMostSignificantBits();
                    totalZeros += BitOperations.PopCount(flags);
                }

                data = data[i..];
            }

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0)
                {
                    totalZeros++;
                }
            }

            return totalZeros;
        }

        [DebuggerStepThrough]
        public static byte[] FromUtf8HexString(scoped ReadOnlySpan<byte> hexString)
        {
            if (hexString.Length == 0)
            {
                return [];
            }

            int oddMod = hexString.Length % 2;
            byte[] result = GC.AllocateUninitializedArray<byte>((hexString.Length >> 1) + oddMod);
            FromUtf8HexString(hexString, result);
            return result;
        }

        [DebuggerStepThrough]
        public static void FromUtf8HexString(ReadOnlySpan<byte> hexString, Span<byte> result)
        {
            int oddMod = hexString.Length % 2;
            int length = (hexString.Length >> 1) + oddMod;
            if (length != result.Length)
            {
                ThrowInvalidOperationException();
            }

            if (!HexConverter.TryDecodeFromUtf8(hexString, result))
            {
                ThrowFormatException_IncorrectHexString();
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowFormatException_IncorrectHexString()
        {
            throw new FormatException("Incorrect hex string");
        }

        [DebuggerStepThrough]
        public static byte[] FromHexString(string hexString, int length) =>
            hexString is null ? throw new ArgumentNullException(nameof(hexString)) : FromHexString(hexString.AsSpan(), length);

        [DebuggerStepThrough]
        public static byte[] FromHexString(ReadOnlySpan<char> hexString, int length)
        {
            int start = hexString is ['0', 'x', ..] ? 2 : 0;
            ReadOnlySpan<char> chars = hexString[start..];

            if (chars.Length == 0)
            {
                return [];
            }

            int oddMod = hexString.Length % 2;
            int actualLength = (chars.Length >> 1) + oddMod;
            byte[] result = GC.AllocateArray<byte>(length);
            Span<byte> writeToSpan = result.AsSpan(length - actualLength);

            bool isSuccess;
            if (oddMod == 0 &&
                BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                chars.Length >= Vector128<ushort>.Count * 2)
            {
                isSuccess = HexConverter.TryDecodeFromUtf16_Vector128(chars, writeToSpan);
            }
            else
            {
                isSuccess = HexConverter.TryDecodeFromUtf16(chars, writeToSpan, oddMod == 1);
            }

            return isSuccess ? result : throw new FormatException("Incorrect hex string");
        }

        [DebuggerStepThrough]
        public static byte[] FromHexString(string hexString) =>
            hexString is null ? throw new ArgumentNullException(nameof(hexString)) : FromHexString(hexString.AsSpan());

        [DebuggerStepThrough]
        private static byte[] FromHexString(ReadOnlySpan<char> hexString)
        {
            int start = hexString is ['0', 'x', ..] ? 2 : 0;
            ReadOnlySpan<char> chars = hexString[start..];

            if (chars.Length == 0)
            {
                return [];
            }

            int oddMod = hexString.Length % 2;
            int actualLength = (chars.Length >> 1) + oddMod;
            byte[] result = GC.AllocateUninitializedArray<byte>(actualLength);

            bool isSuccess;
            if (oddMod == 0 &&
                BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                chars.Length >= Vector128<ushort>.Count * 2)
            {
                isSuccess = HexConverter.TryDecodeFromUtf16_Vector128(chars, result);
            }
            else
            {
                isSuccess = HexConverter.TryDecodeFromUtf16(chars, result, oddMod == 1);
            }

            return isSuccess ? result : throw new FormatException("Incorrect hex string");
        }

        public static void ChangeEndianness8(Span<byte> bytes)
        {
            if (bytes.Length % 16 != 0)
            {
                throw new NotImplementedException("Has to be a multiple of 16");
            }

            Span<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(bytes);
            for (int i = 0; i < ulongs.Length / 2; i++)
            {
                ulong ith = ulongs[i];
                ulong endIth = ulongs[^(i + 1)];
                (ulongs[i], ulongs[^(i + 1)]) =
                    (BinaryPrimitives.ReverseEndianness(endIth), BinaryPrimitives.ReverseEndianness(ith));
            }
        }

        public static string ToCleanUtf8String(this ReadOnlySpan<byte> bytes)
        {
            // The maximum number of UTF-16 chars is bytes.Length, but each Rune can be up to 2 chars.
            // So we allocate bytes.Length to bytes.Length * 2 chars.
            const int maxOutputChars = 32 * 2;

            if (bytes.IsEmpty || bytes.Length > 32)
                return string.Empty;

            // Allocate a char buffer on the stack.
            char[]? charsArray = null;
            Span<char> outputBuffer = stackalloc char[maxOutputChars];

            int outputPos = 0;
            int index = 0;
            bool hasValidContent = false;
            bool shouldAddSpace = false;

            while (index < bytes.Length)
            {
                ReadOnlySpan<byte> span = bytes[index..];

                OperationStatus status = Rune.DecodeFromUtf8(span, out Rune rune, out var bytesConsumed);
                if (status == OperationStatus.Done)
                {
                    if (!IsControlCharacter(rune))
                    {
                        if (shouldAddSpace)
                        {
                            outputBuffer[outputPos++] = ' ';
                            shouldAddSpace = false;
                        }

                        int charsNeeded = rune.Utf16SequenceLength;
                        if (outputPos + charsNeeded > outputBuffer.Length)
                        {
                            // Expand output buffer
                            int newSize = outputBuffer.Length * 2;
                            char[] newBuffer = ArrayPool<char>.Shared.Rent(newSize);
                            outputBuffer[..outputPos].CopyTo(newBuffer);
                            outputBuffer = newBuffer;
                            if (charsArray is not null)
                            {
                                ArrayPool<char>.Shared.Return(charsArray);
                            }
                            charsArray = newBuffer;
                        }

                        rune.EncodeToUtf16(outputBuffer[outputPos..]);
                        outputPos += charsNeeded;
                        hasValidContent = true;
                    }
                    else
                    {
                        // Control character encountered; set flag to add space if needed
                        shouldAddSpace |= hasValidContent;
                    }
                    index += bytesConsumed;
                }
                else if (status == OperationStatus.NeedMoreData)
                {
                    // Incomplete sequence at the end; break out of the loop
                    break;
                }
                else
                {
                    // Unexpected status; treat as invalid data
                    shouldAddSpace |= hasValidContent;
                    index++;
                }
            }

            // Create the final string from the output buffer.
            string outputString = outputPos > 0 ? new string(outputBuffer[..outputPos]) : string.Empty;
            if (charsArray is not null)
            {
                ArrayPool<char>.Shared.Return(charsArray);
            }

            return outputString;
        }

        private static bool IsControlCharacter(Rune rune)
        {
            // Control characters are U+0000 to U+001F and U+007F to U+009F
            return rune.Value <= 0x001F || (rune.Value >= 0x007F && rune.Value <= 0x009F);
        }

    }
}
