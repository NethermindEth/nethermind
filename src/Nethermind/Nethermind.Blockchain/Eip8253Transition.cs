// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Blockchain;

/// <summary>
/// EIP-8253: bumps the nonce of the listed zero-nonce storage accounts to 1 at fork activation,
/// before any pre-execution system contract calls and before any transactions.
/// </summary>
public static class Eip8253Transition
{
    /// <summary>
    /// Applies the transition to every <see cref="Eip8253Data.Accounts"/> entry that still has
    /// a zero nonce.
    /// </summary>
    /// <returns>The number of accounts bumped; 0 when the EIP is disabled or already applied.</returns>
    /// <remarks>
    /// The transition is keyed off state rather than the activation block: nonces never decrease
    /// and the listed accounts cannot self-destruct (empty code), so a zero nonce is observable
    /// only before the transition has run. This makes the check reorg-safe without needing the
    /// parent header to detect the fork boundary. The checks use
    /// <see cref="IWorldState.PeekNonce"/> so the per-block probe never leaks into EIP-7928
    /// block access lists; the nonce writes go through <see cref="IWorldState.SetNonce"/> and are
    /// therefore recorded as <c>NonceChange</c> entries at the pre-execution access index,
    /// as the EIP requires.
    /// </remarks>
    public static int Apply(IWorldState worldState, IReleaseSpec spec)
    {
        if (!spec.IsEip8253Enabled) return 0;

        int bumped = 0;
        foreach (Address account in Eip8253Data.Accounts)
        {
            // PeekNonce is null for nonexistent accounts, so only existing zero-nonce accounts match.
            if (worldState.PeekNonce(account) == 0)
            {
                worldState.SetNonce(account, 1);
                bumped++;
            }
        }

        return bumped;
    }
}
