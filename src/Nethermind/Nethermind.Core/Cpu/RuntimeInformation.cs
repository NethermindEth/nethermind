// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
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
    public static ParallelOptions ParallelOptionsLogicalCores { get; } = new() { MaxDegreeOfParallelism = ProcessorCount };
    public static ParallelOptions ParallelOptionsPhysicalCoresUpTo16 { get; } = new() { MaxDegreeOfParallelism = Math.Min(ProcessorCount, 16) };
    public static bool Is64BitPlatform() => IntPtr.Size == 8;
}
