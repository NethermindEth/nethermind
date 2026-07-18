// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Core;

public static class Eip8198Constants
{
    public const ulong OldSlotDurationMs = 12_000;
    public const ulong NewSlotDurationMs = 8_000;

    /// <summary>
    /// Scales the parent gas limit by the slot duration change at the EIP-8198 activation boundary,
    /// clamped to <paramref name="minGasLimit"/>.
    /// </summary>
    /// <remarks>
    /// Computed via <see cref="UInt256"/> because <c>parentGasLimit * slotDurationMs</c> can overflow
    /// <see cref="long"/> for extreme genesis gas limits (e.g. <see cref="long.MaxValue"/> in hive tests).
    /// The result fits in <see cref="long"/> since slot duration only shrinks at activation.
    /// Clamping keeps the transition block subject to the protocol gas limit floor, which the
    /// exact-value check at activation would otherwise bypass.
    /// </remarks>
    public static long ScaleGasLimit(long parentGasLimit, ulong slotDurationMs, ulong parentSlotDurationMs, long minGasLimit) =>
        Math.Max((long)((UInt256)parentGasLimit * slotDurationMs / parentSlotDurationMs), minGasLimit);
}
