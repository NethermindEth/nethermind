// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Nethermind.Core.Cpu;

public static class RuntimeInformation
{
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    internal static bool IsWindows() => OperatingSystem.IsWindows(); // prefer linker-friendly OperatingSystem APIs

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    internal static bool IsLinux() => OperatingSystem.IsLinux();

    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
    internal static bool IsMacOS() => OperatingSystem.IsMacOS();

    public static CpuInfo? GetCpuInfo()
    {
#if !ZK_EVM
        if (IsWindows())
            return WmicCpuInfoProvider.WmicCpuInfo.Value;
        if (IsLinux())
            return ProcCpuInfoProvider.ProcCpuInfo.Value;
        if (IsMacOS())
            return SysctlCpuInfoProvider.SysctlCpuInfo.Value;
#endif
        return null;
    }

    /// <summary>The logical processors available to the process, at least one.</summary>
    /// <remarks>
    /// The zkEVM guest runs single-threaded and is compiled ahead of time, so it takes a constant and
    /// every path that fans out on the count compiles away.
    /// </remarks>
#if ZK_EVM
    public const int ProcessorCount = 1;
#else
    public static readonly int ProcessorCount = Math.Max(1, Environment.ProcessorCount);
#endif
    /// <summary>Whether the process has a single logical processor.</summary>
    /// <remarks>
    /// Fan-out gates test this rather than the count: nothing gains from fanning out on one processor,
    /// and a property is not a constant expression, so a guard on it raises no unreachable-code
    /// diagnostic where the count is a constant.
    /// </remarks>
    public static bool IsSingleProcessor => ProcessorCount <= 1;
    public static int PhysicalCoreCount { get; } = GetCpuInfo()?.PhysicalCoreCount ?? ProcessorCount;

    /// <summary>
    /// The logical processors this process may run on that sit on one kind of core: on a hybrid CPU, whose core types
    /// Linux lists separately, the larger of its allowed performance and efficiency logical processors, which on an unpinned
    /// hybrid CPU is its performance ones; everywhere else it is <see cref="ProcessorCount"/>.
    /// </summary>
    /// <remarks>
    /// Size CPU-bound work that runs alongside block processing by this. Counting efficiency cores as equal oversubscribes
    /// the performance cores, which shares them with the processing thread and, on a power-limited part, lowers the clock
    /// of every core.
    /// </remarks>
    public static int PerformanceProcessorCount { get; } = GetPerformanceProcessorCount();

    private static int GetPerformanceProcessorCount()
    {
#if !ZK_EVM
        if (IsLinux())
        {
            return PerformanceCountFrom(ReadOrNull("/sys/devices/cpu_core/cpus"), ReadAllowedCpus(), ProcessorCount);
        }
#endif
        return ProcessorCount;
    }

    /// <summary>
    /// The larger of the performance and the efficiency logical processors the process may run on, or
    /// <paramref name="processorCount"/> when the CPU lists no performance cores.
    /// </summary>
    /// <remarks>
    /// The larger group is as many threads as fit on one kind of core without spilling onto the other, and it never
    /// shrinks when the process is allowed one more CPU of either kind.
    /// </remarks>
    /// <param name="performanceCpus">Linux's list of the performance cores' logical processors, null on a CPU without one.</param>
    /// <param name="allowedCpus">The CPUs the process may run on, null when unknown.</param>
    /// <param name="processorCount">The logical processors available to the process.</param>
    internal static int PerformanceCountFrom(string? performanceCpus, string? allowedCpus, int processorCount)
    {
        if (performanceCpus is null) return processorCount;

        HashSet<int> performance = ParseCpuList(performanceCpus);
        int efficiency;
        if (allowedCpus is null)
        {
            efficiency = processorCount - performance.Count;
        }
        else
        {
            HashSet<int> allowed = ParseCpuList(allowedCpus);
            performance.IntersectWith(allowed);
            efficiency = allowed.Count - performance.Count;
        }

        int largest = Math.Max(performance.Count, efficiency);
        return largest > 0 ? Math.Min(largest, processorCount) : processorCount;
    }

    /// <summary>Parses a Linux CPU list such as <c>0-11,14</c>, skipping malformed entries.</summary>
    internal static HashSet<int> ParseCpuList(string cpuList)
    {
        const int maxRange = 1 << 16;
        HashSet<int> cpus = [];
        foreach (string range in cpuList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = range.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(range, out int cpu) && cpu >= 0) cpus.Add(cpu);
            }
            else if (int.TryParse(range.AsSpan(0, dash), out int first) && int.TryParse(range.AsSpan(dash + 1), out int last)
                     && first >= 0 && last >= first && last - first < maxRange)
            {
                for (int cpu = first; cpu <= last; cpu++) cpus.Add(cpu);
            }
        }

        return cpus;
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        // Absent or unreadable: as far as this process can tell the CPU lists no core types, so the logical count stands.
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The CPUs the process may run on, which a cpuset or an affinity mask narrows below the host's.</summary>
    private static string? ReadAllowedCpus()
    {
        const string prefix = "Cpus_allowed_list:";
        string? status = ReadOrNull("/proc/self/status");
        if (status is null) return null;

        foreach (string line in status.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
        }

        return null;
    }

    public static ParallelOptions ParallelOptionsLogicalCores { get; } = new() { MaxDegreeOfParallelism = ProcessorCount };
    public static bool Is64BitPlatform() => IntPtr.Size == 8;
}
