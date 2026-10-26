// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Resolves the frozen post-state of a Gloas block by its root: the spec store's
/// <c>block_states[root]</c> for the ePBS messages that must be verified against exactly that
/// state (an execution payload envelope names its block through <c>beacon_block_root</c>).
/// </summary>
/// <remarks>
/// The verifier looks the state up itself rather than taking one from its caller, so a caller can
/// not (by mistake or otherwise) supply a state that is not any known block's post-state: a
/// self-consistent envelope built against such a state passes every field check yet describes a
/// block that does not exist. A root this provider does not know is a rejection, never a fallback.
/// </remarks>
public interface IGloasBlockStateProvider
{
    /// <summary>Returns the post-state of the Gloas block with root <paramref name="blockRoot"/>, or <c>null</c> when unknown.</summary>
    /// <remarks>Callers treat the returned state as read-only.</remarks>
    BeaconStateGloas? GetGloasBlockState(Hash256 blockRoot);
}
