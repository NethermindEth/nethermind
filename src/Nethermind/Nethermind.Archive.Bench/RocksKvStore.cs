// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Db;
using RocksDbSharp;

namespace Nethermind.Archive.Bench
{
    /// <summary>
    /// Minimal <see cref="IDb"/> + <see cref="ISortedKeyValueStore"/> adapter over a raw RocksDbSharp
    /// handle. Avoids <c>DbOnTheRocks</c> (which always opens read-write) and its config plumbing, and —
    /// crucially for SAFETY — when <paramref name="readOnly"/> the handle is opened with
    /// <c>RocksDb.OpenReadOnly</c> and every write throws: the production archive physically cannot be
    /// mutated by this tool. The same class backs the writable temp history store (readOnly=false).
    /// </summary>
    public sealed class RocksKvStore(RocksDb db, bool readOnly, string name = "bench") : IDb, ISortedKeyValueStore
    {
        public string Name => name;

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => db.Get(key.ToArray());

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (readOnly) throw new InvalidOperationException("Read-only store — refusing to write (archive safety).");
            if (value is null) db.Remove(key.ToArray());
            else db.Put(key.ToArray(), value);
        }

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
            new SortedView(db, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray());

        public byte[]? FirstKey => throw new NotSupportedException();
        public byte[]? LastKey => throw new NotSupportedException();

        /// <summary>Total on-disk SST bytes — the size metric for the benchmark.</summary>
        public long SstSizeBytes() => long.TryParse(db.GetProperty("rocksdb.total-sst-files-size"), out long v) ? v : 0;

        public void Flush(bool onlyWal = false) { }
        public void Dispose() => db.Dispose();

        // --- IDb surface not exercised by the prototype ---
        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => throw new NotSupportedException();
        public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false) => throw new NotSupportedException();
        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => throw new NotSupportedException();
        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => throw new NotSupportedException();
        Nethermind.Core.IWriteBatch IKeyValueStoreWithBatching.StartWriteBatch() => throw new NotSupportedException();

        private sealed class SortedView : ISortedView
        {
            private readonly Iterator _it;

            public SortedView(RocksDb db, byte[] lower, byte[] upper)
            {
                ReadOptions ro = new ReadOptions().SetIterateLowerBound(lower).SetIterateUpperBound(upper);
                _it = db.NewIterator(readOptions: ro);
            }

            public bool StartBefore(ReadOnlySpan<byte> value) { _it.SeekForPrev(value.ToArray()); return _it.Valid(); }
            public bool MoveNext() { _it.Next(); return _it.Valid(); }
            public ReadOnlySpan<byte> CurrentKey => _it.Key();
            public ReadOnlySpan<byte> CurrentValue => _it.Value();
            public void Dispose() => _it.Dispose();
        }
    }
}
