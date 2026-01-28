// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapTrieFactory(INodeStorage nodeStorage, ILogManager logManager) : ISnapTrieFactory
{
    private readonly RawScopedTrieStore _stateTrieStore = new(nodeStorage, null);

    public ISnapStateTree CreateStateTree() =>
        new PatriciaSnapStateTree(new StateTree(_stateTrieStore, logManager));

    public ISnapStorageTree CreateStorageTree(in ValueHash256 accountPath) =>
        new PatriciaSnapStorageTree(new StorageTree(new RawScopedTrieStore(nodeStorage, accountPath.ToCommitment()), logManager));

    public Hash256? ResolveStorageRoot(byte[] nodeData)
    {
        try
        {
            TreePath emptyTreePath = TreePath.Empty;
            TrieNode node = new(NodeType.Unknown, nodeData, isDirty: true);
            node.ResolveNode(_stateTrieStore, emptyTreePath);
            node.ResolveKey(_stateTrieStore, ref emptyTreePath);
            return node.Keccak;
        }
        catch
        {
            return null;
        }
    }
}
