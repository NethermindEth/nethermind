// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.OpcodeTracing.Plugin.Output;

/// <summary>
/// Root structure for opcode trace JSON output.
/// </summary>
public sealed class TraceOutput
{
    /// <summary>
    /// Gets or sets the trace metadata containing context information.
    /// </summary>
    [JsonPropertyName("metadata")]
    public required TraceMetadata Metadata { get; init; }

    /// <summary>
    /// Gets or sets the opcode counts dictionary mapping opcode names to occurrence counts.
    /// </summary>
    [JsonPropertyName("opcodeCounts")]
    public required Dictionary<string, long> OpcodeCounts { get; init; }
}
