// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.StateTransition.Hashing;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>Checks a lineage-lifetime incremental hasher against full roots, returning the full root to preserve vector behavior.</summary>
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

/// <summary>Hasher disagreement, deliberately distinct from a spec rejection so invalid vectors cannot pass through it.</summary>
internal sealed class HasherDivergenceException(string message) : Exception(message);
