// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns an EIP-7805 inclusion list built from the local mempool.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<byte[][]>> engine_getInclusionListV1();

    [JsonRpcMethod(
        Description = "Verifies the payload including EIP-7805 inclusion list satisfaction and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionList);
}
