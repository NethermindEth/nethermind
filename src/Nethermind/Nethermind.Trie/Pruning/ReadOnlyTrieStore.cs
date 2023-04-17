// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection.Metadata.Ecma335;
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

        public ReadOnlyTrieStore(TrieStore trieStore, IKeyValueStore? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
        }

        public TrieNode FindCachedOrUnknown(Keccak hash) =>
            _trieStore.FindCachedOrUnknown(hash, true);

        public byte[] LoadRlp(Keccak hash, ReadFlags flags) => _trieStore.LoadRlp(hash, _readOnlyStore, flags);

        public bool IsPersisted(Keccak keccak) => _trieStore.IsPersisted(keccak);

        public IReadOnlyTrieStore AsReadOnly(IKeyValueStore keyValueStore)
        {
            return new ReadOnlyTrieStore(_trieStore, keyValueStore);
        }

        public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo) { }

        public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root) { }

        public void HackPersistOnShutdown() { }

        public event EventHandler<ReorgBoundaryReached> ReorgBoundaryReached
        {
            add { }
            remove { }
        }
        public void Dispose() { }

        public byte[]? this[ReadOnlySpan<byte> key] => _trieStore[key];

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags) => _trieStore.Get(key, flags);
    }
}
