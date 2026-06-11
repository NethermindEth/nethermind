// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Flashbots.Data;

public class BuilderBlockValidationRequest(
    BidTrace message,
    RExecutionPayloadV3 executionPayload,
    BlobsBundleV1 blobsBundle,
    byte[] signature,
    ulong registeredGasLimit,
    Hash256 parentBeaconBlockRoot)
{
    [JsonRequired]
    [JsonPropertyName("message")]
    public BidTrace Message { get; set; } = message ?? throw new JsonException("message must not be null");

    [JsonRequired]
    [JsonPropertyName("execution_payload")]
    public RExecutionPayloadV3 ExecutionPayload { get; set; } = executionPayload ?? throw new JsonException("execution_payload must not be null");

    [JsonRequired]
    [JsonPropertyName("blobs_bundle")]
    public BlobsBundleV1 BlobsBundle { get; set; } = blobsBundle ?? throw new JsonException("blobs_bundle must not be null");

    [JsonPropertyName("signature")]
    public byte[]? Signature { get; set; } = signature;

    [JsonRequired]
    [JsonPropertyName("registered_gas_limit")]
    public ulong RegisteredGasLimit { get; set; } = registeredGasLimit;

    /// <summary>
    /// The block hash of the parent beacon block.
    /// <a href="https:/github.com/flashbots/builder/blob/df9c765067d57ab4b2d0ad39dbb156cbe4965778/eth/block-validation/api.go#L198">See more</a>
    /// </summary>
    [JsonRequired]
    [JsonPropertyName("parent_beacon_block_root")]
    public Hash256 ParentBeaconBlockRoot { get; set; } = parentBeaconBlockRoot ?? throw new JsonException("parent_beacon_block_root must not be null");
}
