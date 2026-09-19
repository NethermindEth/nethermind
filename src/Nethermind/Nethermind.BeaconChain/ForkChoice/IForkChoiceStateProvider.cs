// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// Supplies block post-states to <see cref="ForkChoiceRunner"/> — the spec store's
/// <c>block_states</c> map.
/// </summary>
/// <remarks>
/// The owner must retain the post-state of every block passed to
/// <see cref="ForkChoiceRunner.OnBlock"/> (and of the anchor) at least until it is finalized:
/// the runner reads them to derive checkpoint states and justified balances, and to validate
/// attester slashings against the justified state.
/// </remarks>
public interface IForkChoiceStateProvider
{
    /// <summary>Returns the post-state of <paramref name="blockRoot"/>, or <c>null</c> when unknown.</summary>
    /// <remarks>The runner treats the returned state as read-only.</remarks>
    BeaconStateFulu? GetBlockState(Hash256 blockRoot);

    /// <summary>Returns a mutable copy of the post-state of <paramref name="blockRoot"/>, or <c>null</c> when unknown.</summary>
    /// <remarks>The runner slot-advances the copy to an epoch boundary (the spec's <c>store_target_checkpoint_state</c>).</remarks>
    BeaconStateFulu? CopyBlockState(Hash256 blockRoot);
}
