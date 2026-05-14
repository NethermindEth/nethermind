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

        public TrieNode GetOrLoadNode(Hash256? address, in TreePath treePath, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
            _trieStore.GetOrLoadNode(address, in treePath, in hash, isReadOnly: true, flags);

        // The default IScopableTrieStore.TryGetOrLoadNode implementation only consults
        // TryGetCachedNode (which defaults to false here) and otherwise falls back to
        // TryLoadRlp - that bypasses the dirty cache entirely, so transient (not yet
        // persisted) nodes show up as missing. Route through the underlying TrieStore's
        // read-only path so cached nodes are cloned and returned just like GetOrLoadNode.
        public bool TryGetOrLoadNode(Hash256? address, in TreePath treePath, in ValueHash256 hash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None) =>
            _trieStore.TryGetOrLoadNode(address, in treePath, in hash, isReadOnly: true, out node, flags);

        public byte[] LoadRlp(Hash256? address, in TreePath treePath, in ValueHash256 hash, ReadFlags flags) =>
            _trieStore.LoadRlp(address, treePath, in hash, flags);
        public byte[]? TryLoadRlp(Hash256? address, in TreePath treePath, in ValueHash256 hash, ReadFlags flags) =>
            _trieStore.TryLoadRlp(address, treePath, in hash, flags);

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
            public override TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None)
            {
                // Cache hit on the shared dirty cache returns the shared / cloned node directly;
                // miss falls through to LoadRlp + decode.
                TrieNode candidate = fullTrieStore._trieStore.GetSharedCachedOrPlaceholder(Address, in path, in hash);
                if (candidate.NodeType != NodeType.Unknown) return candidate;

                byte[]? rlp = fullTrieStore._trieStore.TryLoadRlp(Address, in path, in hash, flags)
                    ?? throw new MissingTrieNodeException("Node missing", Address, path, new Hash256(in hash));
                return TrieNode.DecodeNode(in path, in hash, rlp);
            }

            public override bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
            {
                TrieNode candidate = fullTrieStore._trieStore.GetSharedCachedOrPlaceholder(Address, in path, in hash);
                if (candidate.NodeType != NodeType.Unknown)
                {
                    node = candidate;
                    return true;
                }

                byte[]? rlp = fullTrieStore._trieStore.TryLoadRlp(Address, in path, in hash, flags);
                if (rlp is null)
                {
                    node = null;
                    return false;
                }
                try
                {
                    node = TrieNode.DecodeNode(in path, in hash, rlp);
                    return true;
                }
                catch (TrieException)
                {
                    node = null;
                    return false;
                }
            }

            protected override ITrieNodeResolver WithAddress(Hash256? address1) =>
                new SharedReadOnlyTraversalResolver(fullTrieStore, address1);
        }
    }
}
