// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Trie
{
    [DebuggerStepThrough]
    public static class Nibbles
    {
        public static Nibble[] FromBytes(params byte[] bytes)
        {
            return FromBytes(bytes.AsSpan());
        }

        public static Nibble[] FromBytes(ReadOnlySpan<byte> bytes)
        {
            Nibble[] nibbles = new Nibble[2 * bytes.Length];
            BytesToNibbleBytes(bytes, MemoryMarshal.AsBytes(nibbles.AsSpan()));
            return nibbles;
        }

        public unsafe static void BytesToNibbleBytes(ReadOnlySpan<byte> bytes, Span<byte> nibbles)
        {
            if (nibbles.Length != 2 * bytes.Length)
            {
                ThrowArgumentException();
            }

            var length = bytes.Length / sizeof(Vector128<byte>) * sizeof(Vector128<byte>);
            if (Vector128.IsHardwareAccelerated && length > 0)
            {
                var input = MemoryMarshal.Cast<byte, Vector128<byte>>(bytes.Slice(0, length));
                ref var output = ref Unsafe.As<byte, Vector128<ushort>>(ref MemoryMarshal.GetReference(nibbles));

                for (int i = 0; i < input.Length; i++)
                {
                    Vector128<byte> value = input[i];
                    (Vector128<ushort> lower0, Vector128<ushort> upper0) = Vector128.Widen(Vector128.BitwiseAnd(value, Vector128.Create((byte)0xf)));
                    lower0 = Vector128.Shuffle(lower0.AsByte(), Vector128.Create((byte)1, 0, 1, 2, 1, 4, 1, 6, 1, 8, 1, 10, 1, 12, 1, 14)).AsUInt16();
                    upper0 = Vector128.Shuffle(upper0.AsByte(), Vector128.Create((byte)1, 0, 1, 2, 1, 4, 1, 6, 1, 8, 1, 10, 1, 12, 1, 14)).AsUInt16();
                    (Vector128<ushort> lower1, Vector128<ushort> upper1) = Vector128.Widen(Vector128.BitwiseAnd(value, Vector128.Create((byte)0xf0)));
                    lower1 = Vector128.ShiftRightLogical(lower1.AsByte(), 4).AsUInt16();
                    upper1 = Vector128.ShiftRightLogical(upper1.AsByte(), 4).AsUInt16();

                    lower0 = Vector128.BitwiseOr(lower0, lower1);
                    upper0 = Vector128.BitwiseOr(upper0, upper1);

                    Unsafe.Add(ref output, i * 2) = lower0;
                    Unsafe.Add(ref output, i * 2 + 1) = upper0;
                }
            }

            for (int i = length; i < bytes.Length; i++)
            {
                int value = Unsafe.Add(ref MemoryMarshal.GetReference(bytes), i);
                Unsafe.Add(ref MemoryMarshal.GetReference(nibbles), i * 2) = (byte)(value >> 4);
                Unsafe.Add(ref MemoryMarshal.GetReference(nibbles), i * 2 + 1) = (byte)(value & 15);
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowArgumentException()
            {
                throw new ArgumentException("Nibbles length must be twice the bytes length");
            }
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

        public static byte ToByte(Nibble highNibble, Nibble lowNibble)
        {
            return (byte)(((byte)highNibble << 4) | (byte)lowNibble);
        }

        public static byte[] ToBytes(ReadOnlySpan<byte> nibbles)
        {
            byte[] bytes = new byte[nibbles.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = ToByte(nibbles[2 * i], nibbles[2 * i + 1]);
            }

            return bytes;
        }

        public static byte[] ToCompactHexEncoding(ReadOnlySpan<byte> nibbles)
        {
            int oddity = nibbles.Length % 2;
            byte[] bytes = new byte[nibbles.Length / 2 + 1];
            for (int i = 0; i < bytes.Length - 1; i++)
            {
                bytes[i + 1] = ToByte(nibbles[2 * i + oddity], nibbles[2 * i + 1 + oddity]);
            }

            if (oddity == 1)
            {
                bytes[0] = ToByte(1, nibbles[0]);
            }

            return bytes;
        }

        public static byte[] EncodePath(ReadOnlySpan<byte> input) => input.Length == 64 ? ToBytes(input) : ToCompactHexEncoding(input);
    }
}
