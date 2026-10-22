// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System.Collections.Generic;

namespace Nethermind.Core.Cpu;

internal static class SysctlCpuInfoParser
{
    internal static CpuInfo ParseOutput(string? content)
    {
        Dictionary<string, string> sysctl = SectionsHelper.ParseSection(content, ':');
        string processorName = sysctl.GetValueOrDefault("machdep.cpu.brand_string") ?? "";
        int? physicalProcessorCount = GetPositiveIntValue(sysctl, "hw.packages");
        int? physicalCoreCount = GetPositiveIntValue(sysctl, "hw.physicalcpu");
        int? logicalCoreCount = GetPositiveIntValue(sysctl, "hw.logicalcpu");
        long? nominalFrequency = GetPositiveLongValue(sysctl, "hw.cpufrequency");
        long? minFrequency = GetPositiveLongValue(sysctl, "hw.cpufrequency_min");
        long? maxFrequency = GetPositiveLongValue(sysctl, "hw.cpufrequency_max");
        return new CpuInfo(processorName, physicalProcessorCount, physicalCoreCount, logicalCoreCount, nominalFrequency, minFrequency, maxFrequency);
    }

    private static int? GetPositiveIntValue(Dictionary<string, string> sysctl, string keyName)
    {
        if (sysctl.TryGetValue(keyName, out string? value) &&
            int.TryParse(value, out int result) &&
            result > 0)
            return result;
        return null;
    }

    private static long? GetPositiveLongValue(Dictionary<string, string> sysctl, string keyName)
    {
        if (sysctl.TryGetValue(keyName, out string? value) &&
            long.TryParse(value, out long result) &&
            result > 0)
            return result;
        return null;
    }
}
