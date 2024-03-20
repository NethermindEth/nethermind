// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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

        public IReadOnlyTrieStore AsReadOnly() => this;

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }

        public IReadOnlyKeyValueStore TrieNodeRlpStore => null!;

        public TrieNode FindCachedOrUnknown(Hash256 hash) => new(NodeType.Unknown, hash);

        public T LoadRlp<T, TDeserializer>(TDeserializer deserializer, Hash256 hash, ReadFlags flags = ReadFlags.None) where TDeserializer : ISpanDeserializer<T>
        {
            return deserializer.Deserialize(null);
        }

        public bool IsPersisted(in ValueHash256 keccak) => true;

        public void Dispose() { }

        public void Set(in ValueHash256 hash, byte[] rlp)
        {
        }

        public bool HasRoot(Hash256 stateRoot)
        {
            return stateRoot == Keccak.EmptyTreeHash;
        }
    }
}
