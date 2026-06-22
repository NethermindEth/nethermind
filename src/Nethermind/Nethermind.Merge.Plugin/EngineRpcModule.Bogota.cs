// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    /// <summary>
    /// returns an IL of pending mempool txs for <paramref name="blockHash"/>.
    /// The CL aggregates per-committee ILs and feeds the result back via FCUv5 / newPayloadV6.
    /// </summary>
    public Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256 blockHash)
        => getInclusionListTransactionsHandler.Handle(blockHash);

    /// <summary>
    /// on top of Amsterdam's <see cref="ExecutionPayloadV4"/>: adds an
    /// <c>inclusionListTransactions</c> parameter validated against the post-execution state;
    /// returns <see cref="PayloadStatus.InclusionListUnsatisfied"/> when any IL tx is
    /// valid but missing.
    /// </summary>
    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions), EngineApiVersions.NewPayload.V6);

    /// <summary>
    /// PayloadAttributesV5 carries <c>inclusionListTransactions</c>; staged
    /// into the producer tx-source pipeline before the FCU runs.
    /// </summary>
    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        byte[]? custodyColumns = null)
    {
        if (payloadAttributes?.InclusionListTransactions is { } ilTxs)
        {
            IReleaseSpec spec = _specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp));
            // unparsable IL items are a no-op, not a protocol error.
            try
            {
                inclusionListTxSource.Set(ilTxs, spec);
            }
            catch (Exception ex) when (ex is RlpException or ArgumentException)
            {
                if (_logger.IsDebug) _logger.Debug($"engine_forkchoiceUpdatedV5: discarding malformed inclusion list ({ex.GetType().Name}: {ex.Message})");
            }
        }
        return ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);
    }
}
