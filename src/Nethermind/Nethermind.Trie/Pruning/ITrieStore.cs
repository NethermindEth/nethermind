// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieStore : ITrieNodeResolver, IDisposable
    {
        void OpenContext(long blockNumber, Hash256 keccak);
        void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None);

        void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None);

        bool IsPersisted(in ValueHash256 keccak);

        IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore);

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        // Used for serving via hash
        IReadOnlyKeyValueStore TrieNodeRlpStore { get; }

        // Used by healing
        void Set(in ValueHash256 hash, byte[] rlp);

        void PersistNode(TrieNode trieNode, IWriteBatch? batch = null, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None);
        void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IWriteBatch? keyValueStore = null, WriteFlags writeFlags = WriteFlags.None);

        public void ClearCache();

        void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefix);
        void DeleteByRange(Span<byte> startKey, Span<byte> endKey, IWriteBatch writeBatch = null);
        bool CanAccessByPath();
        bool ShouldResetObjectsOnRootChange();
        void PrefetchForSet(Span<byte> key, byte[] storeNibblePrefix, Hash256 stateRoot);
        void StopPrefetch();
    }
}
