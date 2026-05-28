// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

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
    /// Bogota (EIP-7805 FOCIL) layered on Amsterdam's <see cref="ExecutionPayloadV4"/>: identical
    /// payload structure to <c>engine_newPayloadV5</c> plus a new <c>inclusionListTransactions</c>
    /// parameter that the EL validates against the post-execution state and returns
    /// <see cref="PayloadStatus.InvalidInclusionList"/> when any IL tx is valid but missing.
    /// <see href="https://github.com/ethereum/execution-apis/pull/609">execution-apis#609</see>.
    /// </summary>
    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions), EngineApiVersions.NewPayload.V6);

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
        // Set on every V5 FCU, INCLUDING an empty IL, so the previous slot's IL is overwritten
        // (drained) rather than leaking into the next production cycle. The previous gate
        // `{ Length: > 0 }` skipped Set() for empty arrays, leaving stale IL in the source.
        if (payloadAttributes?.InclusionListTransactions is { } ilTxs)
        {
            IReleaseSpec spec = _specProvider.GetSpec(ForkActivation.TimestampOnly(payloadAttributes.Timestamp));
            // Malformed entries (garbage bytes, truncated RLP) must not abort the FCU —
            // EIP-7805 §"Validation" treats unparsable IL items as a no-op rather than a
            // protocol error. The decoder already skips entries it can't read, but a single
            // bad item early in the array can still surface as e.g. an
            // IndexOutOfRangeException from the RLP context, or an ArgumentException from
            // the RLP buffer-bounds guards. Narrow the catch to those expected decode faults
            // so genuine bugs (NRE, OOM, …) still surface; log so anomalous IL traffic from a
            // misbehaving CL is observable rather than silent.
            try
            {
                inclusionListTxSource.Set(ilTxs, spec);
            }
            catch (Exception ex) when (ex is RlpException or ArgumentException or IndexOutOfRangeException)
            {
                if (_logger.IsDebug) _logger.Debug($"engine_forkchoiceUpdatedV5: discarding malformed inclusion list ({ex.GetType().Name}: {ex.Message})");
            }
        }
        // custodyColumns: a 16-byte bitarray indicating column custody set (EIP-7805 §IL committee).
        // No EL-side processing required today; recorded here for future blob-column gating.
        return ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V5);
    }
}
