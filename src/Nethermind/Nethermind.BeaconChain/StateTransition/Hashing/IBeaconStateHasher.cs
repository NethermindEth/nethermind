// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition.Hashing;

/// <summary>Computes the SSZ hash-tree-root of a <see cref="BeaconStateFulu"/> or <see cref="BeaconStateGloas"/>.</summary>
/// <remarks>
/// Implementations must produce a root identical to the generated
/// <see cref="BeaconStateFulu.Merkleize"/> and <see cref="BeaconStateGloas.Merkleize"/> but may cache
/// work across calls (see <see cref="CachedBeaconStateHasher"/>). Parity is required for states SSZ decode
/// can produce; a short fixed vector or a null vector element may get a root where the generated code throws.
/// Stateful implementations are not thread-safe and follow one state lineage at a time, mirroring the
/// <see cref="EpochCache"/> ownership rules.
/// </remarks>
public interface IBeaconStateHasher
{
    /// <summary>Returns <c>hash_tree_root(state)</c>.</summary>
    Hash256 HashTreeRoot(BeaconStateFulu state);

    /// <summary>Returns <c>hash_tree_root(state)</c>.</summary>
    /// <remarks>Defaults to a full re-merkleization.</remarks>
    Hash256 HashTreeRoot(BeaconStateGloas state) => SszRoots.HashTreeRoot(state);
}
