// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Builds an inclusion list from the local mempool.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<InclusionListBytes>> engine_getInclusionListV1(Hash256? parentBlockHash = null);

    [JsonRpcMethod(
        Description = "Checks a signed inclusion list's EIP-8288 recursive_stark: VALID when a held verified proof under the commitment covers the list, INVALID when its dependencies omit one of the list's or the supplied il_proof fails, PROOF_UNKNOWN when the proof is not held.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<InclusionListProofStatusV1> engine_verifyInclusionListProofV1(byte[][] transactions, RecursiveStarkV1 recursiveStark,
        InclusionListProofV1? ilProof = null);

    [JsonRpcMethod(
        Description = "Returns the EIP-8288 il_proof [stark_proof, proof_deps] held under a stark_proof_hash, from a validated mode-1 wrapper or a verified il_proof, or null.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<InclusionListProofV1?> engine_getInclusionListProofV1(Hash256 starkProofHash);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status (including inclusion-list compliance) and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions);

    [JsonRpcMethod(
        Description = "Verifies the payload according to the execution environment rules and returns the verification status, hash of the last valid block, and the execution witness when the payload is valid.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV6(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, byte[][]? inclusionListTransactions);

    [JsonRpcMethod(
        Description = "Verifies the payload against each counted inclusion list, whose obligations follow from its EIP-8288 commitment, and returns the verification status (including inclusion-list compliance) and hash of the last valid block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<PayloadStatusV2>> engine_newPayloadV7(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, InclusionListV1[]? inclusionLists);

    [JsonRpcMethod(
        Description = "Verifies the payload against each counted inclusion list, whose obligations follow from its EIP-8288 commitment, and returns the verification status, hash of the last valid block, and the execution witness when the payload is valid.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitnessV7(ExecutionPayloadV4 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests, InclusionListV1[]? inclusionLists);

    [JsonRpcMethod(
        Description = "Applies fork choice and starts building a new block if payload attributes are present.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV6(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        [JsonRpcParameter(ConverterType = typeof(BlobCellBitArrayConverter))] BitArray? custodyColumns = null);

    [JsonRpcMethod(
        Description = "Applies fork choice and starts building a new block if payload attributes are present.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<ForkchoiceUpdatedV2Result>> engine_forkchoiceUpdatedV5(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null,
        [JsonRpcParameter(ConverterType = typeof(BlobCellBitArrayConverter))] BitArray? custodyColumns = null);
}
