// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>The EIP-7666 irregular state transition that installs the identity EVM code at the retired precompile's address.</summary>
/// <remarks>Scoped per processing environment, so it resolves parents through that environment's header finder.</remarks>
public sealed class IdentityPrecompileTransition(ISpecProvider specProvider, IHeaderFinder headerFinder)
{
    // Last processed block with EIP-7666 enabled: a child of it cannot be the fork block. Held by reference because
    // a block being built only gets its hash once processing ends.
    private BlockHeader? _lastEnabledHeader;

    /// <summary>Sets the code of <see cref="Eip7666Constants.IdentityAddress"/> in <paramref name="state"/> if <paramref name="header"/> is the fork block.</summary>
    /// <param name="header">The header of the block being processed.</param>
    /// <param name="spec">The release spec in effect for <paramref name="header"/>.</param>
    /// <param name="state">The state to write to; on the BAL path the pre-execution world state, recording at index 0.</param>
    /// <remarks>
    /// The fork block is the first block with EIP-7666 enabled whose parent does not have it enabled. Only the code is
    /// set: any nonce, balance or storage at the address is kept. The parent is only looked up when it is not the
    /// previously processed EIP-7666 block, so steady-state processing reads neither headers nor state.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The parent header cannot be found. Every environment knows the parent of
    /// the block it processes, so a miss is a wiring fault to surface, not a block to skip.</exception>
    public void ApplyIfForkBlock(BlockHeader header, IReleaseSpec spec, IWorldState state)
    {
        if (!spec.IsEip7666Enabled || header.IsGenesis)
        {
            return;
        }

        if ((header.ParentHash is null || header.ParentHash != _lastEnabledHeader?.Hash) && IsForkBlock(header))
        {
            state.CreateAccountIfNotExists(Eip7666Constants.IdentityAddress, UInt256.Zero);
            state.InsertCode(Eip7666Constants.IdentityAddress, Eip7666Constants.IdentityCode, spec);
        }

        _lastEnabledHeader = header;
    }

    private bool IsForkBlock(BlockHeader header)
    {
        BlockHeader parent = (header.ParentHash is null ? null : headerFinder.Get(header.ParentHash, header.Number - 1))
            ?? throw new InvalidOperationException($"Cannot detect the EIP-7666 fork block: parent of {header.ToString(BlockHeader.Format.Short)} not found.");
        return !specProvider.GetSpec(parent).IsEip7666Enabled;
    }
}
