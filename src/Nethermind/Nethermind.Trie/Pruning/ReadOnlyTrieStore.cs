// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Safe to be reused for the same wrapped store.
    /// </summary>
    public class ReadOnlyTrieStore : IReadOnlyTrieStore
    {
        private readonly TrieStore _trieStore;
        private readonly INodeStorage? _readOnlyStore;
        public INodeStorage.KeyScheme Scheme => _trieStore.Scheme;

        public ReadOnlyTrieStore(TrieStore trieStore, INodeStorage? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
        }

        public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath treePath, Hash256 hash) =>
            _trieStore.FindCachedOrUnknown(address, treePath, hash, true);

        public byte[] LoadRlp(Hash256? address, in TreePath treePath, Hash256 hash, ReadFlags flags) =>
            _trieStore.LoadRlp(address, treePath, hash, _readOnlyStore, flags);
        public byte[]? TryLoadRlp(Hash256? address, in TreePath treePath, Hash256 hash, ReadFlags flags) =>
            _trieStore.TryLoadRlp(address, treePath, hash, _readOnlyStore, flags);

        public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak) => _trieStore.IsPersisted(address, path, keccak);

        public IReadOnlyTrieStore AsReadOnly(INodeStorage nodeStore)
        {
            return new ReadOnlyTrieStore(_trieStore, nodeStore);
        }

        public void CommitNode(long blockNumber, Hash256? address, NodeCommitInfo nodeCommitInfo, WriteFlags flags = WriteFlags.None) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags flags = WriteFlags.None) { }

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public IReadOnlyKeyValueStore TrieNodeRlpStore => _trieStore.TrieNodeRlpStore;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp)
        {
        }

        public IScopedTrieStore GetTrieStore(Hash256? address)
        {
            return new ScopedReadOnlyTrieStore(this, address);
        }

        public void Dispose() { }

        private class ScopedReadOnlyTrieStore : IScopedTrieStore
        {
            private readonly ReadOnlyTrieStore _trieStoreImplementation;
            private readonly Hash256? _address;

            public ScopedReadOnlyTrieStore(ReadOnlyTrieStore fullTrieStore, Hash256? address)
            {
                _trieStoreImplementation = fullTrieStore;
                _address = address;
            }

            public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
            {
                return _trieStoreImplementation.FindCachedOrUnknown(_address, path, hash);
            }

            public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
            {
                return _trieStoreImplementation.LoadRlp(_address, path, hash, flags);
            }

            public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
            {
                return _trieStoreImplementation.TryLoadRlp(_address, path, hash, flags);
            }

            public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
            {
                if (address == _address) return this;
                return new ScopedReadOnlyTrieStore(_trieStoreImplementation, address);
            }

            public INodeStorage.KeyScheme Scheme => _trieStoreImplementation.Scheme;

            public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
            {
            }

            public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
            {
            }

            public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
            {
                return _trieStoreImplementation.IsPersisted(_address, path, in keccak);
            }

            public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp)
            {
            }
        }
    }
}
