//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions
{
    public static unsafe partial class Bytes
    {
        public static readonly IEqualityComparer<byte[]> EqualityComparer = new BytesEqualityComparer();

        public static readonly IComparer<byte[]> Comparer = new BytesComparer();

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

        private class BytesComparer : Comparer<byte[]>
        {
            public override int Compare(byte[]? x, byte[]? y)
            {
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
        }

        public static readonly byte[] Zero32 = new byte[32];

        public static readonly byte[] Empty = new byte[0];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetBit(this byte b, int bitNumber)
        {
            return (b & (1 << (7 - bitNumber))) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetBit(this ref byte b, int bitNumber)
        {
            byte mask = (byte) (1 << (7 - bitNumber));
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
        public static bool AreEqual(Span<byte> a1, Span<byte> a2)
        {
            // this works for nulls
            return a1.SequenceEqual(a2);
        }

        public static bool IsZero(this byte[] bytes)
        {
            return IsZero((ReadOnlySpan<byte>)bytes);
        }
        
        public static bool IsZero(this Span<byte> bytes)
        {
            return IsZero((ReadOnlySpan<byte>)bytes);
        }
        
        public static bool IsZero(this ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 32)
            {
                return bytes[31] == 0 && bytes.SequenceEqual(Zero32);
            }

            for (int i = 0; i < bytes.Length / 2; i++)
            {
                if (bytes[i] != 0)
                {
                    return false;
                }

                if (bytes[bytes.Length - i - 1] != 0)
                {
                    return false;
                }
            }

            return bytes.Length % 2 == 0 || bytes[bytes.Length / 2] == 0;
        }

        public static int LeadingZerosCount(this Span<byte> bytes, int startIndex = 0)
        {
            for (int i = startIndex; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    return i - startIndex;
                }
            }

            return bytes.Length - startIndex;
        }

        public static int TrailingZerosCount(this byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[bytes.Length - i - 1] != 0)
                {
                    return i;
                }
            }

            return bytes.Length;
        }

        public static Span<byte> WithoutLeadingZeros(this byte[] bytes)
        {
            return bytes.AsSpan().WithoutLeadingZeros();
        }

        public static Span<byte> WithoutLeadingZeros(this Span<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    return bytes.Slice(i, bytes.Length - i);
                }
            }

            return new byte[] {0};
        }

        public static byte[] Concat(byte prefix, byte[] bytes)
        {
            byte[] result = new byte[1 + bytes.Length];
            result[0] = prefix;
            Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
            return result;
        }

        public static byte[] PadLeft(this byte[] bytes, int length, byte padding = 0)
        {
            return PadLeft(bytes.AsSpan(), length, padding);
        }

        public static byte[] PadLeft(this Span<byte> bytes, int length, byte padding = 0)
        {
            if (bytes.Length == length)
            {
                return bytes.ToArray();
            }

            if (bytes.Length > length)
            {
                return bytes.Slice(0, length).ToArray();
            }

            byte[] result = new byte[length];
            bytes.CopyTo(result.AsSpan().Slice(length - bytes.Length));

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
                return (byte[]) bytes.Clone();
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
            return ToUnsignedBigInteger((ReadOnlySpan<byte>) bytes);
        }

        public static BigInteger ToUnsignedBigInteger(this ReadOnlySpan<byte> bytes)
        {
            return new(bytes, true, true);
        }

        public static uint ReadEthUInt32(this Span<byte> bytes)
        {
            return ReadEthUInt32((ReadOnlySpan<byte>) bytes);
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
            bytes.CopyTo(fourBytes.Slice(4 - bytes.Length));
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
            bytes.CopyTo(fourBytes.Slice(4 - bytes.Length));
            return BinaryPrimitives.ReadUInt32LittleEndian(fourBytes);
        }

        public static int ReadEthInt32(this Span<byte> bytes)
        {
            return ReadEthInt32((ReadOnlySpan<byte>) bytes);
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
            bytes.CopyTo(fourBytes.Slice(4 - bytes.Length));
            return BinaryPrimitives.ReadInt32BigEndian(fourBytes);
        }

        public static ulong ReadEthUInt64(this Span<byte> bytes)
        {
            return ReadEthUInt64((ReadOnlySpan<byte>) bytes);
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
            bytes.CopyTo(eightBytes.Slice(8 - bytes.Length));
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
            b = (byte) ((b & 0xF0) >> 4 | (b & 0x0F) << 4);
            b = (byte) ((b & 0xCC) >> 2 | (b & 0x33) << 2);
            b = (byte) ((b & 0xAA) >> 1 | (b & 0x55) << 1);
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
            return ToHexString(bytes, false, false, false);
        }

        public static void StreamHex(this byte[] bytes, StreamWriter streamWriter)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                uint val = Lookup32[bytes[i]];
                streamWriter.Write((char) val);
                streamWriter.Write((char) (val >> 16));
            }
        }

        public static string ToHexString(this byte[] bytes, bool withZeroX)
        {
            return ToHexString(bytes, withZeroX, false, false);
        }

        public static string ToHexString(this byte[] bytes, bool withZeroX, bool noLeadingZeros)
        {
            return ToHexString(bytes, withZeroX, noLeadingZeros, false);
        }

        public static string ToHexString(this byte[] bytes, bool withZeroX, bool noLeadingZeros, bool withEip55Checksum)
        {
            return ByteArrayToHexViaLookup32(bytes, withZeroX, noLeadingZeros, withEip55Checksum);
        }

        private struct StateSmall
        {
            public StateSmall(byte[] bytes, bool withZeroX)
            {
                Bytes = bytes;
                WithZeroX = withZeroX;
            }

            public byte[] Bytes;
            public bool WithZeroX;
        }

        private struct State
        {
            public State(byte[] bytes, int leadingZeros, bool withZeroX, bool withEip55Checksum)
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

        [DebuggerStepThrough]
        public static string ByteArrayToHexViaLookup32Safe(byte[] bytes, bool withZeroX)
        {
            if (bytes.Length == 0)
            {
                return withZeroX ? "0x" : "";
            }

            int length = bytes.Length * 2 + (withZeroX ? 2 : 0);
            StateSmall stateToPass = new(bytes, withZeroX);

            return string.Create(length, stateToPass, (chars, state) =>
            {
                ref var charsRef = ref MemoryMarshal.GetReference(chars);

                if (state.WithZeroX)
                {
                    charsRef = '0';
                    Unsafe.Add(ref charsRef, 1) = 'x';
                    charsRef = ref Unsafe.Add(ref charsRef, 2);
                }

                ref var input = ref state.Bytes[0];
                ref var output = ref Unsafe.As<char, uint>(ref charsRef);

                int toProcess = state.Bytes.Length;

                var lookup32 = Lookup32;
                while (toProcess > 8)
                {
                    output = lookup32[input];
                    Unsafe.Add(ref output, 1) = lookup32[Unsafe.Add(ref input, 1)];
                    Unsafe.Add(ref output, 2) = lookup32[Unsafe.Add(ref input, 2)];
                    Unsafe.Add(ref output, 3) = lookup32[Unsafe.Add(ref input, 3)];
                    Unsafe.Add(ref output, 4) = lookup32[Unsafe.Add(ref input, 4)];
                    Unsafe.Add(ref output, 5) = lookup32[Unsafe.Add(ref input, 5)];
                    Unsafe.Add(ref output, 6) = lookup32[Unsafe.Add(ref input, 6)];
                    Unsafe.Add(ref output, 7) = lookup32[Unsafe.Add(ref input, 7)];

                    output = ref Unsafe.Add(ref output, 8);
                    input = ref Unsafe.Add(ref input, 8);

                    toProcess -= 8;
                }

                while (toProcess > 0)
                {
                    output = lookup32[input];

                    output = ref Unsafe.Add(ref output, 1);
                    input = ref Unsafe.Add(ref input, 1);

                    toProcess -= 1;
                }
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

            State stateToPass = new(bytes, leadingZerosFirstCheck, withZeroX, withEip55Checksum);
            return string.Create(length, stateToPass, (chars, state) =>
            {
                string? hashHex = null;
                bool isWithChecksum = state.WithEip55Checksum; 
                if (isWithChecksum)
                {
                    // this path is rarely used - only in wallets
                    hashHex = Keccak.Compute(state.Bytes.ToHexString(false)).ToString(false);
                }

                int offset0x = 0;
                if (state.WithZeroX)
                {
                    chars[0] = '0';
                    chars[1] = 'x';
                    offset0x += 2;
                }

                bool odd = state.LeadingZeros % 2 == 1;
                int oddity = odd ? 1 : 0;
                int charsLength = chars.Length;
                for (int i = offset0x; i < charsLength; i += 2)
                {
                    uint val = Lookup32[state.Bytes[(i - offset0x + state.LeadingZeros) / 2]];
                    if (i != offset0x || !odd)
                    {
                        char char1 = (char) val;
                        chars[i - oddity] =
                            isWithChecksum && char.IsLetter(char1) && hashHex![i - offset0x] > '7'
                                ? char.ToUpper(char1)
                                : char1;
                    }

                    char char2 = (char) (val >> 16);
                    chars[i + 1 - oddity] =
                        isWithChecksum && char.IsLetter(char2) && hashHex![i + 1 - offset0x] > '7'
                            ? char.ToUpper(char2)
                            : char2;
                }
            });
        }

        private static uint[] Lookup32 = CreateLookup32("x2");

        private static uint[] CreateLookup32(string format)
        {
            uint[] result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString(format);
                result[i] = s[0] + ((uint) s[1] << 16);
            }

            return result;
        }

        private static int CountLeadingZeros(byte[] bytes)
        {
            int leadingZeros = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if ((bytes[i] & 240) == 0)
                {
                    leadingZeros++;
                    if ((bytes[i] & 15) == 0)
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
        public static byte[] FromHexStringOld(string? hexString)
        {
            if (hexString is null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            if (hexString.Length % 2 == 1)
            {
                hexString = hexString.Insert(startIndex, "0");
            }

            int numberChars = hexString.Length - startIndex;

            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hexString.Substring(i + startIndex, 2), 16);
            }

            return bytes;
        }

        private static byte[] FromHexNibble1Table =
        {
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 0, 16,
            32, 48, 64, 80, 96, 112, 128, 144, 255, 255,
            255, 255, 255, 255, 255, 160, 176, 192, 208, 224,
            240, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 160, 176, 192,
            208, 224, 240
        };

        private static byte[] FromHexNibble2Table =
        {
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 0, 1,
            2, 3, 4, 5, 6, 7, 8, 9, 255, 255,
            255, 255, 255, 255, 255, 10, 11, 12, 13, 14,
            15, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 10, 11, 12,
            13, 14, 15
        };

        [DebuggerStepThrough]
        public static byte[] FromHexString(string hexString)
        {
            if (hexString == null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            bool odd = hexString.Length % 2 == 1;
            int numberChars = hexString.Length - startIndex + (odd ? 1 : 0);
            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
            {
                if (odd && i == 0)
                {
                    bytes[0] += FromHexNibble2Table[(byte) hexString[startIndex]];
                }
                else if (odd)
                {
                    bytes[i / 2] += FromHexNibble1Table[(byte) hexString[i + startIndex - 1]];
                    bytes[i / 2] += FromHexNibble2Table[(byte) hexString[i + startIndex]];
                }
                else
                {
                    bytes[i / 2] += FromHexNibble1Table[(byte) hexString[i + startIndex]];
                    bytes[i / 2] += FromHexNibble2Table[(byte) hexString[i + startIndex + 1]];
                }
            }

            return bytes;
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
        public static int GetSimplifiedHashCode(this Span<byte> bytes)
        {
            const int fnvPrime = 0x01000193;

            if (bytes.Length == 0)
            {
                return 0;
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
