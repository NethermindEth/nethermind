// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Crypto;
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
        if (block.Header.RecursiveStark is { } recursiveStark)
        {
            executionPayload.RecursiveStarkProof = recursiveStark.StarkProof;
            executionPayload.RecursiveStarkBlockDepsHash = recursiveStark.BlockDepsHash.Bytes.ToArray();
        }

        return executionPayload;
    }

    public new static ExecutionPayloadV3 Create(Block block) => Create<ExecutionPayloadV3>(block);

    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        if (InclusionListRecursiveStark is { } inclusionProof
            && (InclusionListTransactions is null || inclusionProof.BlockDepsHash is null
                || inclusionProof.StarkProof is not { Length: > 0 and <= Eip8288Constants.MaxProofBytes }))
        {
            return Result<Block>.Fail("Invalid inclusion-list recursive STARK");
        }

        if (InclusionListProvenDependencies is { } dependencies
            && (InclusionListRecursiveStark is null || InclusionListTransactions is null
                || !Nethermind.Consensus.Eip8288.FocilInclusionListValidator.HasValidMetadataLength(dependencies)))
            return Result<Block>.Fail("Invalid inclusion-list proven dependencies");

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
        block.InclusionListTransactions = InclusionListTransactions is not null ? TxsDecoder.DecodeTxs(InclusionListTransactions, true).Transactions : null;
        block.InclusionListRecursiveStark = InclusionListRecursiveStark;
        block.InclusionListProvenDependencies = InclusionListProvenDependencies;
        if (RecursiveStarkProof is null && RecursiveStarkBlockDepsHash is not null)
        {
            return Result<Block>.Fail($"Missing {nameof(RecursiveStarkProof)}");
        }

        if (RecursiveStarkProof is not null)
        {
            if (RecursiveStarkProof.Length > NativeLeanProofVerifier.MaxProofBytes)
            {
                return Result<Block>.Fail($"{nameof(RecursiveStarkProof)} exceeds the proof size limit");
            }

            if (RecursiveStarkBlockDepsHash is not { Length: Hash256.Size })
            {
                return Result<Block>.Fail($"Invalid {nameof(RecursiveStarkBlockDepsHash)}: expected {Hash256.Size} bytes, got {RecursiveStarkBlockDepsHash?.Length.ToString() ?? "none"}");
            }

            block.Header.RecursiveStark = new RecursiveStark(RecursiveStarkProof, new Hash256(RecursiveStarkBlockDepsHash));
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

    /// <summary>
    /// EIP-8288 <c>recursive_stark</c> proof and its <c>block_deps_hash</c>, present on every block once
    /// the fork is active, so payloads for forks without EIP-8288 are byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? RecursiveStarkProof { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? RecursiveStarkBlockDepsHash { get; set; }
}
