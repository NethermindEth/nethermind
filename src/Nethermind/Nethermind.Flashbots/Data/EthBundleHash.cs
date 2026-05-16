// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Flashbots.Data;

/// <summary>
/// Wire-format response for <c>eth_sendBundle</c>.
/// </summary>
/// <remarks>
/// <see cref="Smart"/> is populated with the literal string <c>"true"</c> only when the
/// caller specified at least one builder in <c>builders</c>, matching the Flashbots
/// reference implementation.
/// </remarks>
public class EthBundleHash
{
    [JsonPropertyName("bundleHash")]
    public Hash256 BundleHash { get; set; } = Hash256.Zero;

    [JsonPropertyName("smart")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Smart { get; set; }
}
