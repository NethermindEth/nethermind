// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Output structure for cumulative opcode trace in RealTime mode.
/// Contains aggregated opcode counts across all processed blocks in the session.
/// </summary>
public sealed class CumulativeTraceOutput
{
    /// <summary>
    /// Gets or sets the cumulative metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public required CumulativeMetadata Metadata { get; init; }

    /// <summary>
    /// Gets or sets the aggregated opcode counts dictionary mapping opcode bytes to total occurrence counts.
    /// Serializes with human-readable opcode names as keys.
    /// </summary>
    [JsonPropertyName("opcodeCounts")]
    [JsonConverter(typeof(OpcodeCountsJsonConverter))]
    public required Dictionary<byte, long> OpcodeCounts { get; init; }
}

/// <summary>
/// Metadata for cumulative RealTime mode trace.
/// </summary>
public sealed class CumulativeMetadata
{
    /// <summary>
    /// Gets or sets the first block number included in the cumulative count.
    /// </summary>
    [JsonPropertyName("firstBlock")]
    public required long FirstBlock { get; init; }

    /// <summary>
    /// Gets or sets the last block number included in the cumulative count.
    /// </summary>
    [JsonPropertyName("lastBlock")]
    public required long LastBlock { get; init; }

    /// <summary>
    /// Gets or sets the total number of blocks processed.
    /// </summary>
    [JsonPropertyName("totalBlocksProcessed")]
    public required long TotalBlocksProcessed { get; init; }

    /// <summary>
    /// Gets or sets the unique session identifier for this tracing session.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets or sets the tracing mode (always "RealTime" for cumulative output).
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    /// <summary>
    /// Gets or sets the UTC timestamp when tracing started.
    /// </summary>
    [JsonPropertyName("startedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartedAt { get; init; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the last update.
    /// </summary>
    [JsonPropertyName("lastUpdatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastUpdatedAt { get; init; }

    /// <summary>
    /// Gets or sets the total elapsed time in milliseconds since tracing started.
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Duration { get; init; }

    /// <summary>
    /// Gets or sets the completion status ("complete", "partial", or "in_progress").
    /// </summary>
    [JsonPropertyName("completionStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletionStatus { get; init; }

    /// <summary>
    /// Gets or sets the configured start block (if range was specified).
    /// </summary>
    [JsonPropertyName("configuredStartBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConfiguredStartBlock { get; init; }

    /// <summary>
    /// Gets or sets the configured end block (if range was specified).
    /// </summary>
    [JsonPropertyName("configuredEndBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConfiguredEndBlock { get; init; }

    /// <summary>
    /// Gets or sets warnings encountered during tracing.
    /// </summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Warnings { get; init; }
}
