// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie
{
    public static class Nibbles
    {
        private static readonly byte PathPointerOdd = 0xfe;

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
            // Ensure the length of the nibbles span is exactly twice the length of the bytes span.
            if (nibbles.Length != 2 * bytes.Length)
            {
                ThrowArgumentException();
            }

            // Calculate the length to process using SIMD operations.
            var length = bytes.Length / sizeof(Vector128<byte>) * sizeof(Vector128<byte>);
            // Check if SIMD hardware acceleration is available and if there is data to process.
            // This will be branch eliminated the asm if not supported.
            if (Vector128.IsHardwareAccelerated && length > 0)
            {
                // Cast the byte span to a span of Vector128<byte> for SIMD processing.
                var input = MemoryMarshal.Cast<byte, Vector128<byte>>(bytes.Slice(0, length));
                // Cast the nibble span to a reference to first element of Vector128<ushort> as input doubles.
                ref var output = ref Unsafe.As<byte, Vector128<ushort>>(ref MemoryMarshal.GetReference(nibbles));

                for (int i = 0; i < input.Length; i++)
                {
                    // Get the bytes where each byte contains 2 nibbles that we want to move into their own byte.
                    Vector128<byte> value = input[i];

                    // Mask off lower nibble 0x0f and split each byte into two bytes and store them in two separate vectors.
                    (Vector128<ushort> lower0, Vector128<ushort> upper0) = Vector128.Widen(Vector128.BitwiseAnd(value, Vector128.Create((byte)0x0f)));
                    // Arrange the 0x0f nibbles; we use the 1st element to represent 0s, and then order as 0,2,4,6,8,10,12,14th elements.
                    // This leaves byte holes for the other set of nibbles to fill.
                    lower0 = Vector128.Shuffle(lower0.AsByte(), Vector128.Create((byte)1, 0, 1, 2, 1, 4, 1, 6, 1, 8, 1, 10, 1, 12, 1, 14)).AsUInt16();
                    upper0 = Vector128.Shuffle(upper0.AsByte(), Vector128.Create((byte)1, 0, 1, 2, 1, 4, 1, 6, 1, 8, 1, 10, 1, 12, 1, 14)).AsUInt16();

                    // Mask off upper nibble 0xf0 and split each byte into two bytes and store them in two separate vectors.
                    // Widening from byte -> ushort creates byte sized gaps so the two sets can be combined.
                    (Vector128<ushort> lower1, Vector128<ushort> upper1) = Vector128.Widen(Vector128.BitwiseAnd(value, Vector128.Create((byte)0xf0)));
                    // Arrange the 0xf0 nibbles they are already in correct place, but need to be shifted down by a nibble (e.g. >> 4)
                    lower1 = Vector128.ShiftRightLogical(lower1.AsByte(), 4).AsUInt16();
                    upper1 = Vector128.ShiftRightLogical(upper1.AsByte(), 4).AsUInt16();

                    // Combine the two sets of nibbles from the original bytes as their own bytes
                    lower0 = Vector128.BitwiseOr(lower0, lower1);
                    upper0 = Vector128.BitwiseOr(upper0, upper1);

                    // Store the combined nibbles into the output span.
                    Unsafe.Add(ref output, i * 2) = lower0;
                    Unsafe.Add(ref output, i * 2 + 1) = upper0;
                }
            }

            // Process any remaining bytes that were not handled by SIMD.
            for (int i = length; i < bytes.Length; i++)
            {
                // We use Unsafe here as we have verified all the bounds above and also only go to length
                // However the loop doesn't start a 0 and the nibbles span access is complex (rather than just i)
                // so the Jit can't work out if the bounds checks and their if+exceptions can be eliminated.
                // Because of this using regular array style access causes 3 bounds checks to be inserted.
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

        public static byte[] BytesToNibbleBytes(Span<byte> bytes)
        {
            Span<byte> nibbles = stackalloc byte[2 * bytes.Length];
            BytesToNibbleBytes(bytes, nibbles);
            return nibbles.ToArray();
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

        public static byte[] ToEncodedStorageBytes(byte[] nibbles)
        {
            return ToEncodedStorageBytes(nibbles.AsSpan());
        }

        public static byte[] ToEncodedStorageBytes(Span<byte> nibbles)
        {
            int oddity = nibbles.Length % 2;
            byte[] bytes = new byte[nibbles.Length / 2 + oddity + 1];
            for (int i = 0; i < nibbles.Length / 2; i++)
            {
                bytes[i + oddity + 1] = ToByte(nibbles[2 * i + oddity], nibbles[2 * i + 1 + oddity]);
            }
            if (oddity == 1)
                bytes[1] = ToByte(0, nibbles[0]);

            bytes[0] = (byte)(PathPointerOdd | oddity);

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

        [SkipLocalsInit]
        public static byte[] NibblesToByteStorage(Span<byte> nibbles)
        {
            Span<byte> bytes = stackalloc byte[130];

            int ni = 0;
            int bi = 0;
            bool pushZero = false;
            while (ni < nibbles.Length)
            {
                byte highNibble;
                byte lowNibble;

                if (pushZero)
                {
                    highNibble = 0;
                    lowNibble = nibbles[ni];
                    ni++;
                    pushZero = lowNibble == 0;
                }
                else
                {
                    highNibble = nibbles[ni];
                    if (ni + 1 >= nibbles.Length)
                    {
                        lowNibble = 0;
                        ni++;
                    }
                    else if (nibbles[ni + 1] == 0)
                    {
                        lowNibble = 0;
                        pushZero = true;
                        ni += 2;
                    }
                    else
                    {
                        lowNibble = nibbles[ni + 1];
                        ni += 2;
                    }
                }

                bytes[bi++] = ToByte(highNibble, lowNibble);
            }
            if (pushZero)
                bytes[bi++] = 0;

            return bytes.Slice(0, bi).ToArray();
        }

        public static byte[] BytesToNibblesStorage(Span<byte> bytes)
        {
            Span<byte> nibbles = stackalloc byte[bytes.Length * 2];

            byte[] last2Nibbles = new byte[2];
            last2Nibbles[0] = (byte)((bytes[^1] & 240) >> 4);
            last2Nibbles[1] = (byte)(bytes[^1] & 15);

            int ni = 0;
            int bi = 0;
            bool skipZero = false;
            bool lastNibbleSkipped = false;
            while (bi < bytes.Length)
            {
                if (skipZero)
                {
                    nibbles[ni] = (byte)(bytes[bi] & 15);
                    skipZero = nibbles[ni] == 0;
                    ni++;
                    bi++;
                    continue;
                }
                else
                {
                    nibbles[ni] = (byte)((bytes[bi] & 240) >> 4);
                    nibbles[ni + 1] = (byte)(bytes[bi] & 15);
                }

                if (nibbles[ni] == 0 && nibbles[ni + 1] == 0)
                {
                    ni++;
                    lastNibbleSkipped = true;
                }
                else if (nibbles[ni] != 0 && nibbles[ni + 1] == 0)
                {
                    ni += 2;
                    skipZero = true;
                    lastNibbleSkipped = false;
                }
                else
                {
                    ni += 2;
                    lastNibbleSkipped = false;
                }

                bi++;
            }

            int nibblesCount = lastNibbleSkipped ? ni : (last2Nibbles[1] == 0 ? ni - 1 : ni);

            return nibbles.Slice(0, nibblesCount).ToArray();
        }

        public static void NibblesToByteStorage(Span<byte> nibbles, in Span<byte> bytes)
        {
            Debug.Assert(bytes.Length == nibbles.Length / 2 + 1);
            int oddity = nibbles.Length % 2;
            for (int i = 0; i < bytes.Length - 1; i++)
            {
                bytes[i + 1] = ToByte(nibbles[2 * i + oddity], nibbles[2 * i + 1 + oddity]);
            }
            if (oddity == 1)
            {
                bytes[0] = ToByte(1, nibbles[0]);
            }
        }

        public static void BytesToNibblesStorage(Span<byte> bytes, in Span<byte> nibbles)
        {
            Debug.Assert(nibbles.Length == bytes.Length * 2);
            BytesToNibbleBytes(bytes, nibbles);
        }

        public static byte[] CompactToHexEncode(byte[] compactPath)
        {
            if (compactPath.Length == 0)
            {
                return compactPath;
            }
            int nibblesCount = compactPath.Length * 2 + 1;
            byte[] array = ArrayPool<byte>.Shared.Rent(nibblesCount);
            try
            {
                Span<byte> nibbles = array.Slice(0, nibblesCount);
                BytesToNibbleBytes(compactPath, nibbles.Slice(0, 2 * compactPath.Length));
                nibbles[^1] = 16;

                if (nibbles[0] < 2)
                {
                    nibbles = nibbles[..^1];
                }

                int chop = 2 - (nibbles[0] & 1);
                return nibbles[chop..].ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }

        public static Span<byte> IncrementNibble(this Span<byte> nibbles, bool trimHighest = false)
        {
            if (nibbles.Length == 0)
                return nibbles;

            int omitted = 0;

            for (int i = nibbles.Length - 1; i >= 0; i--)
            {
                if (nibbles[i] == 0x0f)
                {
                    nibbles[i] = 0;
                    omitted++;
                    continue;
                }
                else
                {
                    nibbles[i]++;
                    break;
                }
            }
            return trimHighest ?
                nibbles.Slice(0, nibbles.Length - omitted) :
                nibbles;
        }

        public static byte[] EncodePath(ReadOnlySpan<byte> input) => input.Length == 64 ? ToBytes(input) : ToCompactHexEncoding(input);
    }
}
