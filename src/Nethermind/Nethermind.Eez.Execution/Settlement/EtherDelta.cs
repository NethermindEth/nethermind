// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

internal static class EtherDelta
{
    private static readonly UInt256 MaxPositive = (UInt256.One << 191) - 1;

    /// <exception cref="EezSettlementException"><paramref name="value"/> does not fit an <c>int192</c>.</exception>
    public static Int256.Int256 Credit(in UInt256 value) =>
        value <= MaxPositive ? new Int256.Int256(value) : throw new EezSettlementException($"Value {value} does not fit an int192 ether delta.");

    public static Int256.Int256 Debit(in UInt256 value)
    {
        Int256.Int256 credit = Credit(value);
        Int256.Int256.Subtract(Int256.Int256.Zero, credit, out Int256.Int256 debit);
        return debit;
    }
}
