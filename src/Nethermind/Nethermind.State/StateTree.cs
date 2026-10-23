// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Nethermind.State.Flat")]

namespace Nethermind.State
{
    public partial class StateTree : PatriciaTree
    {
        private static readonly AccountDecoder _decoder = new();

        public static Rlp EmptyAccountRlp => field ??= _decoder.Encode(Account.TotallyEmpty);

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
        [SkipLocalsInit]
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
            if (bytes.IsEmpty)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            return _decoder.TryDecodeStruct(bytes, out account);
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
            Set(keccak.BytesAsSpan, account is null ? CappedArray<byte>.Empty
                : new CappedArray<byte>(account.IsTotallyEmpty ? EmptyAccountRlp.Bytes : _decoder.EncodeAsBytes(account)));
        }

        [SkipLocalsInit]
        internal void SetAccounts(Dictionary<AddressAsKey, Account?> accounts)
        {
            using ArrayPoolListRef<BulkSetEntry> entries = new(accounts.Count);
            Unsafe.SkipInit(out KeyHashBatch batch);
            batch.Initialize(Address.Size);
            foreach (KeyValuePair<AddressAsKey, Account?> entry in accounts)
            {
                batch.Add(entry.Key.Value.Bytes, entries.Count, out ValueHash256 hash);
                Account? account = entry.Value;
                byte[]? encoded = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp.Bytes : _decoder.EncodeAsBytes(account);
                entries.Add(new(in hash, encoded));
                if (batch.IsFull) batch.Flush(entries.AsSpan());
            }
            batch.Flush(entries.AsSpan());
            BulkSet(entries);
        }

        /// <summary>Writes the changed accounts into the tree.</summary>
        internal partial void WriteAccounts(Dictionary<AddressAsKey, Account?> accounts);

        /// <summary>
        /// Bench (B', <c>FlatDb.FusedAccountTrieUpdate</c>): writes the changed accounts and hashes what they changed below
        /// the root in one parallel pass, one job per top nibble that encodes its own accounts, writes them and hashes its
        /// subtree (<see cref="PatriciaTree.BulkSetAndHashByTopNibble"/>). Only the address hashing, which decides the
        /// nibble, and the grouping stay on this thread. <see cref="UpdateRootHash()"/> is then left the root.
        /// </summary>
        /// <param name="minAccounts">Fewest accounts that take the fused pass; fewer, or a tree that cannot take it
        /// (<see cref="PatriciaTree.CanFuseBulkSetAndHash"/>), go to <see cref="WriteAccounts"/>.</param>
        /// <returns>Whether the fused pass ran.</returns>
        [SkipLocalsInit]
        internal bool WriteAccountsFused(Dictionary<AddressAsKey, Account?> accounts, int minAccounts)
        {
            const int Nibbles = 16;
            int count = accounts.Count;
            if (count < Math.Max(minAccounts, 1) || !CanFuseBulkSetAndHash)
            {
                WriteAccounts(accounts);
                return false;
            }

            BulkSetEntry[] hashed = ArrayPool<BulkSetEntry>.Shared.Rent(count);
            Account?[] hashedAccounts = ArrayPool<Account?>.Shared.Rent(count);
            BulkSetEntry[] grouped = ArrayPool<BulkSetEntry>.Shared.Rent(count);
            Account?[] groupedAccounts = ArrayPool<Account?>.Shared.Rent(count);
            try
            {
                Unsafe.SkipInit(out KeyHashBatch batch);
                batch.Initialize(Address.Size);
                int index = 0;
                foreach (KeyValuePair<AddressAsKey, Account?> entry in accounts)
                {
                    batch.Add(entry.Key.Value.Bytes, index, out ValueHash256 hash);
                    hashed[index] = new(in hash, null);
                    hashedAccounts[index++] = entry.Value;
                    if (batch.IsFull) batch.Flush(hashed);
                }
                batch.Flush(hashed);

                // Counting sort on the top nibble: starts[n] is where nibble n's accounts begin, starts[16] the count.
                Span<int> starts = stackalloc int[Nibbles + 1];
                starts.Clear();
                for (int i = 0; i < count; i++) starts[hashed[i].GetPathNibble(0) + 1]++;
                for (int nib = 0; nib < Nibbles; nib++) starts[nib + 1] += starts[nib];

                Span<int> next = stackalloc int[Nibbles];
                starts[..Nibbles].CopyTo(next);
                for (int i = 0; i < count; i++)
                {
                    int to = next[hashed[i].GetPathNibble(0)]++;
                    grouped[to] = hashed[i];
                    groupedAccounts[to] = hashedAccounts[i];
                }

                // Read here so no job races the lazy initialisation.
                byte[] emptyAccountRlp = EmptyAccountRlp.Bytes;
                BulkSetAndHashByTopNibble(grouped, starts, (start, length) =>
                {
                    for (int i = start; i < start + length; i++)
                    {
                        Account? account = groupedAccounts[i];
                        byte[]? encoded = account is null ? null : account.IsTotallyEmpty ? emptyAccountRlp : _decoder.EncodeAsBytes(account);
                        grouped[i] = new(in grouped[i].Path, encoded);
                    }
                });

                return true;
            }
            finally
            {
                ArrayPool<BulkSetEntry>.Shared.Return(hashed, clearArray: true);
                ArrayPool<Account?>.Shared.Return(hashedAccounts, clearArray: true);
                ArrayPool<BulkSetEntry>.Shared.Return(grouped, clearArray: true);
                ArrayPool<Account?>.Shared.Return(groupedAccounts, clearArray: true);
            }
        }

        public StateTreeBulkSetter BeginSet(int estimatedEntries) => new(estimatedEntries, this);

        public class StateTreeBulkSetter(int estimatedEntries, StateTree tree) : IDisposable
        {
            readonly ArrayPoolList<PatriciaTree.BulkSetEntry> _bulkWrite = new(estimatedEntries);

            public void Set(Address key, Account? account)
            {
                KeccakCache.ComputeTo(key.Bytes, out ValueHash256 keccak);

                // EncodeAsBytes skips the throwaway Rlp wrapper that _decoder.Encode(account) would
                // allocate for every changed account on each block commit.
                byte[]? accountBytes = account is null ? null
                    : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp.Bytes
                    : _decoder.EncodeAsBytes(account);

                _bulkWrite.Add(new BulkSetEntry(keccak, accountBytes));
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
