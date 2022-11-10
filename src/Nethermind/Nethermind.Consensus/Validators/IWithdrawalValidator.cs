// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public interface IWithdrawalValidator
{
    /// <summary>
    /// Validates the block specified for withdrawals against
    /// the <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>.
    /// </summary>
    /// <param name="block">The block to validate.</param>
    /// <returns>
    /// <c>true</c> if <see cref="Block.Withdrawals"/> are not <c>null</c> when EIP-4895 is activated;
    /// otherwise, <c>false</c>.
    /// </returns>
    bool ValidateWithdrawals(Block block) => ValidateWithdrawals(block, out _);

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
}
