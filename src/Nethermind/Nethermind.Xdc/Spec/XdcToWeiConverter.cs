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
/// Reads a reward stated in XDC — <c>63.42</c>, <c>1</c>, <c>2.5e1</c> — and yields the amount in wei.
/// </summary>
/// <remarks>
/// The reference client decodes these fields into a float64 and scales them by 10^18 through a
/// <c>big.Float</c> whose precision defaults to 64 bits, then truncates toward zero. Both steps are
/// load-bearing: <c>63.42</c> has to produce 63420000000000001704 — the value Apothem's genesis was
/// generated with — and neither <see cref="double"/> nor <see cref="decimal"/> arithmetic lands
/// there. So the exact product is formed in <see cref="BigInteger"/> and rounded explicitly.
/// <para>
/// The 64 comes from Go raising a zero-precision <c>big.Float</c> to that width, which only survives
/// into the product while the receiver's precision is unset — the detail that would silently change
/// the result if the reference restructured that expression. <c>Reward</c>, the pre-<c>TIPUpgradeReward</c>
/// equivalent, is likewise a whole-XDC value scaled by <c>Unit.Ether</c> in <see cref="XdcRewardCalculator"/>.
/// </para>
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

    /// <summary>
    /// Upper bound on a reward, well above the ~3.75e10 total XDC supply and so far above any real
    /// value that only a mistake reaches it.
    /// </summary>
    /// <remarks>
    /// Guards the migration from the previous wei-denominated spelling of these fields. A reward
    /// still stated in wei is at least 10^18 — six orders of magnitude past this bound — so it fails
    /// the load instead of quietly inflating every payout. Also keeps the converted amount at most
    /// 10^30 wei, comfortably inside <see cref="UInt256"/>.
    /// </remarks>
    private const double MaxPlausibleAmountInXdc = 1e12;

    private const int UInt256ByteCount = 32;

    private static readonly UInt256Converter WeiConverter = new();

    public override UInt256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number) ThrowNotANumber(reader.TokenType);

        if (!reader.TryGetDouble(out double amount) || !double.IsFinite(amount) || double.IsNegative(amount))
        {
            ThrowNotAnAmount(in reader);
        }

        if (amount > MaxPlausibleAmountInXdc) ThrowImplausible(in reader);

        return ToWei(amount);
    }

    /// <remarks>
    /// Writes wei rather than XDC. <see cref="V2ConfigParams"/> doubles as the
    /// <c>XDPoS_networkInformation</c> response DTO, whose wire format must not change, and reporting
    /// wei over JSON-RPC while configuring XDC in the chainspec is the split used everywhere else.
    /// The consequence is that a <see cref="V2ConfigParams"/> does not survive a JSON round trip;
    /// nothing performs one.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options) =>
        WeiConverter.Write(writer, value, options);

    private static UInt256 ToWei(double amount)
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

        // At most 10^30 by MaxPlausibleAmountInXdc, so it always fits.
        int byteCount = wei.GetByteCount(isUnsigned: true);
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

    private static string Literal(in Utf8JsonReader reader) =>
        Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotANumber(JsonTokenType tokenType) =>
        throw new JsonException($"An XDC reward must be a JSON number, found {tokenType}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotAnAmount(in Utf8JsonReader reader) =>
        throw new JsonException($"'{Literal(in reader)}' is not an XDC amount");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowImplausible(in Utf8JsonReader reader) =>
        throw new JsonException(
            $"'{Literal(in reader)}' exceeds {MaxPlausibleAmountInXdc:G} XDC. These rewards are stated in XDC, not wei — a value carried over from the wei spelling has to be divided by 10^18");
}
