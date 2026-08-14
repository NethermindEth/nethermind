// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Extensions;

public static class Hash256Extensions
{
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
