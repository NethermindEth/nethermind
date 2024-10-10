// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Safe to be reused for the same wrapped store.
    /// </summary>
    public class ReadOnlyTrieStore(TrieStore trieStore, INodeStorage? readOnlyStore) : IReadOnlyTrieStore
    {
        private readonly TrieStore _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        public INodeStorage.KeyScheme Scheme => _trieStore.Scheme;

        public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath treePath, Hash256 hash) =>
            _trieStore.FindCachedOrUnknown(address, treePath, hash, true);

        public byte[] LoadRlp(Hash256? address, in TreePath treePath, Hash256 hash, ReadFlags flags) =>
            _trieStore.LoadRlp(address, treePath, hash, readOnlyStore, flags);
        public byte[]? TryLoadRlp(Hash256? address, in TreePath treePath, Hash256 hash, ReadFlags flags) =>
            _trieStore.TryLoadRlp(address, treePath, hash, readOnlyStore, flags);

        public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak) => _trieStore.IsPersisted(address, path, keccak);

        public IReadOnlyTrieStore AsReadOnly(INodeStorage nodeStore) => new ReadOnlyTrieStore(_trieStore, nodeStore);

        public ICommitter BeginCommit(TrieType trieType, long blockNumber, Hash256? address, TrieNode? root, WriteFlags writeFlags) =>
            new NullTrieStore.NullCommitter();

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public IReadOnlyKeyValueStore TrieNodeRlpStore => _trieStore.TrieNodeRlpStore;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] rlp) { }

        public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedReadOnlyTrieStore(this, address);

        public bool HasRoot(Hash256 stateRoot) => _trieStore.HasRoot(stateRoot);

        public void Dispose() { }

        private class ScopedReadOnlyTrieStore(ReadOnlyTrieStore fullTrieStore, Hash256? address) : IScopedTrieStore
        {
            public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
                fullTrieStore.FindCachedOrUnknown(address, path, hash);

            public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
                fullTrieStore.LoadRlp(address, path, hash, flags);

            public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => fullTrieStore.TryLoadRlp(address, path, hash, flags);

            public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address1) =>
                address1 == address ? this : new ScopedReadOnlyTrieStore(fullTrieStore, address1);

            public INodeStorage.KeyScheme Scheme => fullTrieStore.Scheme;

            public ICommitter BeginCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
                new NullTrieStore.NullCommitter();

            public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
                fullTrieStore.IsPersisted(address, path, in keccak);

            public void Set(in TreePath path, in ValueHash256 keccak, byte[] rlp) { }
        }
    }
}
