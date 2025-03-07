// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Validators;

public interface IWithdrawalValidator
{
    /// <summary>
    /// Validates the specified block for withdrawals, if they are available at <paramref name="spec"/>.
    /// </summary>
    /// <param name="block">The block to validate.</param>
    /// <param name="spec">The current spec.</param>
    /// <param name="error">The validation error message if any.</param>
    /// <returns>Whether withdrawals are valid. </returns>
    bool ValidateWithdrawals(Block block, IReleaseSpec spec, out string? error);
}
