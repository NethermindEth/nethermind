// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Common;

internal static class HexConvert
{
    public static bool TryParseHash(string s, out Hash256? hash)
    {
        hash = null;
        if (string.IsNullOrEmpty(s) || !s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Length != 66)
        {
            return false;
        }

        try
        {
            hash = new Hash256(Bytes.FromHexString(s));
            return true;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Encodes an SSZ bitvector (LSB-first within each byte) as a <c>0x</c>-prefixed hex string.</summary>
    public static string BitVectorToHex(BitArray bits)
    {
        byte[] bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bytes.ToHexString(withZeroX: true);
    }
}
