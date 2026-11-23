// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Messages;

namespace Nethermind.Consensus.Validators;

/// <summary>Gives a block received without its EIP-8288 dependency proof the proof the EL stored for it.</summary>
/// <remarks>A block still missing its proof is accepted only when <see cref="DependencyProofFinality"/> trusts it: the one
/// path that accepts a nonempty proof commitment without verifying the proof.</remarks>
public sealed class DependencyProofAvailabilityBlockValidator(IBlockValidator inner, IDependencyProofStore proofStore, IBlockTree blockTree)
    : IBlockValidator
{
    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) =>
        inner.Validate(header, parent, isUncle, out error);

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) =>
        inner.Validate(header, parent, isUncle, out error, validateHash);

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphaned(header, out error);

    public bool ValidateWithdrawals(Block block, out string? error) =>
        inner.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) =>
        TrustFinality(block, inner.ValidateOrphanedBlock(proofStore.WithProof(block), out error), ref error);

    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true) =>
        TrustFinality(block, inner.ValidateSuggestedBlock(proofStore.WithProof(block), parent, out error, validateHashes), ref error);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) =>
        inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) =>
        inner.ValidateBodyAgainstHeader(header, toBeValidated, out error);

    private bool TrustFinality(Block block, bool valid, [NotNullWhen(false)] ref string? error)
    {
        if (valid || error != BlockErrorMessages.MissingDependencyProof || !DependencyProofFinality.IsTrusted(blockTree, block.Header))
            return valid;

        error = null;
        return true;
    }
}
