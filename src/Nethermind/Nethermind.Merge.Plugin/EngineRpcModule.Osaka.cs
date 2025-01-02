// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    /// <summary>
    /// Method parameter list is extended with <see cref="SlotNumber"/> parameter.
    /// <see href="https://eips.ethereum.org/EIPS/eip-7843">EIP-7843</see>.
    /// </summary>
    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, ulong? slotNumber)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, slotNumber), EngineApiVersions.Osaka);
}
