// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>Re-attaches the stored EIP-8288 <c>stark_proof</c> to a block queued for processing without one.</summary>
/// <remarks>
/// The block store keeps the proof apart from the body, so a block reloaded from it has none: the replay after a restart,
/// a reorg back onto a stored branch, or a block processed after sync stored it. An invalid block is reported as such only
/// when its body carries the proof its header commits to, so without one it would never be marked invalid. A proof missing
/// from the store leaves the block without it: the data is not available, which is not proof of an invalid block.
/// </remarks>
public sealed class StarkProofRecoveryStep(IStarkProofStore proofStore) : IBlockPreprocessorStep
{
    /// <inheritdoc/>
    /// <remarks>Only queued processing re-attaches the stored proof; tracing and one-time processing do not read it.</remarks>
    public void RecoverData(Block block) { }

    /// <inheritdoc/>
    public void RecoverDataForQueuedProcessing(Block block)
    {
        if (block.StarkProof is not null || block.Header.RecursiveStark is null || block.Hash is not { } blockHash) return;

        block.StarkProof = proofStore.Get(block.Number, blockHash);
    }
}
