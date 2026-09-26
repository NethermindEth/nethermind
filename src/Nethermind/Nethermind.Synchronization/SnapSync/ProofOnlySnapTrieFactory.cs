// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

/// <summary>
/// Builds an empty, non-persistent tree used to verify an account refresh proof.
/// </summary>
internal sealed class ProofOnlySnapTrieFactory(ILogManager logManager) : ISnapTrieFactory
{
    public ISnapTree<PathWithAccount> CreateStateTree() => new ProofOnlySnapStateTree(logManager);

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) =>
        throw new NotSupportedException("Storage proof verification is not supported by the proof-only factory.");

    private sealed class ProofOnlySnapStateTree(ILogManager logManager) : ISnapTree<PathWithAccount>
    {
        private readonly StateTree _tree = new(new EmptyTrieStore(), logManager);

        public Hash256 RootHash => _tree.RootHash;

        public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => false;

        public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries) =>
            ISnapTree<PathWithAccount>.DoBulkSetAndUpdateRootHash(_tree, entries);

        public void Commit(ValueHash256 upperBound) { }

        public void Dispose() { }
    }

    private sealed class EmptyTrieStore : IScopedTrieStore
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => null;

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => null;

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;

        public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => NoopCommitter.Instance;

        private sealed class NoopCommitter : ICommitter
        {
            public static readonly NoopCommitter Instance = new();

            public TrieNode CommitNode(ref TreePath path, TrieNode node) => node;

            public void Dispose() { }
        }
    }
}
