// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

internal static partial class WitnessNodeStorage
{
    /// <summary>Builds a hash-keyed node storage holding the witness' state nodes.</summary>
    /// <param name="state">The witness' state nodes, each keyed by the keccak of its own bytes.</param>
    /// <remarks>Checks no node; the trie walks check the nodes they visit against their depth (see
    /// <see cref="SecuredTriePath"/>).</remarks>
    public static INodeStorage Create(IOwnedReadOnlyList<byte[]> state) => new HashKeyedNodeStorage(state.AsSpan());

    /// <summary>Builds a trie store over <see cref="Create"/>'s node storage, which reports each storage node it loads to
    /// <paramref name="paths"/>.</summary>
    public static ITrieStore CreateTrieStore(IOwnedReadOnlyList<byte[]> state, out WitnessTriePaths paths)
    {
        HashKeyedNodeStorage nodes = (HashKeyedNodeStorage)Create(state);
        nodes.StorageNodeLoading = paths = new WitnessTriePaths(nodes, state.AsSpan());
        return new RawTrieStore(nodes);
    }
}
