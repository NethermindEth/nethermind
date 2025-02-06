// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Flashbots.Data;

public class SubmitBlockRequest
{
    public SubmitBlockRequest(RExecutionPayloadV3 executionPayload, BlobsBundleV1 blobsBundle, BidTrace message, byte[] signature)
    {
        ExecutionPayload = executionPayload;
        BlobsBundle = blobsBundle;
        Message = message;
        Signature = signature;
    }
    [JsonPropertyName("execution_payload")]
    public RExecutionPayloadV3 ExecutionPayload { get; set; }

    [JsonPropertyName("blobs_bundle")]
    public BlobsBundleV1 BlobsBundle { get; set; }

    [JsonPropertyName("message")]
    public BidTrace Message { get; set; }

    [JsonPropertyName("signature")]
    public byte[] Signature { get; set; }
}
