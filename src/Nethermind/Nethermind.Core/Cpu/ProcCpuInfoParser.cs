// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nethermind.Core.Cpu;

internal static partial class ProcCpuInfoParser
{
    internal static CpuInfo ParseOutput(string? content)
    {
        var logicalCores = SectionsHelper.ParseSections(content, ':');
        var processorModelNames = new HashSet<string>();
        var processorsToPhysicalCoreCount = new Dictionary<string, int>();

        int logicalCoreCount = 0;
        var nominalFrequency = Frequency.Zero;
        var minFrequency = Frequency.Zero;
        var maxFrequency = Frequency.Zero;

        foreach (var logicalCore in logicalCores)
        {
            if (logicalCore.TryGetValue(ProcCpuInfoKeyNames.PhysicalId, out string? physicalId) &&
                logicalCore.TryGetValue(ProcCpuInfoKeyNames.CpuCores, out string? cpuCoresValue) &&
                int.TryParse(cpuCoresValue, out int cpuCoreCount) &&
                cpuCoreCount > 0)
                processorsToPhysicalCoreCount[physicalId] = cpuCoreCount;

            if (logicalCore.TryGetValue(ProcCpuInfoKeyNames.ModelName, out string? modelName))
            {
                processorModelNames.Add(modelName);
                nominalFrequency = ParseFrequencyFromBrandString(modelName);
                logicalCoreCount++;
            }

            if (logicalCore.TryGetValue(ProcCpuInfoKeyNames.MinFrequency, out string? minCpuFreqValue)
                && Frequency.TryParseMHz(minCpuFreqValue, out var minCpuFreq))
            {
                minFrequency = minCpuFreq;
            }

            if (logicalCore.TryGetValue(ProcCpuInfoKeyNames.MaxFrequency, out string? maxCpuFreqValue)
                 && Frequency.TryParseMHz(maxCpuFreqValue, out var maxCpuFreq))
            {
                maxFrequency = maxCpuFreq;
            }
        }

        return new CpuInfo(
            processorModelNames.Count > 0 ? string.Join(", ", processorModelNames) : "",
            processorsToPhysicalCoreCount.Count > 0 ? processorsToPhysicalCoreCount.Count : null,
            processorsToPhysicalCoreCount.Count > 0 ? processorsToPhysicalCoreCount.Values.Sum() : null,
            logicalCoreCount > 0 ? logicalCoreCount : null,
            nominalFrequency > 0 ? nominalFrequency : null,
            minFrequency > 0 ? minFrequency : null,
            maxFrequency > 0 ? maxFrequency : null);
    }

    internal static Frequency ParseFrequencyFromBrandString(string brandString)
    {
        const string pattern = "(\\d.\\d+)GHz";
        var matches = MatchRegex().Matches(brandString);
        if (matches.Count > 0 && matches[0].Groups.Count > 1)
        {
            string match = Regex.Matches(brandString, pattern, RegexOptions.IgnoreCase)[0].Groups[1].ToString();
            return Frequency.TryParseGHz(match, out var result) ? result : Frequency.Zero;
        }

        return 0d;
    }

    [GeneratedRegex("(\\d.\\d+)GHz", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex MatchRegex();
}
