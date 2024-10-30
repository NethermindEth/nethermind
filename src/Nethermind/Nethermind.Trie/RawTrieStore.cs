// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// This is an <see cref="IScopedTrieStore"/> that does not have any caching or block related handling.
/// It fetch directly from `INodeStorage` and have the minimum interface for `PatriciaTrie` to work.
///
/// Things that does not need any block related handling such as trie root hash calculation should use this.
/// </summary>
/// <param name="nodeStorage"></param>
/// <param name="address"></param>
public class RawTrieStore(INodeStorage nodeStorage, Hash256? address): IScopedTrieStore
{

    public RawTrieStore(IKeyValueStore keyValueStore, Hash256? address): this(new NodeStorage(keyValueStore), address)
    {
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        return new TrieNode(NodeType.Unknown, hash);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return nodeStorage.Get(address, path, hash, flags);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        return new RawTrieStore(nodeStorage, address);
    }

    public INodeStorage.KeyScheme Scheme => nodeStorage.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        return new RawCommitter(nodeStorage.StartWriteBatch(), address, writeFlags);
    }

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        byte[]? rlp = nodeStorage.Get(address, path, keccak, ReadFlags.None);
        return rlp is not null;
    }

    public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp)
    {
        nodeStorage.Set(address, path, keccak, rlp);
    }

    private class RawCommitter(INodeStorage.WriteBatch writeBatch, Hash256? address, WriteFlags writeFlags): ICommitter {

        public void Dispose()
        {
            writeBatch.Dispose();
        }

        public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo)
        {
            if (nodeCommitInfo.IsEmptyBlockMarker || nodeCommitInfo.Node.IsBoundaryProofNode) return;

            TrieNode currentNode = nodeCommitInfo.Node;
            if (currentNode?.Keccak is null) return;

            writeBatch.Set(address, path, currentNode.Keccak, currentNode.FullRlp, writeFlags);
            currentNode.IsPersisted = true;
        }
    }
}
