// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.Processing;

/// <summary>Configuration for the BAL-driven shadow state-root validation lane.</summary>
/// <remarks>
/// The shadow lane recomputes the post-block state root from the block access list, in parallel with
/// normal processing, and compares it to the header. It never affects which blocks are accepted or rejected.
/// </remarks>
public interface IBalStateRootConfig : IConfig
{
    [ConfigItem(Description = "Whether to run the BAL-driven shadow state-root validation lane. It compares and counts only, never affecting consensus.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Whether the shadow lane should offload batch hashing to the GPU backend when available.", DefaultValue = "false")]
    bool UseGpu { get; set; }

    [ConfigItem(Description = "Minimum keccak batch size (number of messages) below which the shadow lane stays on the CPU backend instead of the GPU.", DefaultValue = "4096")]
    int GpuMinBatch { get; set; }
}
