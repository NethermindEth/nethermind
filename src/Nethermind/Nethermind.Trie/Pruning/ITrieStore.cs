// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieStore : ITrieNodeResolver, IReadOnlyKeyValueStore, IDisposable
    {
        void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo);

        void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root);

        bool IsPersisted(Keccak keccak);

        IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore);

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

        void SaveNodeDirectly(long blockNumber, TrieNode trieNode, IKeyValueStore? batch = null);

        public void ClearCache();

        public void MarkPrefixDeleted(ReadOnlySpan<byte> keyPrefix);
        public void DeleteByPrefix(ReadOnlySpan<byte> keyPrefix);
    }
}
