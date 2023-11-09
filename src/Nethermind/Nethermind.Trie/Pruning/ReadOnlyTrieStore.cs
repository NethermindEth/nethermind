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
        private readonly IKeyValueStore? _readOnlyStore;
        private readonly ReadOnlyValueStore _publicStore;

        public ReadOnlyTrieStore(TrieStore trieStore, IKeyValueStore? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
            _publicStore = new ReadOnlyValueStore(_trieStore.AsKeyValueStore());
        }

        public TrieNode FindCachedOrUnknown(Keccak hash) =>
            _trieStore.FindCachedOrUnknown(hash, true);

        public TrieNode FindCachedOrUnknown(Keccak hash, Span<byte> nodePath, Span<byte> storagePrefix) =>
            _trieStore.FindCachedOrUnknown(hash, true);
        public TrieNode FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Keccak rootHash)
        {
            throw new NotImplementedException();
        }

        public byte[] LoadRlp(Keccak hash, ReadFlags readFlags = ReadFlags.None) => _trieStore.LoadRlp(hash, _readOnlyStore, readFlags);

        public bool IsPersisted(in ValueKeccak keccak) => _trieStore.IsPersisted(keccak);

        public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore)
        {
            throw new NotImplementedException();
        }
        public TrieNodeResolverCapability Capability => TrieNodeResolverCapability.Hash;

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore keyValueStore)
        {
            return new ReadOnlyTrieStore(_trieStore, keyValueStore);
        }

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags flags = WriteFlags.None) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags flags = WriteFlags.None) { }

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public IKeyValueStore AsKeyValueStore() => _publicStore;

        public void Dispose() { }

        public byte[]? LoadRlp(Span<byte> nodePath, Keccak rootHash)
        {
            throw new NotImplementedException();
        }

        public void PersistNode(TrieNode trieNode, IKeyValueStore? keyValueStore, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None) { }
        public void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IKeyValueStore? keyValueStore = null, WriteFlags writeFlags = WriteFlags.None) { }

        public void ClearCache()
        {
            _trieStore.ClearCache();
        }

        public void ClearCacheAfter(Keccak rootHash) { }

        public bool ExistsInDB(Keccak hash, byte[] nodePathNibbles) => _trieStore.ExistsInDB(hash, nodePathNibbles);

        public byte[]? this[ReadOnlySpan<byte> key] => _trieStore[key];
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }

        public bool CanAccessByPath() => _trieStore.CanAccessByPath();
        public void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefix)
        {
            throw new NotImplementedException();
        }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey) { }

        public void CommitNode(long blockNumber, Keccak rootHash, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
        {
            throw new NotImplementedException();
        }

        public void OpenContext(long blockNumber, Keccak keccak)
        {
            throw new NotImplementedException();
        }

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
