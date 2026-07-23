// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV3</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV3 : ExecutionPayload, IExecutionPayloadFactory<ExecutionPayloadV3>
{
    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV3, new()
    {
        TExecutionPayload executionPayload = ExecutionPayload.Create<TExecutionPayload>(block);
        executionPayload.ParentBeaconBlockRoot = block.ParentBeaconBlockRoot;
        executionPayload.BlobGasUsed = block.BlobGasUsed;
        executionPayload.ExcessBlobGas = block.ExcessBlobGas;
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
        block.Header.RecursiveStark = RecursiveStarkProof is null ? null : new RecursiveStark(RecursiveStarkProof, new Hash256(RecursiveStarkBlockDepsHash!));
        return baseResult;
    }

    public override bool ValidateFork(ISpecProvider specProvider)
         => specProvider.GetSpec(BlockNumber, Timestamp).IsEip4844Enabled;

    /// <summary>
    /// Gets or sets <see cref="Block.BlobGasUsed"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonRequired]
    public sealed override ulong? BlobGasUsed { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ExcessBlobGas"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4844">EIP-4844</see>.
    /// </summary>
    [JsonRequired]
    public sealed override ulong? ExcessBlobGas { get; set; }

    /// <summary>
    /// EIP-8288 <c>recursive_stark</c> proof and its <c>block_deps_hash</c>. Optional and present only
    /// when the block declares dependencies (from the Osaka-based prototype onward), so payloads for
    /// forks without EIP-8288 are byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? RecursiveStarkProof { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? RecursiveStarkBlockDepsHash { get; set; }
}

