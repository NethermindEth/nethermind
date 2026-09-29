// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Hashes every state through an incremental hasher that lives as long as the vector, as production keeps
/// one per lineage, and fails when its root differs from the full hasher's; returns the full root.
/// </summary>
/// <remarks>
/// Returning the full root keeps the pipeline's behavior identical to a run without the check, so a
/// vector still fails only for the reason it would have failed before.
/// </remarks>
internal sealed class DifferentialBeaconStateHasher(IBeaconStateHasher incremental, IBeaconStateHasher full) : IBeaconStateHasher
{
    public DifferentialBeaconStateHasher() : this(new CachedBeaconStateHasher(), new FullBeaconStateHasher())
    {
    }

    public Hash256 HashTreeRoot(BeaconStateFulu state) =>
        Compare(state.Slot, full.HashTreeRoot(state), incremental.HashTreeRoot(state));

    public Hash256 HashTreeRoot(BeaconStateGloas state) =>
        Compare(state.Slot, full.HashTreeRoot(state), incremental.HashTreeRoot(state));

    private static Hash256 Compare(ulong slot, Hash256 fullRoot, Hash256 incrementalRoot) =>
        fullRoot == incrementalRoot
            ? fullRoot
            : throw new HasherDivergenceException($"the incremental hasher's root {incrementalRoot} differs from the full hasher's {fullRoot} for the state at slot {slot}");
}

/// <summary>
/// The incremental hasher disagreed with the full one. Deliberately not a spec rejection, so an invalid
/// vector cannot pass by being "rejected" through it.
/// </summary>
internal sealed class HasherDivergenceException(string message) : Exception(message);
