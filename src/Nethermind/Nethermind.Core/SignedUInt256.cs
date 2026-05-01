// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core;

public readonly record struct SignedUInt256(UInt256 Value, bool IsNegative)
{
    public static SignedUInt256 Zero => new(UInt256.Zero, false);

    public static implicit operator SignedUInt256(UInt256 value) => new(value, false);

    public UInt256 ToUInt256() =>
        IsNegative && Value != UInt256.Zero
            ? throw new OverflowException($"Cannot convert negative SignedUInt256 (-{Value}) to UInt256")
            : Value;

    public static SignedUInt256 operator +(SignedUInt256 a, SignedUInt256 b)
    {
        if (a.IsNegative == b.IsNegative)
        {
            // Same sign: add magnitudes, keep sign
            return new(a.Value + b.Value, a.IsNegative);
        }

        // Different signs: subtract smaller from larger
        if (a.Value >= b.Value)
            return new(a.Value - b.Value, a.IsNegative);

        return new(b.Value - a.Value, b.IsNegative);
    }

    public static SignedUInt256 Negate(SignedUInt256 value) =>
        value.Value == UInt256.Zero ? value : new(value.Value, !value.IsNegative);

    public override string ToString() =>
        IsNegative && Value != UInt256.Zero ? $"-{Value}" : Value.ToString();
}
