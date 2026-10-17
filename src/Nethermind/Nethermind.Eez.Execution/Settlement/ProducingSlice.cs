// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The effect entries of a batch that L1 applied, as <c>(skip, take)</c> over them. L1 runs a contiguous run of a
/// batch's claimed steps; claimed step 0 is the anchor, which carries no effect, so claimed step <c>i</c> is effect
/// <c>i - 1</c>. A run that starts past the anchor means a competing batch in the same L1 block already made the
/// skipped hops.
/// </summary>
public readonly record struct ProducingSlice(int Skip, int Take)
{
    /// <param name="start">Index of the first claimed step L1 ran.</param>
    /// <param name="length">How many consecutive claimed steps L1 ran; at least one.</param>
    public static ProducingSlice OfSettledRun(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return start == 0 ? new ProducingSlice(0, length - 1) : new ProducingSlice(start - 1, length);
    }
}
