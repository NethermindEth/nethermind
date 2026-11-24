// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Shared JSON options for the Beacon API. Every DTO names its own
/// wire field with <see cref="JsonPropertyNameAttribute"/> rather than relying on a naming policy,
/// so a field's on-wire name is never a guess.</summary>
internal static class BeaconApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

internal sealed record RootDto([property: JsonPropertyName("root")] string Root);

internal sealed record CheckpointDto(
    [property: JsonPropertyName("epoch")] string Epoch,
    [property: JsonPropertyName("root")] string Root);

internal sealed record ForkDto(
    [property: JsonPropertyName("previous_version")] string PreviousVersion,
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("epoch")] string Epoch);
