// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<GetPayloadV5Result?>> engine_getPayloadV5(byte[] payloadId);

    [JsonRpcMethod(
        Description = "Returns requested blobs and proofs.",
        IsSharable = true,
        IsImplemented = true)]
    [Get("/eth/v2/engine/blobs")]
    public Task<ResultWrapper<ICollection<BlobAndProofV2>?>> engine_getBlobsV2(byte[][] versionedHashes);

    [JsonRpcMethod(
        Description = "Returns requested blobs and proofs.",
        IsSharable = true,
        IsImplemented = true)]
    [Get("/eth/v3/engine/blobs")]
    public Task<ResultWrapper<ICollection<NullableBlobAndProofV2>>> engine_getBlobsV3(byte[][] versionedHashes);
}
