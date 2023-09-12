// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        public static readonly ISpanEqualityComparer<byte> SpanEqualityComparer = new SpanBytesEqualityComparer();
        public static readonly ISpanEqualityComparer<byte> SpanNibbleEqualityComparer = new SpanNibbleBytesEqualityComparer();
        public static readonly BytesComparer Comparer = new();

        private class BytesEqualityComparer : EqualityComparer<byte[]>
        {
            public override bool Equals(byte[]? x, byte[]? y)
            {
                return AreEqual(x, y);
            }

            public override int GetHashCode(byte[] obj)
            {
                return obj.GetSimplifiedHashCode();
            }
        }

        private class NullableBytesEqualityComparer : EqualityComparer<byte[]?>
        {
            public override bool Equals(byte[]? x, byte[]? y)
            {
                return AreEqual(x, y);
            }

            public override int GetHashCode(byte[]? obj)
            {
                return obj?.GetSimplifiedHashCode() ?? 0;
            }
        }

        private class SpanBytesEqualityComparer : ISpanEqualityComparer<byte>
        {
            public bool Equals(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y) => AreEqual(x, y);

            public int GetHashCode(ReadOnlySpan<byte> obj) => GetSimplifiedHashCode(obj);
        }

        private class SpanNibbleBytesEqualityComparer : ISpanEqualityComparer<byte>
        {
            public bool Equals(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y) => AreEqual(x, y);

            public int GetHashCode(ReadOnlySpan<byte> obj) => GetSimplifiedHashCodeForNibbles(obj);
        }

        public class BytesComparer : Comparer<byte[]>
        {
            public override int Compare(byte[]? x, byte[]? y)
            {
                if (ReferenceEquals(x, y)) return 0;

                if (x is null)
                {
                    return y is null ? 0 : -1;
                }

                if (y is null)
                {
                    return 1;
                }

                if (x.Length == 0)
                {
                    return y.Length == 0 ? 0 : -1;
                }

                for (int i = 0; i < x.Length; i++)
                {
                    if (y.Length <= i)
                    {
                        return 1;
                    }

                    int result = x[i].CompareTo(y[i]);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return y.Length > x.Length ? -1 : 0;
            }

            public int Compare(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
            {
                if (Unsafe.AreSame(ref MemoryMarshal.GetReference(x), ref MemoryMarshal.GetReference(y)) &&
                    x.Length == y.Length)
                {
                    return 0;
                }

                if (x.Length == 0)
                {
                    return y.Length == 0 ? 0 : -1;
                }

                for (int i = 0; i < x.Length; i++)
                {
                    if (y.Length <= i)
                    {
                        return 1;
                    }

                    int result = x[i].CompareTo(y[i]);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return y.Length > x.Length ? -1 : 0;
            }
        }

        public static readonly byte[] Zero32 = new byte[32];

        public static readonly byte[] Empty = Array.Empty<byte>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetBit(this byte b, int bitNumber)
        {
            return (b & (1 << (7 - bitNumber))) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetBit(this ref byte b, int bitNumber)
        {
            byte mask = (byte)(1 << (7 - bitNumber));
            b = b |= mask;
        }

        public static int GetHighestSetBitIndex(this byte b)
        {
            if ((b & 128) == 128) return 8;
            if ((b & 64) == 64) return 7;
            if ((b & 32) == 32) return 6;
            if ((b & 16) == 16) return 5;
            if ((b & 8) == 8) return 4;
            if ((b & 4) == 4) return 3;
            return (b & 2) == 2 ? 2 : b;
        }

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

        public static Span<byte> WithoutLeadingZeros(this byte[] bytes)
        {
            return bytes.AsSpan().WithoutLeadingZeros();
        }

        public static Span<byte> WithoutLeadingZerosOrEmpty(this byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Array.Empty<byte>();
            return bytes.AsSpan().WithoutLeadingZeros();
        }

        public static Span<byte> WithoutLeadingZerosOrEmpty(this Span<byte> bytes)
        {
            if (bytes.IsNullOrEmpty()) return Array.Empty<byte>();
            return bytes.WithoutLeadingZeros();
        }

        public static Span<byte> WithoutLeadingZeros(this Span<byte> bytes)
        {
            if (bytes.Length == 0) return new byte[] { 0 };

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

        public static byte[] PadLeft(this Span<byte> bytes, int length, byte padding = 0)
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

        public static byte[] Concat(params byte[][] parts)
        {
            int totalLength = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                totalLength += parts[i].Length;
            }

            byte[] result = new byte[totalLength];
            int position = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                Buffer.BlockCopy(parts[i], 0, result, position, parts[i].Length);
                position += parts[i].Length;
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

        public static uint ReadEthUInt32LittleEndian(this Span<byte> bytes)
        {
            if (bytes.Length > 4)
            {
                bytes = bytes.Slice(bytes.Length - 4, 4);
            }

            if (bytes.Length == 4)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            }

            Span<byte> fourBytes = stackalloc byte[4];
            bytes.CopyTo(fourBytes[(4 - bytes.Length)..]);
            return BinaryPrimitives.ReadUInt32LittleEndian(fourBytes);
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
        public static string ByteArrayToHexViaLookup32Safe(Memory<byte> bytes, bool withZeroX)
        {
            if (bytes.Length == 0)
            {
                return withZeroX ? "0x" : string.Empty;
            }

            int length = bytes.Length * 2 + (withZeroX ? 2 : 0);
            StateSmallMemory stateToPass = new(bytes, withZeroX);

            return string.Create(length, stateToPass, static (chars, state) =>
            {
                ref char charsRef = ref MemoryMarshal.GetReference(chars);

                Memory<byte> bytes = state.Bytes;
                if (bytes.Length == 0)
                {
                    if (state.WithZeroX)
                    {
                        chars[1] = 'x';
                        chars[0] = '0';
                    }

                    return;
                }

                OutputBytesToCharHex(ref bytes.Span[0], state.Bytes.Length, ref charsRef, state.WithZeroX, leadingZeros: 0);
            });
        }

        [DebuggerStepThrough]
        private static string ByteArrayToHexViaLookup32(byte[] bytes, bool withZeroX, bool skipLeadingZeros,
            bool withEip55Checksum)
        {
            int leadingZerosFirstCheck = skipLeadingZeros ? CountLeadingZeros(bytes) : 0;
            int length = bytes.Length * 2 + (withZeroX ? 2 : 0) - leadingZerosFirstCheck;
            if (skipLeadingZeros && length == (withZeroX ? 2 : 0))
            {
                return withZeroX ? "0x0" : "0";
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

        internal static void OutputBytesToCharHex(ref byte input, int length, ref char charsRef, bool withZeroX, int leadingZeros)
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
                    Vector128<byte> shifted = Sse2.IsSupported ?
                        Sse2.ShiftRightLogical128BitLane(lowNibbles, 2) :
                        AdvSimd.ExtractVector128(lowNibbles, lowNibbles, 2);

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

        internal static int CountLeadingZeros(ReadOnlySpan<byte> bytes)
        {
            int leadingZeros = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if ((bytes[i] & 0b1111_0000) == 0)
                {
                    leadingZeros++;
                    if ((bytes[i] & 0b1111) == 0)
                    {
                        leadingZeros++;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            return leadingZeros;
        }

        [DebuggerStepThrough]
        public static byte[] FromUtf8HexString(ReadOnlySpan<byte> hexString)
        {
            if (hexString.Length == 0)
            {
                return Array.Empty<byte>();
            }

            int oddMod = hexString.Length % 2;
            byte[] result = GC.AllocateUninitializedArray<byte>((hexString.Length >> 1) + oddMod);
            return HexConverter.TryDecodeFromUtf8(hexString, result, oddMod == 1) ? result : throw new FormatException("Incorrect hex string");
        }

        [DebuggerStepThrough]
        public static byte[] FromHexString(string hexString)
        {
            if (hexString is null)
            {
                throw new ArgumentNullException(nameof(hexString));
            }

            int start = hexString is ['0', 'x', ..] ? 2 : 0;
            ReadOnlySpan<char> chars = hexString.AsSpan(start);

            if (chars.Length == 0)
            {
                return Array.Empty<byte>();
            }

            int oddMod = hexString.Length % 2;
            byte[] result = GC.AllocateUninitializedArray<byte>((chars.Length >> 1) + oddMod);

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

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public static int GetSimplifiedHashCode(this byte[] bytes)
        {
            const int fnvPrime = 0x01000193;

            if (bytes.Length == 0)
            {
                return 0;
            }

            return (fnvPrime * bytes.Length * (((fnvPrime * (bytes[0] + 7)) ^ (bytes[^1] + 23)) + 11)) ^ (bytes[(bytes.Length - 1) / 2] + 53);
        }

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public static int GetSimplifiedHashCode(this ReadOnlySpan<byte> bytes)
        {
            const int fnvPrime = 0x01000193;

            if (bytes.Length == 0)
            {
                return 0;
            }

            return (fnvPrime * bytes.Length * (((fnvPrime * (bytes[0] + 7)) ^ (bytes[^1] + 23)) + 11)) ^ (bytes[(bytes.Length - 1) / 2] + 53);
        }

        public static int GetSimplifiedHashCodeForNibbles(this ReadOnlySpan<byte> bytes)
        {
            const int fnvPrime = 0x01000193;

            if (bytes.Length == 0)
                return 0;

            if (bytes.Length >= 6)
            {
                byte f = (byte)((byte)(bytes[0] << 4) | bytes[1]);
                byte m = (byte)((byte)(bytes[^2] << 4) | bytes[^1]);
                byte l = (byte)((byte)(bytes[(bytes.Length - 1) / 2] << 4) | bytes[(bytes.Length - 1) / 2 + 1]);

                return (fnvPrime * bytes.Length * (((fnvPrime * (f + 7)) ^ (l + 23)) + 11)) ^ (m + 53);
            }

            return (fnvPrime * bytes.Length * (((fnvPrime * (bytes[0] + 7)) ^ (bytes[^1] + 23)) + 11)) ^ (bytes[(bytes.Length - 1) / 2] + 53);
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
    }
}
