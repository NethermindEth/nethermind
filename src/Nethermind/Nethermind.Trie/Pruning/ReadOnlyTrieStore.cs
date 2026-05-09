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
    public class ReadOnlyTrieStore(TrieStore trieStore) : IReadOnlyTrieStore, IScopedReadOnlyTraversalProvider
    {
        private readonly TrieStore _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        public INodeStorage.KeyScheme Scheme => _trieStore.Scheme;

        public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath treePath, Hash256 hash) =>
            _trieStore.FindCachedOrUnknown(address, treePath, hash, true);

        public byte[] LoadRlp(Hash256? address, in TreePath treePath, Hash256 hash, ReadFlags flags) =>
            _trieStore.LoadRlp(address, treePath, hash, flags);
        public byte[]? TryLoadRlp(Hash256? address, in TreePath treePath, Hash256 hash, ReadFlags flags) =>
            _trieStore.TryLoadRlp(address, treePath, hash, flags);

        public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => NullCommitter.Instance;

        public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

        public IDisposable BeginScope(BlockHeader? baseBlock) => new Reactive.AnonymousDisposable(() => { }); // Noop

        public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

        public bool HasRoot(Hash256 stateRoot) => _trieStore.HasRoot(stateRoot);

        public bool HasRoot(Hash256 stateRoot, long blockNumber) => _trieStore.HasRoot(stateRoot, blockNumber);

        public void Dispose() { }

        public ITrieNodeResolver? GetReadOnlyTraversalResolver(Hash256? address) =>
            new SharedReadOnlyTraversalResolver(this, address);

        private sealed class SharedReadOnlyTraversalResolver(ReadOnlyTrieStore fullTrieStore, Hash256? address)
            : ReadOnlyTraversalResolverBase(fullTrieStore, address)
        {
            public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
                fullTrieStore._trieStore.FindCachedOrUnknownShared(Address, path, hash);

            protected override ITrieNodeResolver WithAddress(Hash256? address1) =>
                new SharedReadOnlyTraversalResolver(fullTrieStore, address1);
        }
    }
}
