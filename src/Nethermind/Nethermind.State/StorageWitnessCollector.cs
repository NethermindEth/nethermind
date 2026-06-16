// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Builds the storage <see cref="ScopeWitness"/> for a witness-tracking scope by walking a read-only view
/// of the base state: one pass over the state trie that, at every touched account leaf, descends into that
/// account's storage trie, collecting the trie-node RLP along the path to each touched account and slot.
/// </summary>
/// <remarks>
/// The caller (<c>WitnessScopeProvider</c>) supplies an <see cref="IScopedTrieStore"/> over a fresh read-only
/// view at the scope's base state root (independent of any in-flight execution mutations), so the witness
/// reflects the pre-execution state regardless of writes/commits done during the scope's lifetime.
/// </remarks>
public static class StorageWitnessCollector
{
    public static ScopeWitness Collect(
        IScopedTrieStore stateStore,
        Hash256 stateRoot,
        IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> touchedKeys,
        IReadOnlyCollection<byte[]> codes,
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

        // Keys ordered like: <addr1><addr2><slot1-of-addr2><slot2-of-addr2><addr3><slot1-of-addr3>
        List<byte[]> keys = new(touchedKeys.Count);
        foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in touchedKeys)
        {
            keys.Add(kvp.Key.Value.Bytes.ToArray());
            foreach (UInt256 slot in kvp.Value) keys.Add(slot.ToBigEndian());
        }

        return new ScopeWitness { StateNodes = stateNodes, Keys = keys, Codes = codes };
    }
}
