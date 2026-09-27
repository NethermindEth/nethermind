// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>The EIP-8253 irregular state transition that sets the nonce of the listed zero-nonce storage accounts to 1.</summary>
/// <remarks>Scoped per processing environment, so it resolves parents through that environment's header finder.</remarks>
public sealed class ZeroNonceStorageAccountsTransition(ISpecProvider specProvider, IHeaderFinder headerFinder)
{
    // Hash of the last processed block with EIP-8253 enabled: a child of it cannot be the fork block.
    private Hash256? _lastEnabledBlockHash;

    /// <summary>Bumps the listed accounts in <paramref name="state"/> if <paramref name="header"/> is the fork block.</summary>
    /// <param name="header">The header of the block being processed.</param>
    /// <param name="spec">The release spec in effect for <paramref name="header"/>.</param>
    /// <param name="state">The state to write to; on the BAL path the pre-execution world state, recording at index 0.</param>
    /// <remarks>
    /// The fork block is the first block with EIP-8253 enabled whose parent does not have it enabled. The parent is only
    /// looked up when it is not the previously processed EIP-8253 block, so steady-state processing does no lookup.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The parent header cannot be found. Every environment knows the parent of
    /// the block it processes, so a miss is a wiring fault to surface, not a block to skip.</exception>
    public void ApplyIfForkBlock(BlockHeader header, IReleaseSpec spec, IWorldState state)
    {
        if (!spec.IsEip8253Enabled || header.IsGenesis)
        {
            return;
        }

        IReadOnlyList<Address> accounts = Eip8253Constants.GetAccounts(specProvider.ChainId);
        if (accounts.Count == 0)
        {
            return;
        }

        if (header.ParentHash != _lastEnabledBlockHash && IsForkBlock(header))
        {
            Apply(state, accounts);
        }

        _lastEnabledBlockHash = header.Hash;
    }

    private bool IsForkBlock(BlockHeader header)
    {
        BlockHeader parent = headerFinder.Get(header.ParentHash!, header.Number - 1)
            ?? throw new InvalidOperationException($"Cannot detect the EIP-8253 fork block: parent of {header.ToString(BlockHeader.Format.Short)} not found.");
        return !specProvider.GetSpec(parent).IsEip8253Enabled;
    }

    // The list, not the account's shape, selects what is bumped, so an absent account is created with nonce 1.
    private static void Apply(IWorldState state, IReadOnlyList<Address> accounts)
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
