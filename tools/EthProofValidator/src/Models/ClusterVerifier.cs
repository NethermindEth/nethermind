// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.EthProofValidator.Models;

public class ClusterVerifier
{
    [JsonPropertyName("cluster_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("zkvm")]
    public string ZkType { get; set; } = string.Empty;

    [JsonPropertyName("vk_path")]
    public string VkPath { get; set; } = string.Empty;

    [JsonPropertyName("vk_binary")]
    public string VkBinary { get; set; } = string.Empty;
}
