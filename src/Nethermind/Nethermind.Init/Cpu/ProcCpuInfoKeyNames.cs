// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

namespace Nethermind.Init.Cpu;

internal static class ProcCpuInfoKeyNames
{
    internal const string PhysicalId = "physical id";
    internal const string CpuCores = "cpu cores";
    internal const string ModelName = "model name";
    internal const string MaxFrequency = "max freq";
    internal const string MinFrequency = "min freq";
}
