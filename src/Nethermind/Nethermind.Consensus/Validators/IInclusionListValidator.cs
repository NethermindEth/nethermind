// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public interface IInclusionListValidator
{
    /// <summary>
    /// Validates that <paramref name="block"/> satisfies its inclusion list per
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    /// <param name="block">The block under validation; its <c>InclusionListTransactions</c>
    /// is the set to check.</param>
    /// <param name="isTransactionInBlock">Predicate that reports whether a given IL tx is
    /// already part of the block's executed transaction list; supplied by the caller so
    /// the validator stays decoupled from how the executor tracks inclusion.</param>
    /// <returns>
    /// <c>true</c> if the block's inclusion list is satisfied according to EIP-7805;
    /// otherwise, <c>false</c>.
    /// </returns>
    bool ValidateInclusionList(Block block, Func<Transaction, bool> isTransactionInBlock);
}
