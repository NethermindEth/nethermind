// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition.Hashing;

/// <summary>Computes the SSZ hash-tree-root of a <see cref="BeaconStateFulu"/>.</summary>
/// <remarks>
/// Implementations must produce a root identical to the generated
/// <see cref="BeaconStateFulu.Merkleize"/> but may cache work across calls (see
/// <see cref="CachedBeaconStateHasher"/>). Stateful implementations are not thread-safe and follow
/// one state lineage at a time, mirroring the <see cref="EpochCache"/> ownership rules.
/// </remarks>
public interface IBeaconStateHasher
{
    /// <summary>Returns <c>hash_tree_root(state)</c>.</summary>
    Hash256 HashTreeRoot(BeaconStateFulu state);
}
