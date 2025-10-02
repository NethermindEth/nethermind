// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
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
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests);
}
