// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Cpu;

public static partial class RuntimeInformation
{
    public static partial CpuInfo? GetCpuInfo() => null;

    /// <summary>The logical processors available to the process, at least one.</summary>
    /// <remarks>
    /// The zkEVM guest runs single-threaded and is compiled ahead of time, so it takes a constant and
    /// every path that fans out on the count compiles away.
    /// </remarks>
    public const int ProcessorCount = 1;
}
