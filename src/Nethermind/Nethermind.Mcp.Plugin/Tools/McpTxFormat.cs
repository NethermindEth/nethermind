// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Human-oriented formatting shared by the transaction tools: amounts, addresses, times and gas.</summary>
internal static class McpTxFormat
{
    /// <summary>The number of fraction digits kept by <see cref="Human(string)"/> once the integer part is non-zero.</summary>
    private const int HumanFractionDigits = 4;

    /// <summary>The number of significant fraction digits kept by <see cref="Human(string)"/> for amounts below one.</summary>
    private const int HumanSignificantDigits = 4;

    /// <summary>Formats <paramref name="amount"/> scaled down by 10^<paramref name="decimals"/> exactly, such as <c>1.5</c>, without trailing zeros.</summary>
    public static string Units(in UInt256 amount, int decimals)
    {
        string digits = amount.ToString();
        if (decimals <= 0)
        {
            return digits;
        }

        if (digits.Length <= decimals)
        {
            digits = digits.PadLeft(decimals + 1, '0');
        }

        string integer = digits[..^decimals];
        string fraction = digits[^decimals..].TrimEnd('0');
        return fraction.Length == 0 ? integer : $"{integer}.{fraction}";
    }

    /// <summary>Formats a signed amount scaled by 10^<paramref name="decimals"/>, prefixed with <c>+</c> or <c>-</c> when non-zero.</summary>
    public static string SignedUnits(BigInteger amount, int decimals)
    {
        if (amount.IsZero)
        {
            return "0";
        }

        UInt256 magnitude = (UInt256)BigInteger.Abs(amount);
        return (amount.Sign < 0 ? "-" : "+") + Units(magnitude, decimals);
    }

    /// <summary>Formats wei as ether (or xDAI, which also has 18 decimals).</summary>
    public static string Ether(in UInt256 wei) => Units(wei, 18);

    /// <summary>Formats wei as gwei.</summary>
    public static string Gwei(in UInt256 wei) => Units(wei, 9);

    /// <summary>Formats a quantity as a JSON-RPC hex quantity.</summary>
    public static string Hex(in UInt256 value) => value.ToHexString(true);

    /// <summary>Formats a quantity as a JSON-RPC hex quantity.</summary>
    public static string Hex(ulong value) => ((UInt256)value).ToHexString(true);

    /// <summary>Formats an address with its EIP-55 checksum.</summary>
    public static string Checksum(Address address) => address.ToString(withZeroX: true, withEip55Checksum: true);

    /// <summary>Formats an address in its short form for sentences, such as <c>0xAbCd…1234</c>.</summary>
    public static string Short(Address address)
    {
        string text = Checksum(address);
        return $"{text[..6]}…{text[^4..]}";
    }

    /// <summary>Formats a unix timestamp in seconds as ISO-8601 UTC.</summary>
    public static string Iso(ulong unixSeconds) =>
        unixSeconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? "out of range"
            : DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Formats a count with thousands separators, such as <c>21,000,000</c>.</summary>
    public static string Thousands(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Formats <paramref name="numerator"/>/<paramref name="denominator"/> as a percentage with two decimals.</summary>
    public static double Percent(ulong numerator, ulong denominator) =>
        denominator == 0 ? 0 : Math.Round(100.0 * numerator / denominator, 2);

    /// <summary>
    /// Shortens an exact decimal amount for a sentence: thousands separators, at most four fraction digits for amounts
    /// of one or more and four significant digits below one, such as <c>1,250.5</c> or <c>0.0001234</c>.
    /// </summary>
    public static string Human(string exact)
    {
        int dot = exact.IndexOf('.');
        string integer = dot < 0 ? exact : exact[..dot];
        string fraction = dot < 0 ? string.Empty : exact[(dot + 1)..];

        if (fraction.Length > 0)
        {
            int keep;
            if (integer.TrimStart('0').Length > 0)
            {
                keep = HumanFractionDigits;
            }
            else
            {
                int leadingZeros = fraction.Length - fraction.TrimStart('0').Length;
                keep = leadingZeros + HumanSignificantDigits;
            }

            if (fraction.Length > keep)
            {
                fraction = fraction[..keep];
            }

            fraction = fraction.TrimEnd('0');
        }

        StringBuilder builder = new(integer.Length + integer.Length / 3 + fraction.Length + 1);
        for (int i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0)
            {
                builder.Append(',');
            }

            builder.Append(integer[i]);
        }

        if (fraction.Length > 0)
        {
            builder.Append('.').Append(fraction);
        }

        return builder.ToString();
    }

    /// <summary>Formats a hex string of at most <paramref name="maxBytes"/> bytes from <paramref name="data"/>.</summary>
    public static string HexPrefix(ReadOnlySpan<byte> data, int maxBytes) =>
        "0x" + Convert.ToHexStringLower(data.Length > maxBytes ? data[..maxBytes] : data);
}
