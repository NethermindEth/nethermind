// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator, IWithdrawalValidator
{
    bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error);
    /// <param name="validateHashes">
    /// <c>false</c> to skip recomputing the header hash and the transactions, uncles and withdrawals roots,
    /// including validation of fork-specific withdrawal presence rules. Only pass <c>false</c> after independently
    /// validating the block body, deriving its roots and verifying the header hash; otherwise a block whose body
    /// or header hash does not match its contents can be accepted.
    /// </param>
    bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error);
    bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error);
}
