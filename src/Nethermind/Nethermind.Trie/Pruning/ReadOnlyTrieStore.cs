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
        private readonly ReadOnlyValueStore _publicStore;

        public ReadOnlyTrieStore(TrieStore trieStore, IKeyValueStore? readOnlyStore)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _readOnlyStore = readOnlyStore;
            _publicStore = new ReadOnlyValueStore(_trieStore.AsKeyValueStore());
        }

        public TrieNode FindCachedOrUnknown(Keccak hash) =>
            _trieStore.FindCachedOrUnknown(hash, true);

        public byte[] LoadRlp(Keccak hash, ReadFlags flags) => _trieStore.LoadRlp(hash, _readOnlyStore, flags);

        public bool IsPersisted(in ValueKeccak keccak) => _trieStore.IsPersisted(keccak);

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

        public IKeyValueStore AsKeyValueStore() => _publicStore;

        public void Dispose() { }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }

        public bool IsFullySynced(Keccak stateRoot) => _trieStore.IsFullySynced(stateRoot);

        private class ReadOnlyValueStore : IKeyValueStore
        {
            private readonly IKeyValueStore _keyValueStore;

            public ReadOnlyValueStore(IKeyValueStore keyValueStore)
            {
                _keyValueStore = keyValueStore;
            }

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => _keyValueStore.Get(key, flags);

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }
        }
    }
}
