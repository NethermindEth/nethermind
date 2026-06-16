// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Walks a read-only view of the base state and returns the deduplicated state-trie and storage-trie node
/// RLPs along the paths of every touched account and slot: one pass over the state trie that, at every touched
/// account leaf, descends into that account's storage trie.
/// </summary>
/// <remarks>
/// The caller (<c>TrieWitnessScopeProvider</c>) supplies an <see cref="IScopedTrieStore"/> over a fresh
/// read-only view at the scope's base state root (independent of any in-flight execution mutations), so the
/// result reflects the pre-execution state regardless of writes/commits done during the scope's lifetime.
/// </remarks>
public static class StorageWitnessCollector
{
    public static IReadOnlyList<byte[]> Collect(
        IScopedTrieStore stateStore,
        Hash256 stateRoot,
        IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> touchedKeys,
        ILogManager logManager)
    {
        MultiAccountProofCollector collector = new(touchedKeys);
        if (touchedKeys.Count > 0 && stateRoot != Keccak.EmptyTreeHash)
        {
            PatriciaTree tree = new(stateStore, logManager);
            tree.Accept(collector, stateRoot);
        }

        // A node can lie on more than one touched path; dedup by content for a deterministic node set.
        HashSet<byte[]> seen = new(Bytes.EqualityComparer);
        List<byte[]> stateNodes = new(collector.Nodes.Count);
        foreach (byte[] node in collector.Nodes)
        {
            if (seen.Add(node)) stateNodes.Add(node);
        }

        // A scope that touched nothing still needs the root node so a stateless verifier can anchor
        // traversal; lazy TrieNode handling can otherwise leave it uncaptured.
        if (stateNodes.Count == 0 && stateRoot != Keccak.EmptyTreeHash)
        {
            TreePath path = TreePath.Empty;
            TrieNode root = stateStore.FindCachedOrUnknown(path, stateRoot);
            root.ResolveNode(stateStore, path);
            if (root.Keccak is not null) stateNodes.Add(root.FullRlp.ToArray());
        }

        return stateNodes;
    }
}
