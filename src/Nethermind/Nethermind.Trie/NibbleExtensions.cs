// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;

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
            for (int i = 0; i < bytes.Length; i++)
            {
                nibbles[i * 2] = new Nibble((byte)((bytes[i] & 240) >> 4));
                nibbles[i * 2 + 1] = new Nibble((byte)(bytes[i] & 15));
            }

            return nibbles;
        }

        public static void BytesToNibbleBytes(Span<byte> bytes, Span<byte> nibbles)
        {
            Debug.Assert(nibbles.Length == 2 * bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                nibbles[i * 2] = (byte)((bytes[i] & 240) >> 4);
                nibbles[i * 2 + 1] = (byte)(bytes[i] & 15);
            }
        }

        public static Nibble[] FromHexString(string hexString)
        {
            if (hexString is null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

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

        public static byte[] ToBytes(byte[] nibbles)
        {
            byte[] bytes = new byte[nibbles.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = ToByte(nibbles[2 * i], nibbles[2 * i + 1]);
            }

            return bytes;
        }

        public static byte[] ToCompactHexEncoding(byte[] nibbles)
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
    }
}
