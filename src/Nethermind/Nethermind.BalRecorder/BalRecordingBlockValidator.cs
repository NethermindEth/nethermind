// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Crypto;

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

    public bool ValidateBodyAgainstHeader(BlockHeader header, RlpBlockBody rawBody, [NotNullWhen(false)] out string? error) =>
        inner.ValidateBodyAgainstHeader(header, rawBody, out error);

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error)
    {
        if (balSwitch.Enabled && suggestedBlock.Header.BlockAccessListHash is null)
        {
            // The suggested block is a pre-Prague draft (no BlockAccessListHash in its sealed header).
            // Our spec switch caused SetBlockAccessList to populate BlockAccessListHash on the
            // processed block, which would fail the per-field comparison. Strip it and recompute
            // the hash so the Hash == Hash short-circuit fires instead. This is safe because the
            // SHA-3 pre-image guarantee means equal hashes imply all other header fields are equal.
            // Note: store.Insert() in BalRecordingBlockProcessor runs after this method returns and
            // uses block.Number as the key, so the stripped hash does not affect BAL persistence.
            processedBlock.Header.BlockAccessListHash = null;
            processedBlock.Header.Hash = processedBlock.Header.CalculateHash();
        }
        return inner.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);
    }
}
