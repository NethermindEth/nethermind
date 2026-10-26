// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The delayed L1 history cutoff selected at each aligned L2 timestamp.</summary>
internal sealed class NativeHistorySchedule
{
    internal ulong FirstCutoff { get; }
    internal ulong BlockInterval { get; }
    internal ulong Interval { get; }
    internal ulong Delay { get; }
    internal ulong BootstrapCutoff => FirstCutoff - Interval;

    internal NativeHistorySchedule(ulong firstCutoff, ulong blockInterval, uint positions, uint offset)
    {
        if (blockInterval == 0 || positions < 2 || offset >= positions || blockInterval > ulong.MaxValue / positions)
            throw new ArgumentException("Invalid history update schedule.");
        ulong interval = blockInterval * positions;
        ulong delay = blockInterval * ((ulong)offset + 1);
        if (firstCutoff < interval || firstCutoff > ulong.MaxValue - delay)
            throw new ArgumentException("History bootstrap or first update exceeds the timestamp range.");
        FirstCutoff = firstCutoff;
        BlockInterval = blockInterval;
        Interval = interval;
        Delay = delay;
    }

    internal ulong SourceCutoff(ulong timestamp)
    {
        if (timestamp < FirstCutoff || (timestamp - FirstCutoff) % BlockInterval != 0)
            throw new ArgumentException("L2 timestamp is outside the history schedule.");
        ulong elapsed = timestamp - FirstCutoff;
        return elapsed < Delay ? BootstrapCutoff : FirstCutoff + ((elapsed - Delay) / Interval) * Interval;
    }
}
