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
        private readonly ReadOnlyValueStore _publicStore;

        public ReadOnlyTrieStoreByPath(TrieStoreByPath trieStore, IKeyValueStore? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
            _publicStore = new ReadOnlyValueStore(_trieStore.AsKeyValueStore());
        }

        public TrieNode FindCachedOrUnknown(Keccak hash) =>
            _trieStore.FindCachedOrUnknown(hash);

        public TrieNode? FindCachedOrUnknown(Keccak hash, Span<byte> nodePath, Span<byte> storagePrefix) =>
            _trieStore.FindCachedOrUnknown(hash, nodePath, storagePrefix);
        public TrieNode? FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Keccak rootHash)
        {
            return _trieStore.FindCachedOrUnknown(nodePath, storagePrefix, rootHash);
        }

        public byte[] LoadRlp(Keccak hash, ReadFlags flags = ReadFlags.None) => _trieStore.LoadRlp(hash, _readOnlyStore, flags);

        public bool IsPersisted(Keccak keccak) => _trieStore.IsPersisted(keccak);
        public bool IsPersisted(in ValueKeccak keccak) => _trieStore.IsPersisted(keccak);

        public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore)
        {
            return _trieStore.TryLoadRlp(path, keyValueStore);
        }
        public TrieNodeResolverCapability Capability => TrieNodeResolverCapability.Path;

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore keyValueStore)
        {
            return new ReadOnlyTrieStoreByPath(_trieStore, keyValueStore);
        }

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None) { }

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

        public void PersistNode(TrieNode trieNode, IKeyValueStore? keyValueStore = null, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None) { }
        public void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IKeyValueStore? keyValueStore = null, WriteFlags writeFlags = WriteFlags.None) { }

        public void ClearCache() => _trieStore.ClearCache();
        public void ClearCacheAfter(Keccak rootHash) { }

        public bool ExistsInDB(Keccak hash, byte[] nodePathNibbles) => _trieStore.ExistsInDB(hash, nodePathNibbles);

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey) { }
        public void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefix) { }

        public bool CanAccessByPath() => _trieStore.CanAccessByPath();

        public byte[]? this[ReadOnlySpan<byte> key] => _trieStore[key];

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags) => _trieStore.Get(key, flags);

        public IKeyValueStore AsKeyValueStore() => _publicStore;

        public void CommitNode(long blockNumber, Keccak rootHash, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None) { }

        public void OpenContext(long blockNumber, Keccak keccak) { }

        private class ReadOnlyValueStore : IKeyValueStore
        {
            private readonly IKeyValueStore _keyValueStore;

            public ReadOnlyValueStore(IKeyValueStore keyValueStore)
            {
                _keyValueStore = keyValueStore;
            }

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _keyValueStore.Get(key, flags);

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }
        }
    }
}
