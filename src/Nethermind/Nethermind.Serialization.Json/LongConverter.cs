// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;

namespace Nethermind.Serialization.Json
{
    using Nethermind.Core.Extensions;
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Buffers.Text;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Runtime.Intrinsics;
    using System.Runtime.Intrinsics.X86;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class LongConverter : JsonConverter<long>
    {
        public static long FromString(string s)
        {
            if (s is null)
            {
                throw new JsonException("null cannot be assigned to long");
            }

            if (s == Bytes.ZeroHexValue)
            {
                return 0L;
            }

            if (s.StartsWith("0x0"))
            {
                return long.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier);
            }

            if (s.StartsWith("0x"))
            {
                Span<char> withZero = new(new char[s.Length - 1]);
                withZero[0] = '0';
                s.AsSpan(2).CopyTo(withZero[1..]);
                return long.Parse(withZero, NumberStyles.AllowHexSpecifier);
            }

            return long.Parse(s, NumberStyles.Integer);
        }

        public override long ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            ReadOnlySpan<byte> hex = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
            return FromString(hex);
        }

        public static long FromString(ReadOnlySpan<byte> s)
        {
            if (s.Length == 0)
            {
                throw new JsonException("null cannot be assigned to long");
            }

            if (s.SequenceEqual("0x0"u8))
            {
                return 0L;
            }

            long value;
            if (s.StartsWith("0x"u8))
            {
                s = s[2..];
                if (Utf8Parser.TryParse(s, out value, out _, 'x'))
                {
                    return value;
                }
            }
            else if (Utf8Parser.TryParse(s, out value, out _))
            {
                return value;
            }

            throw new JsonException("hex to long");
        }

        internal static long ReadCore(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetInt64();
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                if (!reader.HasValueSequence)
                {
                    return FromString(reader.ValueSpan);
                }
                else
                {
                    return FromString(reader.ValueSequence.ToArray());
                }
            }

            throw new JsonException();
        }

        public override long Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return ReadCore(ref reader);
        }

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            long value,
            JsonSerializerOptions options)
        {
            switch (ForcedNumberConversion.GetFinalConversion())
            {
                case NumberConversion.Hex:
                    if (value == 0)
                    {
                        writer.WriteStringValue("0x0"u8);
                    }
                    else
                    {
                        WriteHexDirect(writer, (ulong)value);
                    }
                    break;
                case NumberConversion.Decimal:
                    writer.WriteStringValue(value == 0 ? "0" : value.ToString(CultureInfo.InvariantCulture));
                    break;
                case NumberConversion.Raw:
                    writer.WriteNumberValue(value);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        [SkipLocalsInit]
        internal static void WriteHexDirect(Utf8JsonWriter writer, ulong value)
        {
            // Raw JSON output: '"' + "0x" + 16 hex chars + '"' = 20 bytes max
            Span<byte> buf = stackalloc byte[20];
            ref byte b = ref MemoryMarshal.GetReference(buf);

            if (Ssse3.IsSupported)
            {
                // Vectorized: convert all 8 bytes → 16 hex chars in one shot via PSHUFB
                Vector128<byte> hexLookup = Vector128.Create(
                    (byte)'0', (byte)'1', (byte)'2', (byte)'3',
                    (byte)'4', (byte)'5', (byte)'6', (byte)'7',
                    (byte)'8', (byte)'9', (byte)'a', (byte)'b',
                    (byte)'c', (byte)'d', (byte)'e', (byte)'f');

                ulong be = BinaryPrimitives.ReverseEndianness(value);
                Vector128<byte> input = Vector128.CreateScalarUnsafe(be).AsByte();

                // Split each byte into high/low nibbles, interleave, then lookup
                Vector128<byte> hi = Sse2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & Vector128.Create((byte)0x0F);
                Vector128<byte> lo = input & Vector128.Create((byte)0x0F);
                Vector128<byte> nibbles = Sse2.UnpackLow(hi, lo);
                Vector128<byte> hex = Ssse3.Shuffle(hexLookup, nibbles);

                hex.StoreUnsafe(ref Unsafe.Add(ref b, 3));
            }
            else
            {
                WriteHexScalar(ref Unsafe.Add(ref b, 3), value);
            }

            // nibbleCount: ceil(significantBits / 4), guaranteed >= 1 since value != 0
            int nibbleCount = (67 - BitOperations.LeadingZeroCount(value)) >> 2;
            int start = 19 - nibbleCount;

            // Write '"0x' prefix just before first significant hex char
            Unsafe.Add(ref b, start - 3) = (byte)'"';
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, start - 2), (ushort)0x7830); // "0x" LE
            // Closing quote after last hex char
            Unsafe.Add(ref b, 19) = (byte)'"';

            // Hex chars never need JSON escaping — bypass encoder entirely
            writer.WriteRawValue(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref b, start - 3), nibbleCount + 4),
                skipInputValidation: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteHexScalar(ref byte dest, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                int byteVal = (int)(value >> ((7 - i) << 3)) & 0xFF;
                int hi = byteVal >> 4;
                int lo = byteVal & 0xF;
                Unsafe.Add(ref dest, i * 2) = (byte)(hi + 48 + (((9 - hi) >> 31) & 39));
                Unsafe.Add(ref dest, i * 2 + 1) = (byte)(lo + 48 + (((9 - lo) >> 31) & 39));
            }
        }
    }
}
