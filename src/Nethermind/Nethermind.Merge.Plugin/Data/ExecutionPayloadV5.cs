// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV5</c> structure of the Engine API: <c>ExecutionPayloadV4</c>
/// and the EIP-8141 <c>dependencyProof</c>.
/// </summary>
public class ExecutionPayloadV5 : ExecutionPayloadV4, IExecutionPayloadFactory<ExecutionPayloadV5>
{
    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV5, new()
    {
        TExecutionPayload executionPayload = ExecutionPayloadV4.Create<TExecutionPayload>(block);
        executionPayload.DependencyProof = block.DependencyProof ?? [];
        return executionPayload;
    }

    public new static ExecutionPayloadV5 Create(Block block) => Create<ExecutionPayloadV5>(block);

    /// <inheritdoc/>
    private protected override PayloadFields RequiredFields => PayloadFields.V5;

    /// <inheritdoc/>
    /// <remarks>The header's <c>dependency_proof_hash</c> is derived from <see cref="DependencyProof"/>, so a proof other
    /// than the one the block was built with fails the block hash check.</remarks>
    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        // Checked before anything is hashed, as execution-apis requires of engine_newPayloadV7.
        if (DependencyProof is { Length: > Eip8141Constants.MaxDependencyProofBytes })
        {
            return Result<Block>.Fail(FrameTxDependencyProof.TooLarge);
        }

        Result<Block> baseResult = base.TryGetBlock(totalDifficulty);
        if (baseResult.IsError || DependencyProof is null)
        {
            return baseResult;
        }

        Block block = baseResult.Data;
        block.DependencyProof = DependencyProof;
        block.Header.DependencyProofHash = Keccak.Compute(DependencyProof);
        return baseResult;
    }

    public override bool ValidateForkOnNewPayload(ISpecProvider specProvider, int newPayloadVersion) =>
        newPayloadVersion >= EngineApiVersions.NewPayload.V7 && specProvider.GetSpec(BlockNumber, Timestamp).IsEip8141Enabled;

    /// <summary>
    /// Gets or sets <see cref="Block.DependencyProof"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-8141">EIP-8141</see>.
    /// </summary>
    /// <remarks>Neither header nor body: its Keccak-256 becomes the header's <c>dependency_proof_hash</c> before the
    /// block hash is checked.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public sealed override byte[]? DependencyProof { get; set => field = Bind(value, PayloadFields.DependencyProof); }
}
