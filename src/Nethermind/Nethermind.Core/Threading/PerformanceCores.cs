// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Threading;

/// <summary>
/// The performance cores of a hybrid CPU, and a scope that keeps the calling thread on them.
/// </summary>
/// <remarks>
/// On a CPU with performance and efficiency cores the scheduler is free to run block processing on an efficiency
/// core, or beside a prewarming thread on the same performance core, and either costs every block a large share of
/// its speed. The processing thread is narrowed to the performance cores for the time it processes blocks; the other
/// pools keep every core.
/// </remarks>
public static partial class PerformanceCores
{
    /// <summary>The CPUs a Linux <c>cpu_set_t</c> holds.</summary>
    internal const int MaxCpus = MaskWords * 64;
    private const int MaskWords = 16;

    [InlineArray(MaskWords)]
    internal struct CpuMask
    {
        private ulong _word;

        public void Add(int cpu) => this[cpu >> 6] |= 1UL << (cpu & 63);

        public readonly bool Contains(int cpu) => (this[cpu >> 6] & (1UL << (cpu & 63))) != 0;
    }

    /// <summary>
    /// The performance cores' logical processors among the ones the process may run on, when that narrows anything.
    /// </summary>
    /// <param name="performanceCpus">Linux's list of the performance cores' logical processors, null on a CPU without one.</param>
    /// <param name="allowedCpus">The CPUs the process may run on, null when unknown.</param>
    /// <param name="mask">The performance cores to run on.</param>
    /// <param name="cpus">The same, as a sorted list.</param>
    /// <returns>
    /// <c>false</c> on a CPU with one kind of core, and where the process may run on no performance core or on
    /// performance cores only, since there is then nothing to narrow.
    /// </returns>
    internal static bool TryBuildMask(string? performanceCpus, string? allowedCpus, out CpuMask mask, out int[] cpus)
    {
        mask = default;
        cpus = [];
        if (performanceCpus is null) return false;

        HashSet<int> performance = ParseCpuList(performanceCpus);
        int allowedCount = int.MaxValue;
        if (allowedCpus is not null)
        {
            HashSet<int> allowed = ParseCpuList(allowedCpus);
            if (allowed.Count > 0)
            {
                performance.IntersectWith(allowed);
                allowedCount = allowed.Count;
            }
        }

        if (performance.Count == 0 || performance.Count == allowedCount) return false;

        cpus = [.. performance];
        Array.Sort(cpus);
        foreach (int cpu in cpus) mask.Add(cpu);
        return true;
    }

    /// <summary>Parses a Linux CPU list such as <c>0-11,14</c>, skipping malformed entries and any a mask cannot hold whole.</summary>
    internal static HashSet<int> ParseCpuList(string cpuList)
    {
        HashSet<int> cpus = [];
        foreach (string range in cpuList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = range.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(range, out int cpu) && cpu is >= 0 and < MaxCpus) cpus.Add(cpu);
            }
            else if (int.TryParse(range.AsSpan(0, dash), out int first) && int.TryParse(range.AsSpan(dash + 1), out int last)
                     && first >= 0 && last >= first && last < MaxCpus)
            {
                for (int cpu = first; cpu <= last; cpu++) cpus.Add(cpu);
            }
        }

        return cpus;
    }
}
