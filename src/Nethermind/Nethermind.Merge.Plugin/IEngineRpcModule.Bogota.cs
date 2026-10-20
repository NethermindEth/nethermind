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
        Description = "Checks a counted inclusion list's EIP-8288 recursive_stark under the fork at the target block's timestamp: VALID when the proof's bytes are held with a VALID verdict, cover the list and are retained until the attestation deadline (Unix ms), INVALID when the list has no dependencies, the known proof_deps omit one, an INVALID verdict is held or the supplied il_proof fails, PROOF_UNKNOWN when the proof's bytes are not held.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<InclusionListProofStatusV1> engine_verifyInclusionListProofV1(ulong timestamp, byte[][] transactions, RecursiveStarkV1 recursiveStark,
        InclusionListProofV1? ilProof, ulong attestationDeadlineMs);

    [JsonRpcMethod(
        Description = "Returns the EIP-8288 il_proof [stark_proof, proof_deps] whose bytes are held under a stark_proof_hash with a VALID verdict under the fork at the target block's timestamp, or null.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<InclusionListProofV1?> engine_getInclusionListProofV1(ulong timestamp, Hash256 starkProofHash);

    [JsonRpcMethod(
        Description = "Verifies a proof-topic EIP-8288 il_proof under the fork at the target block's timestamp and records its verdict, retaining a valid proof until the view freeze deadline (Unix ms): VALID or INVALID.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<InclusionListProofStatusV1> engine_submitInclusionListProofV1(ulong timestamp, InclusionListProofV1 ilProof, ulong viewFreezeDeadlineMs);

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
