// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace EthProofValidator.src.Models
{
    public class ProofMetadata
    {
        [JsonPropertyName("proof_id")]
        public long ProofId { get; set; }

        [JsonPropertyName("block_number")]
        public long BlockNumber { get; set; }

        [JsonPropertyName("proof_status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("cluster_id")]
        public string ClusterId { get; set; } = string.Empty;

        [JsonPropertyName("cluster_version")]
        public Cluster Cluster { get; set; } = new();
    }

    public class ProofResponse
    {
        [JsonPropertyName("rows")]
        public List<ProofMetadata> Rows { get; set; } = [];
    }

    public class Cluster
    {
        [JsonPropertyName("cluster_id")]
        public string ClusterId { get; set; } = string.Empty;

        [JsonPropertyName("zkvm_version")]
        public ZkvmVersion ZkvmVersion { get; set; } = new();
    }

    public class ZkvmVersion {
        [JsonPropertyName("zkvm")]
        public ZkVm ZkVm { get; set; } = new();
    }

    public class ZkVm
    {
        [JsonPropertyName("slug")]
        public string Type { get; set; } = string.Empty;
    }

}
