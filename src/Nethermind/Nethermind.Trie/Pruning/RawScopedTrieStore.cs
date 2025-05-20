// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public class RawScopedTrieStore(INodeStorage nodeStorage, Hash256? address = null) : IScopedTrieStore
{
    public RawScopedTrieStore(IKeyValueStoreWithBatching kv, Hash256? address = null) : this(new NodeStorage(kv), address)
    {
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? ret = nodeStorage.Get(address, path, hash, flags);
        if (ret is null) throw new MissingTrieNodeException("Node missing", address, path, hash);
        return ret;
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => nodeStorage.Get(address, path, hash, flags);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => new RawScopedTrieStore(nodeStorage, address);

    public INodeStorage.KeyScheme Scheme => nodeStorage.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(nodeStorage, address, writeFlags);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => nodeStorage.KeyExists(address, path, keccak);

    public class Committer(INodeStorage nodeStorage, Hash256? address, WriteFlags writeFlags) : ICommitter
    {
        INodeStorage.IWriteBatch _writeBatch = nodeStorage.StartWriteBatch();

        public void Dispose()
        {
            _writeBatch.Dispose();
        }

        public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo)
        {
            if (!nodeCommitInfo.IsEmptyBlockMarker && !nodeCommitInfo.Node.IsBoundaryProofNode)
            {
                TrieNode node = nodeCommitInfo.Node;

                if (node.Keccak is null)
                {
                    ThrowUnknownHash(node);
                }

                TrieNode currentNode = nodeCommitInfo.Node;
                currentNode.IsPersisted = true;
                _writeBatch.Set(address, path, currentNode.Keccak, currentNode.FullRlp, writeFlags);
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
    }
}
