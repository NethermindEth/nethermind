// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.BeaconChain.Engine;

/// <summary>
/// Engine RPC decorator that reports externally issued block-progression calls
/// (<c>engine_newPayload*</c>/<c>engine_forkchoiceUpdated*</c>) to <see cref="ExternalClDetector"/>.
/// </summary>
/// <remarks>
/// Both the JSON-RPC module pool and the SSZ-REST handlers resolve <see cref="IEngineRpcModule"/>
/// from the container, so this decorator wraps every external path. The embedded driver bypasses
/// it via <see cref="ExternalClDetector.InnerEngine"/>, which the constructor populates.
/// Only the standard consensus-client entry points count as progression: the WithWitness variants
/// are Nethermind extensions used by stateless tooling, and treating them as an external consensus
/// client would permanently disable the embedded driver by mistake.
/// </remarks>
public sealed class ExternalClInterceptingEngineRpcModule : IEngineRpcModule
{
    private readonly IEngineRpcModule _engine;
    private readonly ExternalClDetector _detector;

    public ExternalClInterceptingEngineRpcModule(IEngineRpcModule engine, ExternalClDetector detector)
    {
        _engine = engine;
        _detector = detector;
        detector.SetInner(engine);
    }

    /// <summary>The inner engine, with the external-CL detection side effect applied first.</summary>
    private IEngineRpcModule Detected
    {
        get
        {
            _detector.OnExternalEngineCall();
            return _engine;
        }
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(ExecutionPayload executionPayload)
        => Detected.engine_newPayloadV1(executionPayload);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(ExecutionPayload executionPayload)
        => Detected.engine_newPayloadV2(executionPayload);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayloadV3 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot)
        => Detected.engine_newPayloadV3(executionPayload, blobVersionedHashes, parentBeaconBlockRoot);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV4(ExecutionPayloadV3 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => Detected.engine_newPayloadV4(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => Detected.engine_newPayloadV5(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

    public Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => Detected.engine_newPayloadV6(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions);

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => Detected.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes);

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => Detected.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes);

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null)
        => Detected.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes);

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, BitArray? custodyColumns = null)
        => Detected.engine_forkchoiceUpdatedV4(forkchoiceState, payloadAttributes, custodyColumns);

    public Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV5(ForkchoiceStateV1 forkchoiceState, PayloadAttributes? payloadAttributes = null, BitArray? custodyColumns = null)
        => Detected.engine_forkchoiceUpdatedV5(forkchoiceState, payloadAttributes, custodyColumns);

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV4(ExecutionPayloadV3 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => _engine.engine_newPayloadWithWitnessV4(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV5(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => _engine.engine_newPayloadWithWitnessV5(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions)
        => _engine.engine_newPayloadWithWitnessV6(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests, inclusionListTransactions);

    public ResultWrapper<IReadOnlyList<string>> engine_exchangeCapabilities(IEnumerable<string> methods)
        => _engine.engine_exchangeCapabilities(methods);

    public ResultWrapper<ClientVersionV1[]> engine_getClientVersionV1(ClientVersionV1 clientVersionV1)
        => _engine.engine_getClientVersionV1(clientVersionV1);

    public ResultWrapper<TransitionConfigurationV1> engine_exchangeTransitionConfigurationV1(TransitionConfigurationV1 beaconTransitionConfiguration)
        => _engine.engine_exchangeTransitionConfigurationV1(beaconTransitionConfiguration);

    public Task<ResultWrapper<ExecutionPayload?>> engine_getPayloadV1(byte[] payloadId)
        => _engine.engine_getPayloadV1(payloadId);

    public Task<ResultWrapper<GetPayloadV2Result?>> engine_getPayloadV2(byte[] payloadId)
        => _engine.engine_getPayloadV2(payloadId);

    public Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId)
        => _engine.engine_getPayloadV3(payloadId);

    public Task<ResultWrapper<GetPayloadV4Result?>> engine_getPayloadV4(byte[] payloadId)
        => _engine.engine_getPayloadV4(payloadId);

    public Task<ResultWrapper<GetPayloadV5Result?>> engine_getPayloadV5(byte[] payloadId)
        => _engine.engine_getPayloadV5(payloadId);

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _engine.engine_getPayloadV6(payloadId);

    public ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>> engine_getPayloadBodiesByHashV1(IReadOnlyList<Hash256> blockHashes)
        => _engine.engine_getPayloadBodiesByHashV1(blockHashes);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> engine_getPayloadBodiesByRangeV1(ulong start, ulong count)
        => _engine.engine_getPayloadBodiesByRangeV1(start, count);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(IReadOnlyList<Hash256> blockHashes)
        => _engine.engine_getPayloadBodiesByHashV2(blockHashes);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(ulong start, ulong count)
        => _engine.engine_getPayloadBodiesByRangeV2(start, count);

    public Task<ResultWrapper<IReadOnlyList<BlobAndProofV1?>>> engine_getBlobsV1(byte[][] blobVersionedHashes)
        => _engine.engine_getBlobsV1(blobVersionedHashes);

    public Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> engine_getBlobsV2(byte[][] blobVersionedHashes)
        => _engine.engine_getBlobsV2(blobVersionedHashes);

    public Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> engine_getBlobsV3(byte[][] blobVersionedHashes)
        => _engine.engine_getBlobsV3(blobVersionedHashes);

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> engine_getBlobsV4(byte[][] blobVersionedHashes, BitArray indicesBitarray)
        => _engine.engine_getBlobsV4(blobVersionedHashes, indicesBitarray);

    public Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256? parentBlockHash = null)
        => _engine.engine_getInclusionListV1(parentBlockHash);
}
