// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Config;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The performance cores of an Intel hybrid CPU, and a scope that keeps the calling thread on them.
/// </summary>
/// <remarks>
/// The processing loop continues on whichever thread-pool thread resumes it, and on a CPU with performance and
/// efficiency cores the scheduler can start that thread on an efficiency core for a block that lasts tens of
/// milliseconds. The core types come from Linux's Intel hybrid PMU listing (<c>/sys/devices/cpu_core/cpus</c>);
/// other hybrid designs do not publish it, and there this is a no-op. The allowed CPUs are read once, so a cpuset
/// changed at runtime is not seen; the kernel then refuses a mask outside it and the scope does nothing.
/// </remarks>
internal static partial class PerformanceCores
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
    /// The logical processors <paramref name="cores"/> selects among the performance cores the process may run on,
    /// when that narrows anything.
    /// </summary>
    /// <param name="cores">Which of the performance cores' logical processors to run on.</param>
    /// <param name="performanceCpus">Linux's list of the performance cores' logical processors, null on a CPU without one.</param>
    /// <param name="allowedCpus">The CPUs the process may run on, null when unknown.</param>
    /// <param name="siblingsOf">A logical processor's hyperthread siblings as a Linux CPU list, null when unknown.</param>
    /// <param name="mask">The logical processors to run on.</param>
    /// <param name="cpus">The same, as a sorted list.</param>
    /// <returns>
    /// <c>false</c> for <see cref="ProcessingCores.All"/>, on a CPU with one kind of core, and where the selection
    /// holds every CPU the process may run on or none of them, since there is then nothing to narrow.
    /// </returns>
    internal static bool TryBuildMask(ProcessingCores cores, string? performanceCpus, string? allowedCpus, Func<int, string?> siblingsOf, out CpuMask mask, out int[] cpus)
    {
        mask = default;
        cpus = [];
        if (cores == ProcessingCores.All || performanceCpus is null) return false;

        HashSet<int> selected = ParseCpuList(performanceCpus);
        int allowedCount = int.MaxValue;
        if (allowedCpus is not null)
        {
            HashSet<int> allowed = ParseCpuList(allowedCpus);
            if (allowed.Count > 0)
            {
                selected.IntersectWith(allowed);
                allowedCount = allowed.Count;
            }
        }

        if (cores == ProcessingCores.PerformancePhysical)
        {
            // The lowest selected hyperthread of each core stands for the core.
            HashSet<int> performance = [.. selected];
            selected.RemoveWhere(cpu => siblingsOf(cpu) is { } siblings && ParseCpuList(siblings).Any(sibling => sibling < cpu && performance.Contains(sibling)));
        }

        if (selected.Count == 0 || selected.Count == allowedCount) return false;

        cpus = [.. selected];
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
