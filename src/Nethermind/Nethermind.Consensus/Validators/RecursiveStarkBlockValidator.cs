// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Validators;

/// <summary>Adds EIP-8288 recursive STARK verification to suggested and orphaned block validation.</summary>
/// <remarks>
/// Runs after the inner validator accepts the block, so a block it rejects keeps its error. The inner validator checks that
/// the body carries the <c>stark_proof</c> its header commits to; this one checks that <c>block_deps_hash</c> commits to the
/// block's transaction dependencies and that the proof verifies against the block fork's <c>AGGREGATED_VK</c>, or is empty
/// when there are none. Validators built without the container must wrap themselves in it, or the check does not run.
/// </remarks>
public sealed class RecursiveStarkBlockValidator(
    IBlockValidator inner,
    ILeanProofVerifier leanProofVerifier,
    ISpecProvider specProvider,
    ILogManager logManager) : IBlockValidator
{
    private readonly ILogger _logger = logManager.GetClassLogger<RecursiveStarkBlockValidator>();

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) =>
        inner.Validate(header, parent, isUncle, out error);

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) =>
        inner.Validate(header, parent, isUncle, out error, validateHash);

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphaned(header, out error);

    public bool ValidateWithdrawals(Block block, out string? error) =>
        inner.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphanedBlock(block, out error) && ValidateRecursiveStark(block, out error);

    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true) =>
        inner.ValidateSuggestedBlock(block, parent, out error, validateHashes) && ValidateRecursiveStark(block, out error);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) =>
        inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) =>
        inner.ValidateBodyAgainstHeader(header, toBeValidated, out error);

    private bool ValidateRecursiveStark(Block block, [NotNullWhen(false)] out string? error)
    {
        error = null;
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        if (!spec.IsEip8288Enabled) return true;

        // Fails closed should the inner validator not have required the commitment and its proof.
        if (block.Header.RecursiveStark is not { } recursiveStark || block.StarkProof is not { } proof)
        {
            error = BlockErrorMessages.MissingRecursiveStark;
            return false;
        }

        List<FrameDependency> dependencies = Eip8288Dependencies.ForBlock(block, FrameTxSigHash.ComputeValue);
        Hash256 computed = new(Eip8288Dependencies.ComputeDepsHash(dependencies));
        if (recursiveStark.BlockDepsHash != computed)
        {
            error = BlockErrorMessages.InvalidBlockDepsHash(recursiveStark.BlockDepsHash, computed);
            if (_logger.IsWarn) _logger.Warn($"Block deps hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {recursiveStark.BlockDepsHash}, got {computed}");
            return false;
        }

        if (!RecursiveStarkAggregator.VerifyStarkCheck(leanProofVerifier, Eip8288Verifier.For(spec), dependencies.Count, in computed.ValueHash256, proof))
        {
            error = BlockErrorMessages.InvalidRecursiveStark;
            if (_logger.IsWarn) _logger.Warn($"Invalid recursive STARK in block {block.ToString(Block.Format.FullHashAndNumber)}");
            return false;
        }

        return true;
    }
}
