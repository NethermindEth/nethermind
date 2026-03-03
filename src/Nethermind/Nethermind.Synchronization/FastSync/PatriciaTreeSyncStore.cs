// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.FastSync;

public class PatriciaTreeSyncStore(INodeStorage nodeStorage, ILogManager logManager) : ITreeSyncStore
{
    public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash) =>
        nodeStorage.KeyExists(address, path, hash);

    public void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data) =>
        nodeStorage.Set(address, path, hash, data);

    public void FinalizeSync(BlockHeader pivotHeader) =>
        // Patricia trie doesn't need block header info, just flush
        nodeStorage.Flush(onlyWal: false);

    public ITreeSyncVerificationContext CreateVerificationContext(byte[] rootNodeData) =>
        new PatriciaVerificationContext(nodeStorage, rootNodeData, logManager);

    private class PatriciaVerificationContext(
        INodeStorage nodeStorage,
        byte[] rootNodeData,
        ILogManager logManager) : ITreeSyncVerificationContext
    {
        private readonly StateTree _stateTree = CreateStateTree(nodeStorage, rootNodeData, logManager);

        private static StateTree CreateStateTree(INodeStorage nodeStorage, byte[] rootNodeData, ILogManager logManager)
        {
            StateTree stateTree = new(new RawScopedTrieStore(nodeStorage, null), logManager);
            stateTree.RootRef = new TrieNode(NodeType.Unknown, rootNodeData);
            return stateTree;
        }

        public Account? GetAccount(Hash256 addressHash) =>
            _stateTree.Get(addressHash);
    }
}
