// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Output structure for per-block opcode trace in RealTime mode.
/// Contains opcode counts for a single block plus block-specific metadata.
/// </summary>
public sealed class PerBlockTraceOutput
{
    /// <summary>
    /// Gets or sets the block-specific metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public required PerBlockMetadata Metadata { get; init; }

    /// <summary>
    /// Gets or sets the opcode counts dictionary mapping opcode names to occurrence counts for this block.
    /// </summary>
    [JsonPropertyName("opcodeCounts")]
    public required Dictionary<string, long> OpcodeCounts { get; init; }
}

/// <summary>
/// Metadata for a single block's opcode trace.
/// </summary>
public sealed class PerBlockMetadata
{
    /// <summary>
    /// Gets or sets the block number (required per FR-023).
    /// </summary>
    [JsonPropertyName("blockNumber")]
    public required long BlockNumber { get; init; }

    /// <summary>
    /// Gets or sets the parent block hash (optional per FR-023).
    /// </summary>
    [JsonPropertyName("parentHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentHash { get; init; }

    /// <summary>
    /// Gets or sets the block timestamp in Unix seconds (optional per FR-023).
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Timestamp { get; init; }

    /// <summary>
    /// Gets or sets the number of transactions in the block (optional per FR-023).
    /// </summary>
    [JsonPropertyName("transactionCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TransactionCount { get; init; }

    /// <summary>
    /// Gets or sets the total gas used in the block (optional per FR-023).
    /// </summary>
    [JsonPropertyName("gasUsed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? GasUsed { get; init; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this block trace was created.
    /// </summary>
    [JsonPropertyName("tracedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? TracedAt { get; init; }
}
