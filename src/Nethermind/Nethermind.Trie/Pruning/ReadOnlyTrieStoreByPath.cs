// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection.Metadata.Ecma335;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Safe to be reused for the same wrapped store.
    /// </summary>
    public class ReadOnlyTrieStoreByPath : IReadOnlyTrieStore
    {
        private readonly TrieStoreByPath _trieStore;
        private readonly IKeyValueStore? _readOnlyStore;

        public ReadOnlyTrieStoreByPath(TrieStoreByPath trieStore, IKeyValueStore? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
        }

        public TrieNode FindCachedOrUnknown(Keccak hash) =>
            _trieStore.FindCachedOrUnknown(hash);

        public TrieNode FindCachedOrUnknown(Keccak hash, Span<byte> nodePath, Span<byte> storagePrefix) =>
            _trieStore.FindCachedOrUnknown(hash, nodePath, storagePrefix);

        public byte[] LoadRlp(Keccak hash) => _trieStore.LoadRlp(hash, _readOnlyStore);

        public bool IsPersisted(Keccak keccak) => _trieStore.IsPersisted(keccak);

        public TrieNodeResolverCapability Capability => TrieNodeResolverCapability.Path;

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore keyValueStore)
        {
            return new ReadOnlyTrieStoreByPath(_trieStore, keyValueStore);
        }

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root) { }

        public void HackPersistOnShutdown() { }

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }
        public void Dispose() { }

        public byte[]? LoadRlp(Span<byte> nodePath, Keccak rootHash)
        {
            return _trieStore.LoadRlp(nodePath, rootHash);
        }

        public void SaveNodeDirectly(long blockNumber, TrieNode trieNode, IKeyValueStore? keyValueStore = null) { }
        public void ClearCache() => _trieStore.ClearCache();

        public bool ExistsInDB(Keccak hash, byte[] nodePathNibbles) => _trieStore.ExistsInDB(hash, nodePathNibbles);

        public byte[]? this[ReadOnlySpan<byte> key] => _trieStore[key];
    }
}
