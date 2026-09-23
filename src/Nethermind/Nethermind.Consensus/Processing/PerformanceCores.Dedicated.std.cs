// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

internal static partial class PerformanceCores
{
    internal readonly record struct Cpu(int Id, int Core, int PerformanceClass, uint CpuSetId = 0);

    internal static ReadOnlySpan<int> DedicatedCpus(ProcessingCores cores) =>
        cores != ProcessingCores.All && (uint)cores < (uint)ModeCount ? Host.DedicatedProcessing?.Cpus ?? [] : [];

    /// <summary>Keeps a dedicated worker on its selected CPUs until the scope ends on the same thread.</summary>
    internal static Scope NarrowDedicatedThread(ProcessingCores cores, bool background, ILogger logger) =>
        cores != ProcessingCores.All && (uint)cores < (uint)ModeCount
        && (background ? Host.DedicatedBackground : Host.DedicatedProcessing) is { } selection
            ? Narrow(selection, logger, widenOnDispose: false) : default;

    private static Selection? DedicatedSelection(List<Cpu> cpus, bool background)
    {
        int[] selected = SelectDedicated(cpus, background);
        return selected.Length == 0 ? null : CreateSelection(selected, cpus);
    }

    private static Selection CreateSelection(int[] selected, List<Cpu> cpus)
    {
        CpuMask mask = default;
        foreach (int cpu in selected) mask.Add(cpu);
        uint[]? sets = null;
        if (OperatingSystem.IsWindows())
        {
            sets = new uint[selected.Length];
            int index = 0;
            foreach (Cpu cpu in cpus)
            {
                if (Array.IndexOf(selected, cpu.Id) >= 0) sets[index++] = cpu.CpuSetId;
            }
        }
        return new Selection(mask, selected) { CpuSets = sets };
    }

    internal static List<Cpu> ReadCpus()
    {
        if (OperatingSystem.IsWindows()) return ReadWindowsCpus();
        if (!OperatingSystem.IsLinux() || !TryReadAffinity(out CpuMask allowed)) return [];
        string? performanceList = ReadOrNull("/sys/devices/cpu_core/cpus");
        HashSet<int>? performance = performanceList is null ? null : ParseCpuList(performanceList);
        List<Cpu> cpus = [];
        for (int cpu = 0; cpu < MaxCpus; cpu++)
        {
            if (!allowed.Contains(cpu)) continue;
            if (ReadSiblings(cpu) is not { } siblingsList) return [];
            HashSet<int> siblings = ParseCpuList(siblingsList);
            if (!siblings.Contains(cpu)) return [];
            int core = cpu;
            foreach (int sibling in siblings) core = Math.Min(core, sibling);
            string? capacity = ReadOrNull($"/sys/devices/system/cpu/cpu{cpu}/cpu_capacity");
            int performanceClass = performance is not null ? (performance.Contains(cpu) ? 1 : 0)
                : capacity is not null ? int.Parse(capacity) : 0;
            cpus.Add(new Cpu(cpu, core, performanceClass));
        }
        return cpus;
    }

    internal static int[] SelectDedicated(List<Cpu> cpus, bool background)
    {
        if (cpus.Count == 0) return [];
        cpus.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        int highestClass = int.MinValue;
        foreach (Cpu cpu in cpus) highestClass = Math.Max(highestClass, cpu.PerformanceClass);

        Cpu? first = null;
        Cpu? processing = null;
        foreach (Cpu cpu in cpus)
        {
            if (cpu.PerformanceClass != highestClass) continue;
            if (first is null)
            {
                first = cpu;
                processing = cpu;
            }
            else if (cpu.Core != first.Value.Core)
            {
                processing = cpu;
                break;
            }
        }

        Cpu selected = processing!.Value;
        if (selected.Id == 0)
        {
            foreach (Cpu cpu in cpus)
            {
                if (cpu.Id != 0 && cpu.PerformanceClass == highestClass)
                {
                    selected = cpu;
                    break;
                }
            }
        }
        if (!background) return [selected.Id];

        List<int> remaining = [];
        foreach (Cpu cpu in cpus)
        {
            if (cpu.PerformanceClass < highestClass) remaining.Add(cpu.Id);
        }
        if (remaining.Count > 0) return remaining.ToArray();

        foreach (Cpu cpu in cpus)
        {
            if (cpu.Core != selected.Core) remaining.Add(cpu.Id);
        }
        // On a single physical core the workers must share it to make progress.
        return remaining.ToArray();
    }

}
