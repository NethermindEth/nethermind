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
        Description = "Verifies an EIP-8146 payload whose block access list travels as an independent sidecar and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV6(ExecutionPayloadV5 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests);

    [JsonRpcMethod(
        Description = "Delivers an RLP-encoded EIP-8146 block access list sidecar ahead of the matching payload, enabling early prefetching and parallel execution.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<string?>> engine_notifyBlockAccessListV1(byte[] blockAccessList);
}
