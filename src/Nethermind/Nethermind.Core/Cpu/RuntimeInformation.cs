// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
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
    /// The logical processors on performance cores: on a hybrid CPU, whose core types Linux lists separately, the
    /// efficiency cores are left out; everywhere else it is <see cref="ProcessorCount"/>.
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
            try
            {
                const string performanceCores = "/sys/devices/cpu_core/cpus";
                if (File.Exists(performanceCores))
                {
                    int count = CountCpuList(File.ReadAllText(performanceCores));
                    if (count > 0) return Math.Min(count, ProcessorCount);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
#endif
        return ProcessorCount;
    }

    /// <summary>Counts the CPUs in a Linux CPU list such as <c>0-11,14</c>.</summary>
    internal static int CountCpuList(string cpuList)
    {
        int count = 0;
        foreach (string range in cpuList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = range.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(range, out _)) count++;
            }
            else if (int.TryParse(range.AsSpan(0, dash), out int first) && int.TryParse(range.AsSpan(dash + 1), out int last) && last >= first)
            {
                count += last - first + 1;
            }
        }

        return count;
    }
    public static ParallelOptions ParallelOptionsLogicalCores { get; } = new() { MaxDegreeOfParallelism = ProcessorCount };
    public static bool Is64BitPlatform() => IntPtr.Size == 8;
}
