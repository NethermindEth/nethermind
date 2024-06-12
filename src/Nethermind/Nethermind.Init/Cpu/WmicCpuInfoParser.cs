// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System.Collections.Generic;

namespace Nethermind.Init.Cpu;

internal static class WmicCpuInfoParser
{
    internal static CpuInfo ParseOutput(string? content)
    {
        var processors = SectionsHelper.ParseSections(content, '=');

        var processorModelNames = new HashSet<string>();
        int physicalCoreCount = 0;
        int logicalCoreCount = 0;
        int processorsCount = 0;

        var currentClockSpeed = Frequency.Zero;
        var maxClockSpeed = Frequency.Zero;
        var minClockSpeed = Frequency.Zero;

        foreach (var processor in processors)
        {
            if (processor.TryGetValue(WmicCpuInfoKeyNames.NumberOfCores, out string? numberOfCoresValue) &&
                int.TryParse(numberOfCoresValue, out int numberOfCores) &&
                numberOfCores > 0)
                physicalCoreCount += numberOfCores;

            if (processor.TryGetValue(WmicCpuInfoKeyNames.NumberOfLogicalProcessors, out string? numberOfLogicalValue) &&
                int.TryParse(numberOfLogicalValue, out int numberOfLogical) &&
                numberOfLogical > 0)
                logicalCoreCount += numberOfLogical;

            if (processor.TryGetValue(WmicCpuInfoKeyNames.Name, out string? name))
            {
                processorModelNames.Add(name);
                processorsCount++;
            }

            if (processor.TryGetValue(WmicCpuInfoKeyNames.MaxClockSpeed, out string? frequencyValue)
                && int.TryParse(frequencyValue, out int frequency)
                && frequency > 0)
            {
                maxClockSpeed += frequency;
            }
        }

        return new CpuInfo(
            processorModelNames.Count > 0 ? string.Join(", ", processorModelNames) : "",
            processorsCount > 0 ? processorsCount : (int?)null,
            physicalCoreCount > 0 ? physicalCoreCount : (int?)null,
            logicalCoreCount > 0 ? logicalCoreCount : (int?)null,
            currentClockSpeed > 0 && processorsCount > 0 ? Frequency.FromMHz(currentClockSpeed / processorsCount) : (Frequency?)null,
            minClockSpeed > 0 && processorsCount > 0 ? Frequency.FromMHz(minClockSpeed / processorsCount) : (Frequency?)null,
            maxClockSpeed > 0 && processorsCount > 0 ? Frequency.FromMHz(maxClockSpeed / processorsCount) : (Frequency?)null);
    }
}
