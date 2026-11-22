// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>Cheap copy-on-clone of <see cref="BeaconStateFulu"/> for forking a state lineage.</summary>
/// <remarks>
/// Per field the copy-vs-share decision follows how the state transition writes it: arrays whose
/// slots are written in place (balances, slashings, participation, root/mix arrays and the
/// proposer lookahead) are copied. Validator and inactivity-score snapshots are shared until a
/// writable array is requested, while everything the transition
/// only ever replaces wholesale (checkpoints, sync committees, the payload header, the pending
/// queues and other container lists) is shared. Shared objects are immutable by convention —
/// e.g. <see cref="Validator"/> mutations install a <see cref="BeaconStateMutators.Clone"/>d copy
/// — so the clone and the source can both be advanced independently.
/// </remarks>
public static partial class BeaconStateClone
{
    /// <summary>
    /// Returns a state that can be mutated by the state transition without affecting
    /// <paramref name="state"/> (and vice versa).
    /// </summary>
    public static partial BeaconStateFulu Clone(this BeaconStateFulu state);

    /// <summary>Creates a private working copy for immediate slot processing by the import worker.</summary>
    internal static BeaconStateFulu CloneForImport(this BeaconStateFulu parent, IBeaconStateHasher hasher, Hash256 parentRoot)
    {
        BeaconStateFulu clone = parent.Clone();
        if (hasher is CachedBeaconStateHasher cached)
            cached.RecordImportClone(parent, clone, parentRoot);
        return clone;
    }
}
