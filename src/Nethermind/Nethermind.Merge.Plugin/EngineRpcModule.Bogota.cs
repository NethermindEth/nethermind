// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    /// <summary>
    /// EIP-7805 (FOCIL): returns an inclusion list of pending mempool transactions
    /// for the given block hash. The CL aggregates ILs from committee members and
    /// later passes the result back via <c>engine_forkchoiceUpdatedV5</c> /
    /// <c>engine_newPayloadV6</c>. See
    /// <see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>.
    /// </summary>
    public Task<ResultWrapper<ArrayPoolList<byte[]>>> engine_getInclusionListV1(Hash256 blockHash)
        => getInclusionListTransactionsHandler.Handle(blockHash);

    /// <summary>
    /// Method parameter list is extended with <c>inclusionListTransactions</c> parameter.
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV6(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV3>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions), EngineApiVersions.NewPayload.V6);

    /// <summary>
    /// EIP-7805 (FOCIL): <c>payloadAttributes</c> is extended (PayloadAttributesV5) with
    /// <c>inclusionListTransactions</c>. The IL is staged into the producer tx-source
    /// pipeline before the standard FCU runs so the new payload picks it up.
    /// <see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>.
    /// </summary>
    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        byte[]? custodyColumns = null)
    {
        if (payloadAttributes?.InclusionListTransactions is { Length: > 0 } ilTxs)
        {
            IReleaseSpec spec = _specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp));
            inclusionListTxSource.Set(ilTxs, spec);
        }
        // custodyColumns: a 16-byte bitarray indicating column custody set (EIP-7805 §IL committee).
        // No EL-side processing required today; recorded here for future blob-column gating.
        return ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);
    }
}
