// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StateTree : PatriciaTree
    {
        private static readonly AccountDecoder _decoder = new();

        public static readonly Rlp EmptyAccountRlp = _decoder.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTree(ICappedArrayPool? bufferPool = null)
            : base(new MemDb(), Keccak.EmptyTreeHash, true, NullLogManager.Instance, bufferPool: bufferPool) => TrieType = TrieType.State;

        [DebuggerStepThrough]
        public StateTree(IScopedTrieStore store, ILogManager logManager)
            : base(store, Keccak.EmptyTreeHash, true, logManager) => TrieType = TrieType.State;

        public StateTree(ITrieStore store, ILogManager logManager)
            : base(store.GetTrieStore(null), logManager)
        {
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(KeccakCache.Compute(address.Bytes).BytesAsSpan, rootHash);
            if (bytes.IsEmpty)
            {
                return null;
            }

            RlpReader context = new(bytes);
            return _decoder.Decode(ref context);
        }

        [DebuggerStepThrough]
        public bool TryGetStruct(Address address, out AccountStruct account, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(KeccakCache.Compute(address.Bytes).BytesAsSpan, rootHash);
            RlpReader reader = new(bytes);
            if (bytes.IsEmpty)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            return _decoder.TryDecodeStruct(ref reader, out account);
        }

        [DebuggerStepThrough]
        internal Account? Get(Hash256 keccak) // for testing
        {
            ReadOnlySpan<byte> bytes = Get(keccak.Bytes);
            if (bytes.IsEmpty)
            {
                return null;
            }

            RlpReader context = new(bytes);
            return _decoder.Decode(ref context);
        }

        public void Set(Address address, Account? account)
        {
            KeccakCache.ComputeTo(address.Bytes, out ValueHash256 keccak);
            Set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : _decoder.Encode(account));
        }

        public StateTreeBulkSetter BeginSet(int estimatedEntries) => new(estimatedEntries, this);

        public class StateTreeBulkSetter(int estimatedEntries, StateTree tree) : IDisposable
        {
            readonly ArrayPoolList<PatriciaTree.BulkSetEntry> _bulkWrite = new(estimatedEntries);

            public void Set(Address key, Account? account)
            {
                KeccakCache.ComputeTo(key.Bytes, out ValueHash256 keccak);

                Rlp? accountRlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : _decoder.Encode(account);

                _bulkWrite.Add(new BulkSetEntry(keccak, accountRlp?.Bytes));
            }

            public void Dispose()
            {
                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef = _bulkWrite.ToRef();
                tree.BulkSet(asRef);
            }
        }

        [DebuggerStepThrough]
        public Rlp? Set(Hash256 keccak, Account? account)
        {
            Rlp? rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : _decoder.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }

        public Rlp? Set(in ValueHash256 keccak, Account? account)
        {
            Rlp? rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : _decoder.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }

        public Account? Get(Address address) => Get(address, null);

        public void UpdateRootHash() => UpdateRootHash(true);

        public void Commit() => Commit(false, WriteFlags.None);
    }
}
