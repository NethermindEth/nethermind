// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator
{
    bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error);
    bool ValidateSuggestedBlock(Block block, [NotNullWhen(false)] out string? error, bool validateHashes = true);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error);

    /// <summary>
    /// Validates the block specified for withdrawals against
    /// the <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>.
    /// </summary>
    /// <param name="block">The block to validate.</param>
    /// <param name="error">The validation error message if any.</param>
    /// <returns>
    /// <c>true</c> if <see cref="Block.Withdrawals"/> are not <c>null</c> when EIP-4895 is activated;
    /// otherwise, <c>false</c>.
    /// </returns>
    bool ValidateWithdrawals(Block block, out string? error);

    bool ValidateBody(Block block);
}
