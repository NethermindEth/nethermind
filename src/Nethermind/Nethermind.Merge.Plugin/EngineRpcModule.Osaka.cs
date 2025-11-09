// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV5Result?> _getPayloadHandlerV5;
    private readonly IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>?> _getBlobsHandlerV2;

    public Task<ResultWrapper<GetPayloadV5Result?>> engine_getPayloadV5(List<byte[]>? txRlp = null, string privKey = "EMPTY", bool reorg = false)
        => _getPayloadHandlerV5.HandleAsync(txRlp, privKey, reorg);

    public Task<ResultWrapper<IEnumerable<BlobAndProofV2>?>> engine_getBlobsV2(byte[][] blobVersionedHashes)
         => _getBlobsHandlerV2.HandleAsync(blobVersionedHashes);
}
