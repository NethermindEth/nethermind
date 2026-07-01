// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// Recovers transaction senders (and EIP-7702 authorities) before delegating to the inner validator.
/// </summary>
/// <remarks>
/// Ingress validation runs ahead of the <see cref="RecoverSignatures"/> preprocessor, yet EIP-2780's
/// intrinsic-gas check is sender-dependent; recovering here keeps <see cref="BlockValidator"/> pure.
/// </remarks>
public class RecoveringBlockValidator(
    IBlockValidator baseValidator,
    IEthereumEcdsa ecdsa,
    ISpecProvider specProvider,
    ILogManager logManager) : IBlockValidator
{
    private readonly RecoverSignatures _recovery = new(ecdsa, specProvider, logManager);

    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true)
    {
        _recovery.RecoverData(block);
        return baseValidator.ValidateSuggestedBlock(block, parent, out error, validateHashes);
    }

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error)
    {
        _recovery.RecoverData(block);
        return baseValidator.ValidateOrphanedBlock(block, out error);
    }

    // Remaining members need no recovery: processed blocks already carry senders; header/body checks don't use them.
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) =>
        baseValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) =>
        baseValidator.ValidateBodyAgainstHeader(header, toBeValidated, out error);

    public bool ValidateWithdrawals(Block block, out string? error) =>
        baseValidator.ValidateWithdrawals(block, out error);

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) =>
        baseValidator.Validate(header, parent, isUncle, out error);

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        baseValidator.ValidateOrphaned(header, out error);
}
