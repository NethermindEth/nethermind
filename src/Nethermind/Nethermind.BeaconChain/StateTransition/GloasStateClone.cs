// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Cheap copy-on-clone of <see cref="BeaconStateGloas"/>, the post-fork twin of
/// <see cref="BeaconStateClone"/>. The importer freezes a clone under each block root for
/// <see cref="IGloasBlockStateProvider"/> and keeps advancing the original.
/// </summary>
/// <remarks>
/// Same rule as the Fulu clone: an array whose slots the transition writes in place is copied,
/// everything it only ever replaces wholesale is shared. The Gloas-only fields follow the same
/// split - <c>Builders</c>, <c>BuilderPendingPayments</c> and <c>PtcWindow</c> are indexed in
/// place with replaced (never mutated) elements, the payload availability bits are set in place,
/// and the two withdrawal lists and the latest bid are reassigned as a whole.
/// </remarks>
public static partial class GloasStateClone
{
    /// <summary>
    /// Returns a state that can be mutated by the Gloas state transition without affecting
    /// <paramref name="state"/> (and vice versa).
    /// </summary>
    public static partial BeaconStateGloas Clone(this BeaconStateGloas state);
}
