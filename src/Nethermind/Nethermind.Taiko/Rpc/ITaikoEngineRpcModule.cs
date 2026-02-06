// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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

    [JsonRpcMethod(
        Description = "Retrieves the transaction pool content with the given upper limits including minimum tip.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContentWithMinTip(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit, ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists, ulong minTip);

    [JsonRpcMethod(
        Description = "Updates head L1 origin.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<UInt256> taikoAuth_setHeadL1Origin(UInt256 blockId);

    [JsonRpcMethod(
        Description = "Updates L1 origin.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<L1Origin> taikoAuth_updateL1Origin(L1Origin l1Origin);

    [JsonRpcMethod(
        Description = "Sets the mapping from batch ID to the last block ID in this batch.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<UInt256> taikoAuth_setBatchToLastBlock(UInt256 batchId, UInt256 blockId);

    [JsonRpcMethod(
        Description = "Sets the L1 origin signature for the given block ID.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<L1Origin> taikoAuth_setL1OriginSignature(UInt256 blockId, int[] signature);

    /// <summary>
    /// Clears txpool state (hash cache, account cache, pending transactions) after a chain reorg.
    /// This is specifically designed for Taiko integration tests where the chain is reset to a base block.
    /// After a reorg, stale txpool caches would reject transaction resubmissions with "already known" or "nonce too low".
    /// Pending transactions must also be cleared because tests resubmit transactions with the same hash/nonce,
    /// which would be rejected as "ReplacementNotAllowed" if they remain in the pool.
    /// </summary>
    [JsonRpcMethod(
        Description = "Clears txpool state after chain reorg for testing/debugging purposes. " +
                      "Returns true on success.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<bool> taikoDebug_clearTxPoolForReorg();

    [JsonRpcMethod(
        Description = "Returns the L1 origin of the last block for the given batch.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<L1Origin?>> taikoAuth_lastL1OriginByBatchID(UInt256 batchId);

    [JsonRpcMethod(
        Description = "Returns the ID of the last block for the given batch.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<UInt256?>> taikoAuth_lastBlockIDByBatchID(UInt256 batchId);
}
