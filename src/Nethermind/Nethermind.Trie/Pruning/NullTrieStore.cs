// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieStore : IReadOnlyTrieStore
    {
        private NullTrieStore() { }

        public static NullTrieStore Instance { get; } = new();

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags flags = WriteFlags.None) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags flags = WriteFlags.None) { }

        public void HackPersistOnShutdown() { }

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore keyValueStore) => this;

        public byte[]? TryLoadRlp(Span<byte> path, IKeyValueStore? keyValueStore)
        {
            throw new NotImplementedException();
        }
        public TrieNodeResolverCapability Capability => TrieNodeResolverCapability.Hash;

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public IKeyValueStore AsKeyValueStore() => null!;

        public TrieNode FindCachedOrUnknown(Hash256 hash) => new(NodeType.Unknown, hash);

        public TrieNode FindCachedOrUnknown(Hash256 hash, Span<byte> nodePath, Span<byte> storagePrefix)
        {
            return new(NodeType.Unknown, nodePath, hash) { StoreNibblePathPrefix = storagePrefix.ToArray() };
        }

        public TrieNode FindCachedOrUnknown(Span<byte> nodePath, byte[] storagePrefix, Hash256 rootHash)
        {
            return new(NodeType.Unknown, nodePath, storagePrefix);
        }

        public byte[] LoadRlp(Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            return Array.Empty<byte>();
        }

        public bool IsPersisted(in ValueHash256 keccak) => true;

        public void Dispose() { }

        public byte[]? LoadRlp(Span<byte> nodePath, Hash256 rootHash)
        {
            return Array.Empty<byte>();
        }

        public void PersistNode(TrieNode trieNode, IColumnsWriteBatch<StateColumns>? batch = null, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None) { }
        public void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IColumnsWriteBatch<StateColumns>? batch = null, WriteFlags writeFlags = WriteFlags.None) { }

        public void ClearCache() { }
        public void ClearCacheAfter(Hash256 rootHash) { }

        public bool ExistsInDB(Hash256 hash, byte[] nodePathNibbles) => false;

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey) { }
        public void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefix)
        {
            throw new NotImplementedException();
        }

        public bool CanAccessByPath()
        {
            return false;
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            throw new NotImplementedException();
        }

        public void CommitNode(long blockNumber, Hash256 rootHash, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
        {
            throw new NotImplementedException();
        }

        public void OpenContext(long blockNumber, Hash256 keccak)
        {
            throw new NotImplementedException();
        }

        public byte[]? this[ReadOnlySpan<byte> key] => null;
    }
}
