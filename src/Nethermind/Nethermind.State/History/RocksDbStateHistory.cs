// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;

namespace Nethermind.State.History
{
    /// <summary>
    /// Prototype value store for <see cref="IStateHistory"/> (Βήμα 2). The simplest variant that proves
    /// the core thesis: one sorted store keyed <c>tag ‖ keccak(address) [‖ keccak(slot)] ‖ block</c>
    /// (hash-keyed exactly like the trie / flat backend, so trie-diff backfill ingests the hashes straight
    /// off the leaves; block big-endian so RocksDB lexicographic order == numeric order). An as-of read is
    /// ONE <c>SeekForPrev</c> (<see cref="ISortedView.StartBefore"/>) — no separate position index needed.
    ///
    /// Deliberately simple: full values, point Put per change, byte[] on read. Codec, write-batching and
    /// the mmap+Span zero-alloc read path are later optimizations behind the seam (doc 31). This class
    /// exists to MEASURE, not to ship.
    /// </summary>
    public sealed class RocksDbStateHistory(IDb db) : IStateHistory
    {
        private const byte TagAccount = 0x01;
        private const byte TagStorage = 0x02;
        private const byte FlagAlive = 0x00;
        private const byte FlagTombstone = 0x01;

        private const int HashSize = 32;                       // keccak(address) / keccak(slot)
        private const int BlockSize = 4;                       // uint32 BE — like LogIndex (mainnet < 2^31)
        private const int MaxKeyLength = 1 + HashSize + HashSize + BlockSize;

        private readonly IDb _db = db ?? throw new ArgumentNullException(nameof(db));
        private readonly ISortedKeyValueStore _sorted = db as ISortedKeyValueStore
            ?? throw new ArgumentException($"{nameof(IStateHistory)} requires a sorted (RocksDB-backed) store.", nameof(db));

        private long _minBlock = long.MaxValue;
        private long _maxBlock = long.MinValue;

        public bool Covers(long blockNumber) => _maxBlock >= _minBlock && blockNumber >= _minBlock && blockNumber <= _maxBlock;

        public bool TryGetAccountRlp(long blockNumber, Address address, out byte[]? accountRlp) =>
            TryGetAccountRlpByHash(blockNumber, KeccakCache.Compute(address.Bytes), out accountRlp);

        public byte[]? GetStorage(long blockNumber, Address address, in UInt256 index)
        {
            Span<byte> slot = stackalloc byte[HashSize];
            index.ToBigEndian(slot);
            return GetStorageByHash(blockNumber, KeccakCache.Compute(address.Bytes), ValueKeccak.Compute(slot));
        }

        /// <summary>Read by raw trie path hash — used by backfill/benchmark which have hashes, not preimages.</summary>
        public bool TryGetAccountRlpByHash(long blockNumber, in ValueHash256 addressHash, out byte[]? accountRlp)
        {
            Span<byte> prefix = stackalloc byte[1 + HashSize];
            prefix[0] = TagAccount;
            addressHash.Bytes.CopyTo(prefix[1..]);
            return TrySeekAsOf(prefix, blockNumber, out accountRlp);
        }

        public byte[]? GetStorageByHash(long blockNumber, in ValueHash256 addressHash, in ValueHash256 slotHash)
        {
            Span<byte> prefix = stackalloc byte[1 + HashSize + HashSize];
            prefix[0] = TagStorage;
            addressHash.Bytes.CopyTo(prefix[1..]);
            slotHash.Bytes.CopyTo(prefix[(1 + HashSize)..]);
            return TrySeekAsOf(prefix, blockNumber, out byte[]? value) ? value : null;
        }

        public void Ingest(long blockNumber, IReadOnlyList<StateChange> changes)
        {
            Span<byte> keyBuffer = stackalloc byte[MaxKeyLength];
            for (int i = 0; i < changes.Count; i++)
            {
                StateChange change = changes[i];
                ReadOnlySpan<byte> key = change.SlotHash is { } slotHash
                    ? BuildStorageKey(change.AddressHash.Bytes, slotHash.Bytes, blockNumber, keyBuffer)
                    : BuildAccountKey(change.AddressHash.Bytes, blockNumber, keyBuffer);

                ReadOnlyMemory<byte> payload = change.IsDeletion ? default : change.Value;
                byte[] value = new byte[1 + payload.Length];
                value[0] = change.IsDeletion ? FlagTombstone : FlagAlive;
                payload.Span.CopyTo(value.AsSpan(1));

                _db.PutSpan(key, value);
            }

            if (blockNumber < _minBlock) _minBlock = blockNumber;
            if (blockNumber > _maxBlock) _maxBlock = blockNumber;
        }

        /// <summary>
        /// The whole proposal in one method: the largest key ≤ (prefix ‖ block) for this exact prefix, via
        /// one <c>StartBefore</c>. Returns false on no-such-entry OR a tombstone (deletion wins over an
        /// older value — the self-destruct / EIP-158 / cleared-slot correctness landmine).
        /// </summary>
        private bool TrySeekAsOf(ReadOnlySpan<byte> prefix, long blockNumber, out byte[]? value)
        {
            value = null;
            int n = prefix.Length;

            Span<byte> lower = stackalloc byte[n + BlockSize];          // prefix ‖ 0          (inclusive)
            prefix.CopyTo(lower);
            BinaryPrimitives.WriteUInt32BigEndian(lower[n..], 0);

            Span<byte> upper = stackalloc byte[n + BlockSize];          // prefix ‖ uint.Max   (exclusive)
            prefix.CopyTo(upper);
            BinaryPrimitives.WriteUInt32BigEndian(upper[n..], uint.MaxValue);

            Span<byte> search = stackalloc byte[n + BlockSize];         // prefix ‖ (block + 1)
            prefix.CopyTo(search);
            uint block = (uint)blockNumber;
            BinaryPrimitives.WriteUInt32BigEndian(search[n..], block == uint.MaxValue ? uint.MaxValue : block + 1);

            using ISortedView view = _sorted.GetViewBetween(lower, upper);
            if (!view.StartBefore(search))                              // no change at or before block
                return false;

            ReadOnlySpan<byte> foundKey = view.CurrentKey;
            if (foundKey.Length < n || !foundKey[..n].SequenceEqual(prefix))   // landed on another key
                return false;

            ReadOnlySpan<byte> raw = view.CurrentValue;
            if (raw.Length == 0 || raw[0] == FlagTombstone)            // deleted/empty at that block
                return false;

            value = raw[1..].ToArray();                                // prototype: copy out; mmap+Span later
            return true;
        }

        private static ReadOnlySpan<byte> BuildAccountKey(ReadOnlySpan<byte> addressHash, long blockNumber, Span<byte> buffer)
        {
            buffer[0] = TagAccount;
            addressHash.CopyTo(buffer[1..]);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[(1 + HashSize)..], (uint)blockNumber);
            return buffer[..(1 + HashSize + BlockSize)];
        }

        private static ReadOnlySpan<byte> BuildStorageKey(ReadOnlySpan<byte> addressHash, ReadOnlySpan<byte> slotHash, long blockNumber, Span<byte> buffer)
        {
            buffer[0] = TagStorage;
            addressHash.CopyTo(buffer[1..]);
            slotHash.CopyTo(buffer[(1 + HashSize)..]);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[(1 + HashSize + HashSize)..], (uint)blockNumber);
            return buffer[..(1 + HashSize + HashSize + BlockSize)];
        }
    }
}
