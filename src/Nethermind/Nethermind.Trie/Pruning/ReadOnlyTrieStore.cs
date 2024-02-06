// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Safe to be reused for the same wrapped store.
    /// </summary>
    public class ReadOnlyTrieStore : IReadOnlyTrieStore
    {
        private readonly TrieStore _trieStore;
        private readonly IKeyValueStore? _readOnlyStore;
        private readonly IReadOnlyKeyValueStore _publicStore;

        public ReadOnlyTrieStore(TrieStore trieStore, IKeyValueStore? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
            _publicStore = _trieStore.TrieNodeRlpStore;
        }

        public TrieNode FindCachedOrUnknown(Hash256 hash) =>
            _trieStore.FindCachedOrUnknown(hash, true);

        public TrieNode FindCachedOrUnknown(Hash256 hash, Span<byte> nodePath, Span<byte> storagePrefix) =>
            _trieStore.FindCachedOrUnknown(hash, true);
        public TrieNode FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Hash256 rootHash)
        {
            throw new NotImplementedException();
        }

        public byte[]? TryLoadRlp(Hash256 hash, ReadFlags flags) => _trieStore.TryLoadRlp(hash, _readOnlyStore, flags);
        public byte[] LoadRlp(Hash256 hash, ReadFlags flags) => _trieStore.LoadRlp(hash, _readOnlyStore, flags);

        public bool IsPersisted(in ValueHash256 keccak) => _trieStore.IsPersisted(keccak);
        public bool IsPersisted(Hash256 hash, byte[] nodePathNibbles) => _trieStore.IsPersisted(hash, nodePathNibbles);

        public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore, ReadFlags flags = ReadFlags.None)
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

        public IReadOnlyKeyValueStore TrieNodeRlpStore => _publicStore;

        public byte[]? LoadRlp(Span<byte> nodePath, Hash256 rootHash)
        {
            throw new NotImplementedException();
        }

        public void PersistNode(TrieNode trieNode, IWriteBatch? batch = null, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None) { }
        public void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IWriteBatch? batch = null, WriteFlags writeFlags = WriteFlags.None) { }

        public void ClearCache()
        {
            _trieStore.ClearCache();
        }

        public void ClearCacheAfter(Hash256 rootHash) { }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }
        public void Set(in ValueHash256 hash, byte[] rlp)
        {
        }

        public bool CanAccessByPath() => _trieStore.CanAccessByPath();
        public bool ShouldResetObjectsOnRootChange() => _trieStore.ShouldResetObjectsOnRootChange();
        public void PrefetchForSet(Span<byte> key, byte[] storeNibblePrefix, Hash256 stateRoot) { }

        public void StopPrefetch() { }

        public void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefix) { }

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey, IWriteBatch writeBatch = null) { }

        public void OpenContext(long blockNumber, Hash256 keccak) { }

        public bool HasRoot(Hash256 stateRoot)
        {
            return _trieStore.HasRoot(stateRoot);
        }

        public void Dispose() { }
    }
}
