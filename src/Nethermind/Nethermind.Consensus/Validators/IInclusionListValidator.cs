// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Validators;

public interface IInclusionListValidator
{
    /// <summary>
    /// Validates that <paramref name="block"/> satisfies its inclusion list per
    /// <see href="https://eips.ethereum.org/EIPS/eip-7805">EIP-7805</see>.
    /// </summary>
    /// <param name="block">The block under validation; the IL set is
    /// <c>block.InclusionListTransactions</c> and the executed transactions to compare
    /// against are <c>block.Transactions</c>.</param>
    /// <param name="parentSenderState">Nonce + balance of each IL transaction sender as
    /// observed in the parent block's state, captured before block processing mutates the
    /// live worldstate. The check uses this snapshot (plus deltas from any IL txs the block
    /// already includes) so a censoring builder can't defeat the IL by including a same-nonce
    /// replacement tx that bumps the sender's nonce in the post-execution state.</param>
    /// <returns>
    /// <c>true</c> if the block's inclusion list is satisfied according to EIP-7805;
    /// otherwise, <c>false</c>.
    /// </returns>
    bool ValidateInclusionList(Block block, IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState);
}
