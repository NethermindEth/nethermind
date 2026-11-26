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
    /// <remarks>Checks no node: the guest's trie walks check each node they reach (see <c>PatriciaTree.zkevm.cs</c>).</remarks>
    public static INodeStorage Create(IOwnedReadOnlyList<byte[]> state) => new HashKeyedNodeStorage(state.AsSpan());

    /// <summary>Builds a trie store over <see cref="Create"/>'s node storage.</summary>
    public static ITrieStore CreateTrieStore(IOwnedReadOnlyList<byte[]> state) => new RawTrieStore(Create(state));
}
