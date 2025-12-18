// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Configuration for the Opcode Tracing Plugin.
/// </summary>
[ConfigCategory(Description = "Configuration for the Opcode tracing plugin")]
public interface IOpcodeTracingConfig : IConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether the OpcodeTracing plugin is enabled.
    /// </summary>
    [ConfigItem(Description = "Enable the OpcodeTracing plugin.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the directory where opcode trace JSON files are written.
    /// </summary>
    [ConfigItem(Description = "Directory where opcode trace JSON files are written.", DefaultValue = "traces/opcodes")]
    string OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of tracing workers. 0 uses the number of logical processors.
    /// </summary>
    [ConfigItem(Description = "Maximum number of tracing workers. 0 switches to the number of logical processors. For Retrospective mode only.", DefaultValue = "0")]
    int MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// Gets or sets the start block number for tracing (inclusive). Used with EndBlock to define an explicit range.
    /// </summary>
    [ConfigItem(Description = "Start block number for tracing (inclusive). Used with EndBlock to define explicit range.", DefaultValue = "null")]
    long? StartBlock { get; set; }

    /// <summary>
    /// Gets or sets the end block number for tracing (inclusive). Used with StartBlock to define an explicit range.
    /// </summary>
    [ConfigItem(Description = "End block number for tracing (inclusive). Used with StartBlock to define explicit range.", DefaultValue = "null")]
    long? EndBlock { get; set; }

    /// <summary>
    /// Gets or sets the number of recent blocks to trace from the chain tip.
    /// Alternative to StartBlock/EndBlock for convenience. If both are specified, explicit range takes precedence.
    /// </summary>
    [ConfigItem(Description = "Number of recent blocks to trace from chain tip. Alternative to StartBlock/EndBlock.", DefaultValue = "null")]
    long? Blocks { get; set; }

    /// <summary>
    /// Gets or sets the tracing mode: "RealTime" traces blocks as they are processed during sync/new blocks,
    /// "Retrospective" reads historical blocks from database.
    /// </summary>
    [ConfigItem(Description = "Tracing mode: RealTime (trace during processing) or Retrospective (read from database).", DefaultValue = "\"RealTime\"")]
    string Mode { get; set; }
}
