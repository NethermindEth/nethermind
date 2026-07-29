// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Implementation of opcode tracing configuration.
/// </summary>
public class OpcodeTracingConfig : IOpcodeTracingConfig
{
    public bool Enabled { get; set; }
    public string OutputDirectory { get; set; } = "traces/opcodes";
    public int MaxDegreeOfParallelism { get; set; } = 0;
    public ulong? StartBlock { get; set; }
    public ulong? EndBlock { get; set; }
    public ulong? RecentBlocks { get; set; }
    public string Mode { get; set; } = "RealTime";
}
