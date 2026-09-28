// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Trie
{
    [DebuggerStepThrough]
    public static partial class Nibbles
    {
        private const int StackAllocLengthLimit = 255;

        public static Nibble[] FromBytes(params byte[] bytes) => FromBytes(bytes.AsSpan());

        public static Nibble[] FromBytes(ReadOnlySpan<byte> bytes)
        {
            Nibble[] nibbles = new Nibble[2 * bytes.Length];
            BytesToNibbleBytes(bytes, MemoryMarshal.AsBytes(nibbles.AsSpan()));
            return nibbles;
        }

        public static byte[] BytesToNibbleBytes(ReadOnlySpan<byte> bytes)
        {
            byte[] output = new byte[bytes.Length * 2];
            BytesToNibbleBytes(bytes, output);
            return output;
        }

        public static void BytesToNibbleBytes(ReadOnlySpan<byte> bytes, Span<byte> nibbles)
        {
            // Ensure the length of the nibbles span is exactly twice the length of the bytes span.
            if (nibbles.Length != 2 * bytes.Length)
            {
                ThrowArgumentException();
            }

            ExpandNibbles(ref MemoryMarshal.GetReference(bytes), ref MemoryMarshal.GetReference(nibbles), bytes.Length);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowArgumentException() => throw new ArgumentException("Nibbles length must be twice the bytes length");
        }

        public static Nibble[] FromHexString(string hexString)
        {
            ArgumentNullException.ThrowIfNull(hexString);

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            int numberChars = hexString.Length - startIndex;

            Nibble[] nibbles = new Nibble[numberChars];
            for (int i = 0; i < numberChars; i++)
            {
                nibbles[i] = new Nibble(hexString[i + startIndex]);
            }

            return nibbles;
        }

        public static byte[] ToPackedByteArray(this Nibble[] nibbles)
        {
            int oddity = nibbles.Length % 2;
            byte[] bytes = new byte[nibbles.Length / 2 + oddity];
            for (int i = oddity; i < bytes.Length - oddity; i++)
            {
                bytes[i] = ToByte(nibbles[2 * i + oddity], nibbles[2 * i + 1 + oddity]);
            }

            if (oddity == 1)
            {
                bytes[0] = ToByte(0, nibbles[0]);
            }

            return bytes;
        }

        public static byte ToByte(Nibble highNibble, Nibble lowNibble) => (byte)(((byte)highNibble << 4) | (byte)lowNibble);

        public static byte[] ToBytes(ReadOnlySpan<byte> nibbles)
        {
            byte[] bytes = new byte[nibbles.Length / 2];
            PackNibbles(
                ref MemoryMarshal.GetReference(nibbles),
                ref MemoryMarshal.GetArrayDataReference(bytes),
                bytes.Length);

            return bytes;
        }

        [SkipLocalsInit]
        public static byte[] CompactToHexEncode(byte[] compactPath)
        {
            if (compactPath.Length == 0)
            {
                return compactPath;
            }
            int nibblesCount = compactPath.Length * 2 + 1;
            byte[]? array = null;
            Span<byte> nibbles = nibblesCount < StackAllocLengthLimit
                ? stackalloc byte[nibblesCount]
                : array ??= ArrayPool<byte>.Shared.Rent(nibblesCount);

            BytesToNibbleBytes(compactPath, nibbles[..(2 * compactPath.Length)]);
            nibbles[^1] = 16;

            if (nibbles[0] < 2)
            {
                nibbles = nibbles[..^1];
            }

            int chop = 2 - (nibbles[0] & 1);
            byte[] result = nibbles[chop..].ToArray();
            if (array is not null)
            {
                ArrayPool<byte>.Shared.Return(array);
            }

            return result;
        }

        public static byte[] ToCompactHexEncoding(ReadOnlySpan<byte> nibbles)
        {
            int oddity = nibbles.Length % 2;
            byte[] bytes = new byte[nibbles.Length / 2 + 1];
            PackNibbles(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(nibbles), oddity),
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bytes), 1),
                bytes.Length - 1);

            if (oddity == 1)
            {
                bytes[0] = ToByte(1, nibbles[0]);
            }

            return bytes;
        }

        public static byte[] EncodePath(ReadOnlySpan<byte> input) => input.Length == 64 ? ToBytes(input) : ToCompactHexEncoding(input);

        public static byte[] ToCompactHexEncoding(TreePath nibbles)
        {
            int oddity = nibbles.Length % 2;
            byte[] bytes = GC.AllocateUninitializedArray<byte>(nibbles.Length / 2 + 1);
            if (oddity == 0)
            {
                bytes[0] = 0;
                nibbles.Span[..(bytes.Length - 1)].CopyTo(bytes.AsSpan(1));
                return bytes;
            }

            for (int i = 0; i < bytes.Length - 1; i++)
            {
                bytes[i + 1] = ToByte((byte)nibbles[2 * i + oddity], (byte)nibbles[2 * i + 1 + oddity]);
            }

            bytes[0] = ToByte(1, (byte)nibbles[0]);

            return bytes;
        }

        public static byte[] EncodePath(TreePath input) => input.Length == 64 ? ToBytes(input) : ToCompactHexEncoding(input);

        public static byte[] ToBytes(TreePath nibbles)
        {
            int byteLength = nibbles.Length / 2;
            byte[] bytes = GC.AllocateUninitializedArray<byte>(byteLength);
            nibbles.Span[..byteLength].CopyTo(bytes);

            return bytes;
        }
    }
}
