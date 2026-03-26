// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieStore : IScopedTrieStore
    {
        private NullTrieStore() { }

        public static NullTrieStore Instance { get; } = new();

        public TrieNode FindCachedOrUnknown(in TreePath treePath, Hash256 hash) => new(NodeType.Unknown, hash);

        public CappedArray<byte> LoadRlp(in TreePath treePath, Hash256 hash, ReadFlags flags = ReadFlags.None) => new([]);

        public CappedArray<byte> TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => new([]);

        public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => NullCommitter.Instance;

        public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp) { }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 storageRoot) => this;

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
    }
}
