// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>The EIP-8253 irregular state transition that sets the nonce of the listed zero-nonce storage accounts to 1.</summary>
public static class ZeroNonceStorageAccountsTransition
{
    /// <summary>Returns the accounts to bump if <paramref name="header"/> is the fork block, otherwise an empty list.</summary>
    /// <param name="header">The header of the block being processed.</param>
    /// <param name="spec">The release spec in effect for <paramref name="header"/>.</param>
    /// <param name="specProvider">Provides the chain id that selects the list and the parent's spec.</param>
    /// <param name="headerFinder">Resolves the parent header, which is only looked up when the chain has a non-empty list.</param>
    /// <remarks>The fork block is the first block with EIP-8253 enabled whose parent does not have it enabled.</remarks>
    /// <exception cref="InvalidOperationException">The parent header cannot be found.</exception>
    public static IReadOnlyList<Address> GetAccountsToBump(BlockHeader header, IReleaseSpec spec, ISpecProvider specProvider, IHeaderFinder headerFinder)
    {
        if (!spec.IsEip8253Enabled || header.IsGenesis)
        {
            return [];
        }

        IReadOnlyList<Address> accounts = Eip8253Constants.GetAccounts(specProvider.ChainId);
        if (accounts.Count == 0)
        {
            return accounts;
        }

        BlockHeader parent = headerFinder.Get(header.ParentHash!, header.Number - 1)
            ?? throw new InvalidOperationException($"Cannot detect the EIP-8253 fork block: parent of {header.ToString(BlockHeader.Format.Short)} not found.");

        return specProvider.GetSpec(parent).IsEip8253Enabled ? [] : accounts;
    }

    /// <summary>Sets the nonce of each of <paramref name="accounts"/> to 1, leaving balance, code and storage untouched.</summary>
    /// <remarks>
    /// The list, not the account's shape, selects what is bumped, so an absent account is created with nonce 1.
    /// On the BAL path <paramref name="state"/> is the pre-execution world state, recording each change at index 0.
    /// </remarks>
    public static void Apply(IWorldState state, IReadOnlyList<Address> accounts)
    {
        foreach (Address address in accounts)
        {
            if (state.AccountExists(address))
            {
                state.SetNonce(address, 1);
            }
            else
            {
                state.CreateAccount(address, UInt256.Zero, 1);
            }
        }
    }
}
