// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Taiko.Rpc;

[RpcModule(ModuleType.Engine)]
public interface ITaikoEngineRpcModule : IEngineRpcModule
{
    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(TaikoExecutionPayload executionPayload);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(TaikoExecutionPayload executionPayload);

    [JsonRpcMethod(
        Description =
            "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(
        ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(TaikoExecutionPayloadV3 executionPayload,
        byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot);

    [JsonRpcMethod(
        Description = "Retrieves the transaction pool content with the given upper limits.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit, ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists);
}
