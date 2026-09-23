// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

internal static partial class PerformanceCores
{
    internal static void BuildWindowsSelections(List<Cpu> cpus, Selection?[] selections, out PrewarmSplit? prewarm, out PrewarmSplit? prewarmDedicated)
    {
        prewarm = null;
        prewarmDedicated = null;
        if (cpus.Count == 0) return;
        int highest = int.MinValue;
        foreach (Cpu cpu in cpus) highest = Math.Max(highest, cpu.PerformanceClass);
        HashSet<int> allowed = [];
        List<int> performance = [];
        Dictionary<int, string> siblings = [];
        foreach (Cpu cpu in cpus)
        {
            allowed.Add(cpu.Id);
            if (cpu.PerformanceClass == highest) performance.Add(cpu.Id);
            List<int> sameCore = [];
            foreach (Cpu sibling in cpus)
                if (sibling.Core == cpu.Core) sameCore.Add(sibling.Id);
            siblings[cpu.Id] = string.Join(',', sameCore);
        }
        if (performance.Count == cpus.Count) return;
        string performanceList = string.Join(',', performance);
        foreach (ProcessingCores cores in Enum.GetValues<ProcessingCores>())
        {
            if (TryBuildMask(cores, performanceList, allowed, cpu => siblings[cpu], out _, out int[] selected))
                selections[(int)cores] = CreateSelection(selected, cpus);
        }
        if (selections[(int)ProcessingCores.Performance] is { } near
            && TryBuildEfficiencyMask(performanceList, allowed, out _, out int[] far))
        {
            Selection efficiency = CreateSelection(far, cpus);
            prewarm = new PrewarmSplit(near, efficiency, near.Cpus.Length - 1);
            prewarmDedicated = prewarm;
            if (selections[(int)ProcessingCores.Dedicated] is { } dedicated
                && TryExclude(near.Cpus, dedicated.Cpus, out _, out int[] rest))
                prewarmDedicated = new PrewarmSplit(CreateSelection(rest, cpus), efficiency, rest.Length);
        }
    }

    private static Scope NarrowWindows(Selection selection, ILogger logger)
    {
        if (selection.CpuSets is not { } sets) return default;
        GetThreadSelectedCpuSets(GetCurrentThread(), null, 0, out uint count);
        uint[] previous = new uint[count];
        if (!GetThreadSelectedCpuSets(GetCurrentThread(), previous, count, out _)) return default;
        return SetThreadSelectedCpuSets(GetCurrentThread(), sets, (uint)sets.Length)
            ? new Scope(previous, logger) : default;
    }

    private static List<Cpu> ReadWindowsCpus()
    {
        nint process = GetCurrentProcess();
        nuint allowed = nuint.MaxValue;
        bool singleGroup = GetActiveProcessorGroupCount() == 1;
        if (singleGroup && !GetProcessAffinityMask(process, out allowed, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        GetProcessDefaultCpuSets(process, null, 0, out uint count);
        uint[] defaults = new uint[count];
        if (!GetProcessDefaultCpuSets(process, defaults, count, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        GetSystemCpuSetInformation(null, 0, out uint size, process, 0);
        if (size == 0) return [];
        byte[] buffer = new byte[size];
        if (!GetSystemCpuSetInformation(buffer, size, out uint returned, process, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());

        return ParseWindowsCpus(buffer.AsSpan(0, checked((int)returned)), [.. defaults], singleGroup ? (ulong)allowed : null);
    }

    internal static List<Cpu> ParseWindowsCpus(ReadOnlySpan<byte> buffer, HashSet<uint> defaults, ulong? singleGroupMask)
    {
        List<Cpu> cpus = [];
        for (int offset = 0; offset < buffer.Length;)
        {
            ReadOnlySpan<byte> entry = buffer[offset..];
            if (entry.Length < 8) return [];
            int entrySize = BitConverter.ToInt32(entry);
            if (entrySize < 8 || entrySize > entry.Length) return [];
            // SYSTEM_CPU_SET_INFORMATION has a variable size; only CpuSetInformation (0) has these fields.
            if (BitConverter.ToInt32(entry[4..]) == 0)
            {
                if (entrySize < 32) return [];
                uint cpuSetId = BitConverter.ToUInt32(entry[8..]);
                int group = BitConverter.ToUInt16(entry[12..]);
                int cpu = entry[14];
                byte flags = entry[19];
                bool allocatedElsewhere = (flags & 2) != 0 && (flags & 4) == 0;
                if (cpu >= 64 || group * 64 + cpu >= MaxCpus) return [];
                bool allowedByMask = singleGroupMask is not { } mask || (group == 0 && (mask & (1UL << cpu)) != 0);
                if (allowedByMask && !allocatedElsewhere && (defaults.Count == 0 || defaults.Contains(cpuSetId)))
                    cpus.Add(new Cpu(group * 64 + cpu, group * 256 + entry[15], entry[18], cpuSetId));
            }
            offset += entrySize;
        }
        return cpus;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadSelectedCpuSets(nint thread, uint[] cpuSetIds, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessDefaultCpuSets(nint process, [Out] uint[]? cpuSetIds, uint count, out uint requiredCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(nint process, out nuint processMask, out nuint systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation([Out] byte[]? information, uint length, out uint returnedLength, nint process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadSelectedCpuSets(nint thread, [Out] uint[]? ids, uint count, out uint requiredCount);
}
