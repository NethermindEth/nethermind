// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace SendBlobs;
internal static class HexConvert
{
    public static UInt256 ToUInt256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();

        if (IsHex(value))
        {
            return new UInt256(Bytes.FromHexString(value));
        }

        return UInt256.Parse(value);
    }

    public static ulong ToUInt64(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();

        if (!IsHex(value))
        {
            return ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        string hexBody = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        if (hexBody.Length <= 16)
        {
            return Convert.ToUInt64(hexBody, 16);
        }

        UInt256 parsed = new(Bytes.FromHexString(value));
        if (parsed > ulong.MaxValue)
        {
            throw new OverflowException($"Value '{value}' exceeds UInt64 range.");
        }

        return (ulong)parsed;
    }

    private static bool IsHex(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
}
