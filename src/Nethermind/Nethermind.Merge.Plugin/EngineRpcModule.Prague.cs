// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    readonly IAsyncHandler<byte[], GetPayloadV4Result?> _getPayloadHandlerV4;

    /// <summary>
    /// Method parameter list is extended with <see cref="ExecutionRequets"/> parameter.
    /// <see href="https://eips.ethereum.org/EIPS/eip-7685">EIP-7685</see>.
    /// </summary>
    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.Prague);

    public Task<ResultWrapper<GetPayloadV4Result?>> engine_getPayloadV4(List<byte[]>? txRlp = null, string privKey = "EMPTY", bool reorg= false)
        => _getPayloadHandlerV4.HandleAsync(txRlp, privKey);
}
