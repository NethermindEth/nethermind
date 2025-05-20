// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public interface IInclusionListValidator
{
    /// <summary>
    /// Validates that the block satisfies the inclusion list
    /// the <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    /// <param name="inclusionListTransactions">The inclusion list transactions to validate.</param>
    /// <param name="block">The block to validate.</param>
    /// <returns>
    /// <c>true</c> if the block's inclusion list is satisfied according to EIP-7805;
    /// otherwise, <c>false</c>.
    /// </returns>
    bool ValidateInclusionList(Block block, Func<Transaction, bool> isTransactionInBlock);
}
