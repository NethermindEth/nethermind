// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieStore : ITrieNodeResolver, IDisposable
    {
        void OpenContext(long blockNumber, Keccak keccak);
        void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None);

        void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None);

        bool IsPersisted(in ValueKeccak keccak);

        IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore);

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        IKeyValueStore AsKeyValueStore();

        void PersistNode(TrieNode trieNode, IKeyValueStore? batch = null, bool withDelete = false, WriteFlags writeFlags = WriteFlags.None);
        void PersistNodeData(Span<byte> fullPath, int pathToNodeLength, byte[]? rlpData, IKeyValueStore? keyValueStore = null, WriteFlags writeFlags = WriteFlags.None);

        public void ClearCache();

        void MarkPrefixDeleted(long blockNumber, ReadOnlySpan<byte> keyPrefix);
        void DeleteByRange(Span<byte> startKey, Span<byte> endKey);

        bool CanAccessByPath();
    }
}
