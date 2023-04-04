// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieStore : IReadOnlyTrieStore
    {
        private NullTrieStore() { }

        public static NullTrieStore Instance { get; } = new();

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root) { }

        public void HackPersistOnShutdown() { }

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore keyValueStore)
        {
            return this;
        }

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            return new(NodeType.Unknown, hash);
        }

        public byte[] LoadRlp(Keccak hash)
        {
            return Array.Empty<byte>();
        }

        public bool IsPersisted(Keccak keccak) => true;

        public void Dispose() { }

        public bool IsFullySynced(Keccak stateRoot) => false;
        public byte[]? this[ReadOnlySpan<byte> key] => null;
    }
}
