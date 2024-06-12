// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

namespace Nethermind.Init.Cpu;

internal static class WmicCpuInfoKeyNames
{
    internal const string NumberOfLogicalProcessors = "NumberOfLogicalProcessors";
    internal const string NumberOfCores = "NumberOfCores";
    internal const string Name = "Name";
    internal const string MaxClockSpeed = "MaxClockSpeed";
}
