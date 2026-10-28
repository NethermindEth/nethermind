// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions;

public static class Hash256Extensions
{
    /// <summary>Interprets the hash bytes as an unsigned big-endian integer.</summary>
    public static UInt256 ToUInt256(this Hash256 hash) => hash.ValueHash256.ToUInt256();

    public static ValueHash256 IncrementPath(this in ValueHash256 hash)
    {
        ValueHash256 result = hash;
        Span<byte> bytes = result.BytesAsSpan;

        for (int i = 31; i >= 0; i--)
        {
            if (bytes[i] < 0xFF)
            {
                bytes[i]++;
                return result;
            }
            bytes[i] = 0x00;
        }

        // Overflow - return max (shouldn't happen in practice)
        result = ValueKeccak.Zero;
        result.BytesAsSpan.Fill(0xFF);
        return result;
    }

    public static ValueHash256 DecrementPath(this in ValueHash256 hash)
    {
        ValueHash256 result = hash;
        Span<byte> bytes = result.BytesAsSpan;

        for (int i = 31; i >= 0; i--)
        {
            if (bytes[i] > 0)
            {
                bytes[i]--;
                return result;
            }
            bytes[i] = 0xFF;
        }

        // Underflow - return zero (shouldn't happen in practice)
        return ValueKeccak.Zero;
    }
}
