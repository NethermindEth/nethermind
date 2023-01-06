// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieStore : ITrieNodeResolver, IReadOnlyKeyValueStore, ISyncTrieStore, IDisposable
    {
        void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo);

        void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root);

        bool IsPersisted(Keccak keccak);

        IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore);

        event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    }
}
