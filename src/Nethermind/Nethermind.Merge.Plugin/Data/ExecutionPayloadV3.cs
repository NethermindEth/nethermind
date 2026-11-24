// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV3</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV3 : ExecutionPayload, IExecutionPayloadFactory<ExecutionPayloadV3>, IJsonOnDeserializing
{
    /// <summary>
    /// The fields <c>engine_newPayload</c> must receive, present and non-<c>null</c>, for this payload version.
    /// </summary>
    private protected virtual PayloadFields RequiredFields => PayloadFields.V3;

    /// <inheritdoc/>
    /// <remarks>Arms presence tracking: <c>engine_newPayloadV3</c>+ must reject an omitted or <c>null</c> field.</remarks>
    void IJsonOnDeserializing.OnDeserializing() => _unboundFields = RequiredFields;

    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV3, new()
    {
        TExecutionPayload executionPayload = ExecutionPayload.Create<TExecutionPayload>(block);
        executionPayload.ParentBeaconBlockRoot = block.ParentBeaconBlockRoot;
        executionPayload.BlobGasUsed = block.BlobGasUsed;
        executionPayload.ExcessBlobGas = block.ExcessBlobGas;
        executionPayload.InclusionListTransactions = block.InclusionListTransactions is null ? [] : InclusionListDecoder.Encode(block.InclusionListTransactions);
        if (block.Header.RecursiveStark is not null)
        {
            executionPayload.DependencyProof = block.StarkProof ?? throw new System.InvalidOperationException("Missing block dependency proof");
        }

        return executionPayload;
    }

    public new static ExecutionPayloadV3 Create(Block block) => Create<ExecutionPayloadV3>(block);

    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        Result<Block> baseResult = base.TryGetBlock(totalDifficulty);
        if (baseResult.IsError)
        {
            return baseResult;
        }

        Block block = baseResult.Data;
        block.Header.ParentBeaconBlockRoot = ParentBeaconBlockRoot;
        block.Header.BlobGasUsed = BlobGasUsed;
        block.Header.ExcessBlobGas = ExcessBlobGas;
        block.Header.RequestsHash = ExecutionRequests is not null ? ExecutionRequestExtensions.CalculateHashFromFlatEncodedRequests(ExecutionRequests) : null;
        if (InclusionLists is not null)
        {
            // ValidateParams has already checked each list's shape. Each list is decoded on its own, so its proof stays
            // with exactly its transactions.
            CommitteeInclusionList[] lists = InclusionListV1.Decode(InclusionLists);
            block.InclusionLists = lists;
            block.InclusionListTransactions = CommitteeInclusionList.Flatten(lists);
        }
        else
        {
            block.InclusionListTransactions = InclusionListTransactions is not null ? TxsDecoder.DecodeTxsBorrowingBuffers(InclusionListTransactions, skipErrors: true).Transactions : null;
        }
        if (DependencyProof is not null)
        {
            if (DependencyProof.Length > Eip8288Constants.MaxAggregateProofBytes)
            {
                return Result<Block>.Fail($"{nameof(DependencyProof)} exceeds the proof size limit");
            }

            block.StarkProof = DependencyProof;
            block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(DependencyProof), new Hash256(Eip8288Dependencies.ComputeBlockDepsHash(block, FrameTxSigHash.ComputeValue)));
        }

        return baseResult;
    }

    public override bool ValidateForkOnNewPayload(ISpecProvider specProvider, int newPayloadVersion)
    {
        IReleaseSpec spec = specProvider.GetSpec(BlockNumber, Timestamp);
        // V3 covers Cancun and V4 covers Prague until Amsterdam; Osaka added no newPayload version.
        return spec.IsCancunEnabled
            && !spec.IsAmsterdamEnabled
            && spec.IsPragueEnabled == (newPayloadVersion >= EngineApiVersions.NewPayload.V4);
    }

    /// <summary>
    /// Gets or sets <see cref="Block.BlobGasUsed"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    public sealed override ulong? BlobGasUsed { get; set => field = Bind(value, PayloadFields.BlobGasUsed); }

    /// <summary>
    /// Gets or sets <see cref="Block.ExcessBlobGas"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    public sealed override ulong? ExcessBlobGas { get; set => field = Bind(value, PayloadFields.ExcessBlobGas); }

    /// <summary>The EIP-8288 <c>dependencyProof</c>, which only the execution payload carries.</summary>
    /// <remarks>Its Keccak-256 becomes the header's <c>dependency_proof_hash</c> before the block hash is checked; a payload
    /// without it builds a header without the EIP-8288 fields.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? DependencyProof { get; set; }
}
