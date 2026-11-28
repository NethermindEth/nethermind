// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>Reconstructs snap range proofs in memory for the light client's independent proof verification.</summary>
public sealed class MemorySnapTrieFactory(MemoryNodeStorage nodes, ILogManager logs) : ISnapTrieFactory
{
    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        MemorySnapUpperBoundAdapter adapter = new(new RawScopedTrieStore(nodes), boundWholePath: false);
        return new MemorySnapTree<PathWithAccount>(new StateTree(adapter, logs), adapter, nodes, null, skipRoot: true);
    }

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
    {
        Hash256 address = accountPath.ToCommitment();
        MemorySnapUpperBoundAdapter adapter = new(new RawScopedTrieStore(nodes, address), boundWholePath: true);
        return new MemorySnapTree<PathWithStorageSlot>(new StorageTree(adapter, logs), adapter, nodes, address, skipRoot: false);
    }

    private sealed class MemorySnapTree<TEntry>(PatriciaTree tree, MemorySnapUpperBoundAdapter adapter, MemoryNodeStorage nodes, Hash256? address, bool skipRoot) : ISnapTree<TEntry>
        where TEntry : ISnapEntry
    {
        private List<(TrieNode, TreePath)>? _proofNodes;
        public Hash256 RootHash => tree.RootHash;

        public void SetRootFromProof(IByteArrayList proofs, IReadOnlyList<SnapProofNode> proofNodes) =>
            _proofNodes = PatriciaSnapProof.SetRoot(tree, proofs, proofNodes);

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => nodes.KeyExists(address, path, keccak);

        public void BulkSetAndUpdateRootHash(IReadOnlyList<TEntry> entries) =>
            ISnapTree<TEntry>.DoBulkSetAndUpdateRootHash(tree, entries);

        public bool Commit(ValueHash256 lowerBound, ValueHash256 upperBound)
        {
            PatriciaSnapProof.StitchBoundaries(_proofNodes, this);
            adapter.LowerBound = lowerBound;
            adapter.UpperBound = upperBound;
            tree.Commit(skipRoot, WriteFlags.DisableWAL);
            return _proofNodes is { Count: > 0 } ? _proofNodes[0].Item1.IsPersisted : lowerBound == ValueKeccak.Zero && upperBound == ValueKeccak.MaxValue;
        }

        public void Dispose() { }
    }
}
