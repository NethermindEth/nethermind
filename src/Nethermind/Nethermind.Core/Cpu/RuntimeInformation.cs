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
#if !ZKVM
        if (IsWindows())
            return WmicCpuInfoProvider.WmicCpuInfo.Value;
        if (IsLinux())
            return ProcCpuInfoProvider.ProcCpuInfo.Value;
        if (IsMacOS())
            return SysctlCpuInfoProvider.SysctlCpuInfo.Value;
#endif

        return null;

    }

    public static readonly int ProcessorCount = Math.Max(1, Environment.ProcessorCount);
    public static int PhysicalCoreCount { get; } = GetCpuInfo()?.PhysicalCoreCount ?? ProcessorCount;
    public static ParallelOptions ParallelOptionsLogicalCores { get; } = new() { MaxDegreeOfParallelism = ProcessorCount };
    public static ParallelOptions ParallelOptionsPhysicalCoresUpTo16 { get; } = new() { MaxDegreeOfParallelism = Math.Min(PhysicalCoreCount, 16) };
    public static bool Is64BitPlatform() => IntPtr.Size == 8;
}
