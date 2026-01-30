// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace SendBlobs;
internal static class HexConvert
{
    public static ulong ToUInt64(string s)
    {
        // Prefer Nethermind's shared hex parsing logic to avoid duplicating subtle rules.
        if (s.StartsWith("0x", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> hex = s.AsSpan(2);
            if (hex.IsEmpty)
            {
                throw new FormatException("Empty hex value.");
            }

            // Compute decoded byte length (supports odd-length quantities like "0x1").
            int byteLength = (hex.Length >> 1) + (hex.Length & 1);
            if (byteLength > sizeof(ulong))
            {
                throw new OverflowException("Value does not fit into UInt64.");
            }

            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            bytes.Clear();

            Span<byte> target = bytes[^byteLength..];
            Bytes.FromHexString(s.AsSpan(), target);
            return bytes.ReadEthUInt64();
        }

        return Convert.ToUInt64(s, 10);
    }
}
