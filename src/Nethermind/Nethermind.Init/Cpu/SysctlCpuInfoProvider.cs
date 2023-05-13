// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;

namespace Nethermind.Init.Cpu;

/// <summary>
/// CPU information from output of the `sysctl -a` command.
/// MacOSX only.
/// </summary>
internal static class SysctlCpuInfoProvider
{
    internal static readonly Lazy<CpuInfo?> SysctlCpuInfo = new Lazy<CpuInfo?>(Load);

    private static CpuInfo? Load()
    {
        if (RuntimeInformation.IsMacOS())
        {
            string? content = ProcessHelper.RunAndReadOutput("sysctl", "-a");
            return SysctlCpuInfoParser.ParseOutput(content);
        }
        return null;
    }
}
