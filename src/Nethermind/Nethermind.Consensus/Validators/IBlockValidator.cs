// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator, IWithdrawalValidator
{
    bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error);
    /// <param name="validateHashes">
    /// <c>false</c> to skip recomputing hashes the caller has already verified: the header hash, the uncles hash,
    /// the transactions root and the withdrawals root. Only pass <c>false</c> after verifying the header hash;
    /// otherwise a block whose hash does not match its contents is accepted, and that hash is what the block tree
    /// and the consensus layer see.
    /// </param>
    bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error);
    bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error);
}
