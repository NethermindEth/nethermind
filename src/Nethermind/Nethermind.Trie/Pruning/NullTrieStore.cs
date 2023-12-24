// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieStore : IScopedTrieStore
    {
        private NullTrieStore() { }

        public static NullTrieStore Instance { get; } = new();

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags flags = WriteFlags.None) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags flags = WriteFlags.None) { }

        public TrieNode FindCachedOrUnknown(in TreePath treePath, Hash256 hash) => new(NodeType.Unknown, hash);

        public byte[] LoadRlp(in TreePath treePath, Hash256 hash, ReadFlags flags = ReadFlags.None) => Array.Empty<byte>();

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => true;

        public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp)
        {
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 storageRoot)
        {
            return this;
        }

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;

    }
}
