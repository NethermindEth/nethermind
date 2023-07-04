// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags flags = WriteFlags.None) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags flags = WriteFlags.None) { }

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

        public byte[] LoadRlp(Keccak hash, ReadFlags flags = ReadFlags.None)
        {
            return Array.Empty<byte>();
        }

        public bool IsPersisted(Keccak keccak) => true;
        public bool IsPersisted(in ValueKeccak keccak) => true;

        public void Dispose() { }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return null;
        }
    }
}
