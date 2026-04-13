// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

namespace Nethermind.Core.Cpu;

public class CpuInfo(string processorName,
               int? physicalProcessorCount,
               int? physicalCoreCount,
               int? logicalCoreCount,
               Frequency? nominalFrequency,
               Frequency? minFrequency,
               Frequency? maxFrequency)
{
    public string ProcessorName { get; } = processorName;
    public int? PhysicalProcessorCount { get; } = physicalProcessorCount;
    public int? PhysicalCoreCount { get; } = physicalCoreCount;
    public int? LogicalCoreCount { get; } = logicalCoreCount;
    public Frequency? NominalFrequency { get; } = nominalFrequency;
    public Frequency? MinFrequency { get; } = minFrequency;
    public Frequency? MaxFrequency { get; } = maxFrequency;
}
