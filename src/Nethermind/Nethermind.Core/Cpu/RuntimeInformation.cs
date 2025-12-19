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
        if (IsWindows())
            return WmicCpuInfoProvider.WmicCpuInfo.Value;
        if (IsLinux())
            return ProcCpuInfoProvider.ProcCpuInfo.Value;
        if (IsMacOS())
            return SysctlCpuInfoProvider.SysctlCpuInfo.Value;

        return null;
    }

    public static int PhysicalCoreCount { get; } = GetCpuInfo()?.PhysicalCoreCount ?? Environment.ProcessorCount;

    // Some custom runtimes can report Environment.ProcessorCount == 0, which breaks ParallelOptions (must be -1 or >= 1).
    private static int SafeProcessorCount => Math.Max(Environment.ProcessorCount, 1);
    private static int SafePhysicalCoreCount => Math.Max(PhysicalCoreCount, 1);

    public static ParallelOptions ParallelOptionsLogicalCores { get; } = new() { MaxDegreeOfParallelism = SafeProcessorCount };
    public static ParallelOptions ParallelOptionsPhysicalCoresUpTo16 { get; } = new() { MaxDegreeOfParallelism = Math.Min(SafePhysicalCoreCount, 16) };

    public static bool Is64BitPlatform() => IntPtr.Size == 8;
}
