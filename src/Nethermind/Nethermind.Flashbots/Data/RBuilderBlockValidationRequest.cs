// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Flashbots.Data;

public class RBuilderBlockValidationRequest
{
    public RBuilderBlockValidationRequest(
        Message message,
        RExecutionPayloadV3 execution_payload,
        BlobsBundleV1 blobs_bundle,
        byte[] signature,
        long registered_gas_limit,
        Hash256 withdrawals_root,
        Hash256 parent_beacon_block_root)
    {
        this.message = message;
        this.execution_payload = execution_payload;
        this.blobs_bundle = blobs_bundle;
        this.signature = signature;
        this.registered_gas_limit = registered_gas_limit;
        this.withdrawals_root = withdrawals_root;
        this.parent_beacon_block_root = parent_beacon_block_root;
    }

    [JsonRequired]
    public Message message { get; set; }

    [JsonRequired]
    public RExecutionPayloadV3 execution_payload { get; set; }

    [JsonRequired]
    public BlobsBundleV1 blobs_bundle { get; set; }

    [JsonRequired]
    public byte[] signature { get; set; }

    [JsonRequired]
    public long registered_gas_limit { get; set; }

    [JsonRequired]
    public Hash256 withdrawals_root { get; set; }

    [JsonRequired]
    public Hash256 parent_beacon_block_root { get; set; }
}
