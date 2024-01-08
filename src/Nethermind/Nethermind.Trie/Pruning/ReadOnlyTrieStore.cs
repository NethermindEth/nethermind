// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        private readonly IReadOnlyKeyValueStore _publicStore;

        public ReadOnlyTrieStore(TrieStore trieStore, IKeyValueStore? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
            _publicStore = _trieStore.TrieNodeRlpStore;
        }

        public TrieNode FindCachedOrUnknown(Hash256 hash) =>
            _trieStore.FindCachedOrUnknown(hash, true);

        public byte[]? TryLoadRlp(Hash256 hash, ReadFlags flags) => _trieStore.TryLoadRlp(hash, _readOnlyStore, flags);
        public byte[] LoadRlp(Hash256 hash, ReadFlags flags) => _trieStore.LoadRlp(hash, _readOnlyStore, flags);

        public bool IsPersisted(in ValueHash256 keccak) => _trieStore.IsPersisted(keccak);

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

        public void Set(in ValueHash256 hash, byte[] rlp)
        {
        }

        public void Dispose() { }
    }
}
