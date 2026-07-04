// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IGetInclusionListHandler _getInclusionListHandler = getInclusionListHandler;

    public Task<ResultWrapper<byte[][]>> engine_getInclusionListV1()
        => Task.FromResult(_getInclusionListHandler.Handle());

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionList)
    {
        executionPayload.InclusionList = inclusionList;
        return NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.NewPayload.V6);
    }
}
