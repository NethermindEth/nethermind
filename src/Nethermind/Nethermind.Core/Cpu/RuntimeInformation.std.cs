// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Cpu;

public static partial class RuntimeInformation
{
    public static partial CpuInfo? GetCpuInfo()
    {
        if (IsWindows())
            return WmicCpuInfoProvider.WmicCpuInfo.Value;
        if (IsLinux())
            return ProcCpuInfoProvider.ProcCpuInfo.Value;
        if (IsMacOS())
            return SysctlCpuInfoProvider.SysctlCpuInfo.Value;
        return null;
    }

    /// <summary>The logical processors available to the process, at least one.</summary>
    public static int ProcessorCount => Math.Max(1, Environment.ProcessorCount);
}
