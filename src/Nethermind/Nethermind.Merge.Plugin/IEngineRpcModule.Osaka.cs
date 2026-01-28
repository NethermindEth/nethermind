// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the most recent version of an execution payload and fees with respect to the transaction set contained by the mempool.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<GetPayloadV5Result?>> engine_getPayloadV5(List<byte[]>? txRlp = null, string privKey = "EMPTY", bool reorg = false);

    [JsonRpcMethod(
        Description = "Returns requested blobs and proofs.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> engine_getBlobsV2(byte[][] blobVersionedHashes);

    [JsonRpcMethod(
        Description = "Returns requested blobs and proofs.",
        IsSharable = true,
        IsImplemented = true)]
    public Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> engine_getBlobsV3(byte[][] blobVersionedHashes);
}
