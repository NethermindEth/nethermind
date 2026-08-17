// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Xdc.Spec;

/// <summary>
/// Reads a reward denominated in XDC — <c>63.42</c>, <c>1</c>, <c>2.5e1</c> — and yields the amount
/// in wei.
/// </summary>
/// <remarks>
/// The reference client decodes these fields into a float64 and scales them by 10^18 through a
/// <c>big.Float</c> carrying the 64-bit significand its wei multiplier is built with, then truncates
/// toward zero. Both steps are load-bearing: <c>63.42</c> has to produce
/// 63420000000000001704 — the value Apothem's genesis was generated with — and neither
/// <see cref="double"/> nor <see cref="decimal"/> arithmetic lands there. So the exact product is
/// formed in <see cref="BigInteger"/> and rounded explicitly.
/// <para>
/// Only a JSON number is accepted. A hex QUANTITY would have to mean wei, and carrying two units on
/// one field is how a reward ends up wrong by a factor of 10^18.
/// </para>
/// <para>
/// Applying this per property keeps the XDC unit convention inside <c>engine.XDPoS.params</c>. The
/// converters registered on <see cref="EthereumJsonSerializer"/> are untouched, so the rest of the
/// chainspec — and JSON-RPC, where EIP-1474 requires a QUANTITY — still rejects a fractional number.
/// </para>
/// </remarks>
public sealed class XdcToWeiConverter : JsonConverter<UInt256>
{
    private const int WeiPerXdcExponent = 18;

    /// <summary>Significand width the reference conversion rounds to.</summary>
    private const int ReferencePrecisionBits = 64;

    private const int UInt256ByteCount = 32;

    public override UInt256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number) ThrowNotANumber(reader.TokenType);

        ReadOnlySpan<byte> literal = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
        if (!reader.TryGetDouble(out double amount) || !double.IsFinite(amount) || amount < 0)
        {
            ThrowNotAnAmount(literal);
        }

        return ToWei(amount, literal);
    }

    /// <remarks>
    /// Not supported: the reverse is lossy, because most wei amounts are not the exact image of any
    /// XDC literal, and emitting the wei value verbatim would read back scaled by 10^18.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(UInt256)} rewards cannot be written back as XDC amounts");

    private static UInt256 ToWei(double amount, ReadOnlySpan<byte> literal)
    {
        if (amount == 0) return default;

        (BigInteger significand, int exponent) = Decompose(amount);

        // The amount is `significand * 2^exponent`, so its wei value is `numerator / 2^shift` exactly.
        BigInteger numerator = significand * BigInteger.Pow(10, WeiPerXdcExponent);
        int shift = -exponent;
        if (shift < 0)
        {
            numerator <<= -shift;
            shift = 0;
        }

        // A value in [2^(e-1), 2^e) keeps bits down to 2^(e-64); `numerator`'s bit length minus the
        // shift is that e, because the divisor is a power of two.
        int ulpExponent = (int)numerator.GetBitLength() - shift - ReferencePrecisionBits;

        BigInteger rounded = ShiftRoundHalfEven(numerator, shift + ulpExponent);
        BigInteger wei = ulpExponent >= 0 ? rounded << ulpExponent : rounded >> -ulpExponent;

        int byteCount = wei.GetByteCount(isUnsigned: true);
        if (byteCount > UInt256ByteCount) ThrowNotAnAmount(literal);

        Span<byte> bytes = stackalloc byte[UInt256ByteCount];
        wei.TryWriteBytes(bytes[(UInt256ByteCount - byteCount)..], out _, isUnsigned: true, isBigEndian: true);

        ReadOnlySpan<byte> bigEndian = bytes;
        return new UInt256(in bigEndian, isBigEndian: true);
    }

    /// <summary>Splits a positive finite <paramref name="value"/> into <c>significand * 2^exponent</c>.</summary>
    private static (BigInteger Significand, int Exponent) Decompose(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biasedExponent = (int)((bits >> 52) & 0x7FF);
        long significand = bits & 0xF_FFFF_FFFF_FFFF;

        return biasedExponent == 0
            ? (significand, -1074)
            : (significand | (1L << 52), biasedExponent - 1075);
    }

    /// <summary>Divides by <c>2^shift</c>, rounding half to even; a negative shift multiplies instead.</summary>
    private static BigInteger ShiftRoundHalfEven(BigInteger value, int shift)
    {
        if (shift <= 0) return value << -shift;

        BigInteger quotient = value >> shift;
        BigInteger remainder = value - (quotient << shift);
        int comparedToHalf = remainder.CompareTo(BigInteger.One << (shift - 1));

        return comparedToHalf > 0 || (comparedToHalf == 0 && !quotient.IsEven)
            ? quotient + BigInteger.One
            : quotient;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotANumber(JsonTokenType tokenType) =>
        throw new JsonException($"An XDC reward must be a JSON number, found {tokenType}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotAnAmount(ReadOnlySpan<byte> literal) =>
        throw new JsonException($"'{Encoding.UTF8.GetString(literal)}' is not an XDC amount representable in wei");
}
