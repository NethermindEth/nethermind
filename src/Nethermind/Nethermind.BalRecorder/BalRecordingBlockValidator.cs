// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.BalRecorder;

public class BalRecordingBlockValidator(IBlockValidator inner, BalRecorderSpecSwitch balSwitch) : IBlockValidator
{
    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) =>
        inner.Validate(header, parent, isUncle, out error);

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphaned(header, out error);

    public bool ValidateWithdrawals(Block block, out string? error) =>
        inner.ValidateWithdrawals(block, out error);

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) =>
        inner.ValidateOrphanedBlock(block, out error);

    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true) =>
        inner.ValidateSuggestedBlock(block, parent, out error, validateHashes);

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) =>
        inner.ValidateBodyAgainstHeader(header, toBeValidated, out error);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error)
    {
        if (!balSwitch.Enabled)
            return inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);

        // When the BAL switch is active, the processed block may have a computed BlockAccessListHash
        // that the suggested block (sealed pre-Prague) does not. Align the field so the inner validator
        // doesn't reject the block on a BAL hash mismatch we deliberately introduced.
        Hash256? savedHash = suggestedBlock.Header.BlockAccessListHash;
        suggestedBlock.Header.BlockAccessListHash = processedBlock.Header.BlockAccessListHash;
        try
        {
            return inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);
        }
        finally
        {
            suggestedBlock.Header.BlockAccessListHash = savedHash;
        }
    }
}
