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
/// Suggested- and orphaned-block validation run at ingress, ahead of the <see cref="RecoverSignatures"/>
/// preprocessor step, yet EIP-2780 makes the intrinsic-gas check sender-dependent (the self-transfer
/// discount). Recovering here — via the same <see cref="RecoverSignatures"/> logic the processing path
/// uses — keeps <see cref="BlockValidator"/> a pure structural check and ensures the later preprocessor
/// pass (which short-circuits once senders are populated) still finds authorities recovered.
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

    // Remaining members need no recovery: processed blocks already carry senders, and header,
    // withdrawal, and body checks do not depend on transaction senders.
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
