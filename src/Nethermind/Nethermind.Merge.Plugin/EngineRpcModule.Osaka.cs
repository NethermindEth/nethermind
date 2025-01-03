// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    public Task<ResultWrapper<InclusionList>> engine_getInclusionList()
        => GetInclusionList(EngineApiVersions.Osaka);

    /// <summary>
    /// Method parameter list is extended with <see cref="InclusionList"/> parameter.
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, InclusionList? inclusionList)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionList), EngineApiVersions.Osaka);

    protected async Task<ResultWrapper<InclusionList>> GetInclusionList(int version)
    {
        // todo: fetch from local mempool
        await Task.Delay(0);
        return ResultWrapper<InclusionList>.Success(new InclusionList());
    }
}
