// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Metadata for an opcode trace operation.
/// </summary>
public sealed class TraceMetadata
{
    /// <summary>
    /// Gets or sets the first block number traced (inclusive).
    /// </summary>
    [JsonPropertyName("startBlock")]
    public required long StartBlock { get; init; }

    /// <summary>
    /// Gets or sets the last block number traced (inclusive).
    /// </summary>
    [JsonPropertyName("endBlock")]
    public required long EndBlock { get; init; }

    /// <summary>
    /// Gets or sets the tracing mode used (RealTime or Retrospective).
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    /// <summary>
    /// Gets or sets the UTC timestamp when tracing completed.
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Timestamp { get; init; }

    /// <summary>
    /// Gets or sets the total elapsed time in milliseconds.
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Duration { get; init; }

    /// <summary>
    /// Gets or sets the completion status (complete, partial, or error).
    /// </summary>
    [JsonPropertyName("completionStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletionStatus { get; init; }

    /// <summary>
    /// Gets or sets warnings encountered during tracing.
    /// </summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Warnings { get; init; }
}
