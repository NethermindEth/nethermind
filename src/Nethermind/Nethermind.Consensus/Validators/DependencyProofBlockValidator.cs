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

/// <summary>Verifies the EIP-8288 dependency proof against the block's dependencies and the fork's <c>AGGREGATED_VK</c>
/// in suggested and orphaned block validation.</summary>
/// <remarks>Runs after the inner validator accepts the block and must sit inside <see cref="DependencyProofAvailabilityBlockValidator"/>
/// to see the stored proof. Validators built without the container must add it themselves, or the check does not run.</remarks>
public sealed class DependencyProofBlockValidator(
    IBlockValidator inner,
    ILeanProofVerifier leanProofVerifier,
    ISpecProvider specProvider,
    ILogManager logManager) : IBlockValidator
{
    private readonly ILogger _logger = logManager.GetClassLogger<DependencyProofBlockValidator>();

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) =>
        inner.Validate(header, parent, isUncle, out error);

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) =>
        inner.Validate(header, parent, isUncle, out error, validateHash);

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphaned(header, out error);

    public bool ValidateWithdrawals(Block block, out string? error) =>
        inner.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphanedBlock(block, out error) && ValidateDependencyProof(block, out error);

    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true) =>
        inner.ValidateSuggestedBlock(block, parent, out error, validateHashes) && ValidateDependencyProof(block, out error);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) =>
        inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) =>
        inner.ValidateBodyAgainstHeader(header, toBeValidated, out error);

    private bool ValidateDependencyProof(Block block, [NotNullWhen(false)] out string? error)
    {
        error = null;
        // A block without the commitment needs no spec lookup: the inner validator has already required it from EIP-8288 on.
        if (block.Header.RecursiveStark is not { } recursiveStark) return true;

        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        if (!spec.IsEip8288Enabled) return true;

        byte[]? proof = block.StarkProof ?? (recursiveStark.DependencyProofHash == Keccak.OfAnEmptyString ? [] : null);
        if (proof is null)
        {
            error = BlockErrorMessages.MissingDependencyProof;
            return false;
        }

        List<FrameDependency> dependencies = Eip8288Dependencies.ForBlock(block, FrameTxSigHash.ComputeValue);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        if (!RecursiveStarkAggregator.VerifyStarkCheck(leanProofVerifier, Eip8288Verifier.For(spec), dependencies.Count, in depsHash, proof))
        {
            error = BlockErrorMessages.InvalidRecursiveStark;
            if (_logger.IsWarn) _logger.Warn($"Invalid recursive STARK in block {block.ToString(Block.Format.FullHashAndNumber)}");
            return false;
        }

        return true;
    }
}
