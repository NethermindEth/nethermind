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
    public long? StartBlock { get; set; }
    public long? EndBlock { get; set; }
    public long? Blocks { get; set; }
    public string Mode { get; set; } = "RealTime";
}
