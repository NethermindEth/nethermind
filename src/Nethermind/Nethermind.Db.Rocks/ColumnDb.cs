// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnDb : IDb, ISortedKeyValueStore, IMergeableKeyValueStore, IKeyValueStoreWithSnapshot, ISstIngestible
{
    private static long _sstIngestSeq;

    private readonly RocksDb _rocksDb;
    internal readonly DbOnTheRocks _mainDb;
    internal readonly ColumnFamilyHandle _columnFamily;

    private readonly DbOnTheRocks.IteratorManager _iteratorManager;
    private readonly RocksDbReader _reader;

    public ColumnDb(RocksDb rocksDb, DbOnTheRocks mainDb, string name)
    {
        _rocksDb = rocksDb;
        _mainDb = mainDb;
        if (name == "Default") name = "default";
        _columnFamily = _rocksDb.GetColumnFamily(name);
        Name = name;

        _iteratorManager = new DbOnTheRocks.IteratorManager(_rocksDb, _columnFamily, _mainDb._readAheadReadOptions);
        _reader = new RocksDbReader(mainDb, mainDb.CreateReadOptions, _iteratorManager, _columnFamily);
    }

    public void Dispose() => _iteratorManager.Dispose();
    public string Name { get; }

    byte[]? IReadOnlyKeyValueStore.Get(ReadOnlySpan<byte> key, ReadFlags flags) => _reader.Get(key, flags);

    Span<byte> IReadOnlyKeyValueStore.GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags) => _reader.GetSpan(key, flags);

    MemoryManager<byte>? IReadOnlyKeyValueStore.GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags)
    {
        Span<byte> span = ((IReadOnlyKeyValueStore)this).GetSpan(key, flags);
        return span.IsNullOrEmpty() ? null : new DbSpanMemoryManager(this, span);
    }


    int IReadOnlyKeyValueStore.Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags) => _reader.Get(key, output, flags);

    bool IReadOnlyKeyValueStore.KeyExists(ReadOnlySpan<byte> key) => _reader.KeyExists(key);

    void IReadOnlyKeyValueStore.DangerousReleaseMemory(in ReadOnlySpan<byte> key) => _reader.DangerousReleaseMemory(key);

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, flags);

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None) =>
        _mainDb.SetWithColumnFamily(key, _columnFamily, value, writeFlags);

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None) =>
        _mainDb.MergeWithColumnFamily(key, _columnFamily, value, writeFlags);

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            ColumnFamilyHandle[] columnFamilies = new ColumnFamilyHandle[keys.Length];
            Array.Fill(columnFamilies, _columnFamily);
            return _rocksDb.MultiGet(keys, columnFamilies);
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllCore(iterator);
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllKeysCore(iterator);
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        Iterator iterator = _mainDb.CreateIterator(ordered, _columnFamily);
        return _mainDb.GetAllValuesCore(iterator);
    }

    public IWriteBatch StartWriteBatch() => new ColumnsDbWriteBatch(this, (DbOnTheRocks.RocksDbWriteBatch)_mainDb.StartWriteBatch());

    private class ColumnsDbWriteBatch(ColumnDb columnDb, DbOnTheRocks.RocksDbWriteBatch underlyingWriteBatch)
        : IWriteBatch
    {
        public void Dispose() => underlyingWriteBatch.Dispose();

        public void Clear() => underlyingWriteBatch.Clear();

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (value is null)
            {
                underlyingWriteBatch.Delete(key, columnDb._columnFamily);
            }
            else
            {
                underlyingWriteBatch.Set(key, value, columnDb._columnFamily, flags);
            }
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            underlyingWriteBatch.Set(key, value, columnDb._columnFamily, flags);

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            underlyingWriteBatch.Merge(key, value, columnDb._columnFamily, flags);
    }

    public IWriteBatch StartSstIngestBatch() => new SstIngestWriteBatch(this);

    // Buffers point puts/deletes in a byte-capped managed buffer; at the cap (and on Dispose) it flushes the buffer
    // as a sorted SST and ingests it into this column — bypassing the memtable -> flush -> L0-compaction burst of a
    // large WriteBatch. Buffering is on the managed heap, hence the byte cap to bound peak persist memory.
    private sealed class SstIngestWriteBatch(ColumnDb columnDb) : IWriteBatch
    {
        // Byte cap on the in-memory buffer. At the cap we flush+ingest one SST and free the buffer, so a large
        // persist (e.g. 10x state) never holds the whole batch in managed memory — that transient spike, on top
        // of the working set, OOM-kills the node. Overlapping chunks are fine: each ingest gets a higher global
        // seqno, so the later (final) value wins, which also preserves last-write-wins across chunk boundaries.
        private const long MaxBufferedBytes = 128L * 1024 * 1024;
        // Throttle the persist thread when L0 files pile up, bounding the native compaction working set.
        private const int MaxL0FilesBeforeThrottle = 20;

        // Last write wins within a chunk: a SelfDestruct (delete) followed by re-creation (put) can target the
        // same key, and an SST forbids duplicate keys, so collapse to the final value before writing.
        private readonly Dictionary<byte[], byte[]?> _entries = new(Bytes.EqualityComparer);
        private long _bufferedBytes;
        private readonly IngestExternalFileOptions _options = new IngestExternalFileOptions()
            .SetMoveFiles(true)
            .SetAllowGlobalSeqno(true)
            .SetAllowBlockingFlush(true);

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            byte[] k = key.ToArray();
            if (_entries.TryGetValue(k, out byte[]? existing)) _bufferedBytes -= existing?.Length ?? 0;
            else _bufferedBytes += k.Length;
            _entries[k] = value;
            _bufferedBytes += value?.Length ?? 0;
            if (_bufferedBytes >= MaxBufferedBytes) FlushChunk();
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            throw new NotSupportedException("SST ingestion does not support merge writes");

        public void Clear()
        {
            _entries.Clear();
            _bufferedBytes = 0;
        }

        private void FlushChunk()
        {
            if (_entries.Count == 0) return;

            List<KeyValuePair<byte[], byte[]?>> items = [.. _entries];
            items.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));

            string dir = Path.Combine(columnDb._mainDb.FullPath, "sst_ingest");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{columnDb.Name}_{Interlocked.Increment(ref _sstIngestSeq)}.sst");

            try
            {
                ColumnFamilyOptions writerOptions = columnDb._mainDb.GetColumnFamilyOptions(columnDb.Name) ?? new ColumnFamilyOptions();
                using (SstFileWriter writer = new(new EnvOptions(), writerOptions))
                {
                    writer.Open(file);
                    foreach ((byte[] key, byte[]? value) in items)
                    {
                        if (value is null) writer.Delete(key);
                        else writer.Put(key, value);
                    }
                    writer.Finish();
                }

                // SetMoveFiles(true): on success RocksDB moves (consumes) the file. Only a Finish/ingest failure
                // leaves it staged, so delete it on failure to stop sst_ingest/ accumulating orphaned files.
                columnDb._rocksDb.IngestExternalFiles([file], _options, columnDb._columnFamily);
            }
            catch
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
                throw;
            }

            Clear();
            WaitForCompactionHeadroom();
        }

        // Ingestion bypasses RocksDB's memtable write-stall, so back-to-back ingests pile up L0 files and the
        // compaction working set grows without bound (native memory OOM at large state). Replicate the missing
        // flow control: throttle the persist thread until L0 drains — exactly what the memtable write path does.
        private void WaitForCompactionHeadroom()
        {
            for (int i = 0; i < 1500; i++) // ~30s safety cap
            {
                string? v = columnDb._rocksDb.GetProperty("rocksdb.num-files-at-level0", columnDb._columnFamily);
                if (!int.TryParse(v, out int l0Files) || l0Files < MaxL0FilesBeforeThrottle) return;
                Thread.Sleep(20);
            }
        }

        public void Dispose() => FlushChunk();
    }

    public void Remove(ReadOnlySpan<byte> key) => Set(key, null);

    public void Flush(bool onlyWal) => _mainDb.FlushWithColumnFamily(_columnFamily);

    public void Compact() =>
        _rocksDb.CompactRange(Keccak.Zero.BytesToArray(), Keccak.MaxValue.BytesToArray(), _columnFamily);

    /// <summary>
    /// Not sure how to handle delete of the columns DB
    /// </summary>
    /// <exception cref="NotSupportedException"></exception>
    public void Clear() => throw new NotSupportedException();

    // Maybe it should be column-specific metric?
    public IDbMeta.DbMetric GatherMetric() => _mainDb.GatherMetric();

    public void SetWriteBuffer(long sizeBytes)
    {
        string[] keys = ["write_buffer_size", "max_bytes_for_level_base"];
        string[] values = [sizeBytes.ToString(), (sizeBytes * 4).ToString()];
        Native.Instance.rocksdb_set_options_cf(
            _rocksDb.Handle, _columnFamily.Handle, keys.Length, keys, values);
    }

    public byte[]? FirstKey
    {
        get
        {
            using Iterator iterator = _mainDb.CreateIterator(_mainDb.CreateReadOptions(), ch: _columnFamily);
            iterator.SeekToFirst();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public byte[]? LastKey
    {
        get
        {
            using Iterator iterator = _mainDb.CreateIterator(_mainDb.CreateReadOptions(), ch: _columnFamily);
            iterator.SeekToLast();
            return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
        }
    }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey) =>
        _mainDb.GetViewBetween(firstKey, lastKey, _columnFamily);

    public IKeyValueStoreSnapshot CreateSnapshot()
    {
        Snapshot snapshot = _rocksDb.CreateSnapshot();

        return new DbOnTheRocks.RocksDbSnapshot(
            _mainDb,
            () =>
            {
                ReadOptions readOptions = _mainDb.CreateReadOptions();
                readOptions.SetSnapshot(snapshot);
                return readOptions;
            },
            _columnFamily,
            snapshot);
    }
}
