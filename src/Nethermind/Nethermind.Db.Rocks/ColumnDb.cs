// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnDb : IDb, ISortedKeyValueStore, IMergeableKeyValueStore, IKeyValueStoreWithSnapshot, ISstIngestible
{
    private static long _sstIngestSeq;

    private readonly RocksDb _rocksDb;
    internal readonly DbOnTheRocks _mainDb;
    internal readonly ColumnFamilyHandle _columnFamily;
    internal Action? _testIngestFailureHook;

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

    public ISstIngestWriteBatch StartSstIngestBatch() => new SstIngestWriteBatch(this);

    public string IngestStagingDir => Path.Combine(_mainDb.FullPath, "sst_ingest");

    private const int MaxL0FilesBeforeThrottle = 20;
    private const int L0DrainMaxPolls = 1500;
    private const int L0DrainPollMs = 20;

    private readonly IngestExternalFileOptions _ingestOptions = new IngestExternalFileOptions()
        .SetMoveFiles(true)
        .SetAllowGlobalSeqno(true)
        .SetAllowBlockingFlush(true);

    public void IngestStagedFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        _testIngestFailureHook?.Invoke();
        _rocksDb.IngestExternalFiles([.. files], _ingestOptions, _columnFamily);
    }

    public void WaitForIngestCompactionHeadroom()
    {
        for (int i = 0; i < L0DrainMaxPolls; i++)
        {
            string? v = _rocksDb.GetProperty("rocksdb.num-files-at-level0", _columnFamily);
            if (!int.TryParse(v, out int l0Files) || l0Files < MaxL0FilesBeforeThrottle) return;
            Thread.Sleep(L0DrainPollMs);
        }

        ILogger logger = _mainDb.Logger;
        if (logger.IsWarn) logger.Warn($"L0 of {_mainDb.Name} column {Name} did not drain below {MaxL0FilesBeforeThrottle} files within {L0DrainMaxPolls * L0DrainPollMs / 1000}s; continuing SST ingestion without compaction headroom");
    }

    private sealed class SstIngestWriteBatch(ColumnDb columnDb) : ISstIngestWriteBatch
    {
        private const long MaxBufferedBytes = 128L * 1024 * 1024;
        private const int SlabSize = 1 << 20;

        // Worst-case permanent retention: slabs <= 1024 x 1 MiB = 1 GiB; entries <= 6 arrays/bucket over 2^16..2^22 x 32 B ~= 1.5 GiB.
        // 6 covers peak concurrency: six column batches alive per persist, one persist in flight.
        private static readonly ArrayPool<byte> s_slabPool = ArrayPool<byte>.Create(SlabSize, 1024);
        private static readonly ArrayPool<Entry> s_entryPool = ArrayPool<Entry>.Create(1 << 22, 6);
        private static readonly EnvOptions s_envOptions = new();

        private readonly ColumnDb _columnDb = columnDb;
        private readonly List<byte[]> _slabs = [];
        private readonly List<string> _stagedFiles = [];
        private Entry[] _index = s_entryPool.Rent(1 << 16);
        private int _count;
        private int _slabIndex = -1;
        private int _slabOffset;
        private long _bufferedBytes;

        private struct Entry
        {
            public ulong KeyPrefix;
            public int Slab;
            public int Offset;
            public int KeyLen;
            public int ValLen; // -1 encodes delete
            public int Seq;
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (value is null) Append(key, default, isDelete: true);
            else Append(key, value, isDelete: false);
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            Append(key, value, isDelete: false);

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            throw new NotSupportedException("SST ingestion does not support merge writes");

        private void Append(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool isDelete)
        {
            int length = key.Length + (isDelete ? 0 : value.Length);
            Span<byte> destination = Reserve(length, out int slab, out int offset);
            key.CopyTo(destination);
            if (!isDelete) value.CopyTo(destination[key.Length..]);

            if (_count == _index.Length) GrowIndex();
            _index[_count] = new Entry
            {
                KeyPrefix = ReadPrefix(key),
                Slab = slab,
                Offset = offset,
                KeyLen = key.Length,
                ValLen = isDelete ? -1 : value.Length,
                Seq = _count,
            };
            _count++;

            _bufferedBytes += length + Unsafe.SizeOf<Entry>();
            if (_bufferedBytes >= MaxBufferedBytes) FlushChunk();
        }

        private static ulong ReadPrefix(ReadOnlySpan<byte> key)
        {
            if (key.Length >= sizeof(ulong)) return BinaryPrimitives.ReadUInt64BigEndian(key);
            Span<byte> padded = stackalloc byte[sizeof(ulong)];
            padded.Clear();
            key.CopyTo(padded);
            return BinaryPrimitives.ReadUInt64BigEndian(padded);
        }

        private Span<byte> Reserve(int length, out int slab, out int offset)
        {
            if (length > SlabSize)
            {
                byte[] dedicated = new byte[length];
                _slabs.Add(dedicated);
                slab = _slabs.Count - 1;
                offset = 0;
                return dedicated;
            }

            if (_slabIndex < 0 || _slabOffset + length > SlabSize)
            {
                do
                {
                    _slabIndex++;
                }
                while (_slabIndex < _slabs.Count && _slabs[_slabIndex].Length != SlabSize);

                if (_slabIndex == _slabs.Count) _slabs.Add(s_slabPool.Rent(SlabSize));
                _slabOffset = 0;
            }

            slab = _slabIndex;
            offset = _slabOffset;
            _slabOffset += length;
            return _slabs[slab].AsSpan(offset, length);
        }

        private void GrowIndex()
        {
            Entry[] grown = s_entryPool.Rent(_index.Length * 2);
            Array.Copy(_index, grown, _count);
            s_entryPool.Return(_index);
            _index = grown;
        }

        public void Clear()
        {
            for (int i = _slabs.Count - 1; i >= 0; i--)
            {
                if (_slabs[i].Length != SlabSize) _slabs.RemoveAt(i);
            }
            _count = 0;
            _slabIndex = _slabs.Count > 0 ? 0 : -1;
            _slabOffset = 0;
            _bufferedBytes = 0;
        }

        private ReadOnlySpan<byte> KeySpan(in Entry e) => _slabs[e.Slab].AsSpan(e.Offset, e.KeyLen);

        private bool IsSameKey(in Entry x, in Entry y) =>
            x.KeyPrefix == y.KeyPrefix && x.KeyLen == y.KeyLen && KeySpan(in x).SequenceEqual(KeySpan(in y));

        private sealed class EntryComparer(SstIngestWriteBatch batch) : IComparer<Entry>
        {
            public int Compare(Entry x, Entry y)
            {
                int c = x.KeyPrefix.CompareTo(y.KeyPrefix);
                if (c != 0) return c;
                c = batch.KeySpan(in x).SequenceCompareTo(batch.KeySpan(in y));
                return c != 0 ? c : x.Seq.CompareTo(y.Seq);
            }
        }

        private unsafe void FlushChunk()
        {
            if (_count == 0) return;

            Array.Sort(_index, 0, _count, new EntryComparer(this));

            Directory.CreateDirectory(_columnDb.IngestStagingDir);
            string file = Path.Combine(_columnDb.IngestStagingDir, $"{_columnDb.Name}_{Interlocked.Increment(ref _sstIngestSeq)}.sst");

            try
            {
                ColumnFamilyOptions writerOptions = _columnDb._mainDb.GetColumnFamilyOptions(_columnDb.Name)
                    ?? throw new InvalidOperationException($"No column family options registered for column {_columnDb.Name} of {_columnDb._mainDb.Name}");
                IntPtr writer = Native.Instance.rocksdb_sstfilewriter_create(s_envOptions.Handle, writerOptions.Handle);
                try
                {
                    Native.Instance.rocksdb_sstfilewriter_open(writer, file);
                    for (int i = 0; i < _count; i++)
                    {
                        ref Entry e = ref _index[i];
                        // Equal keys sort by ascending Seq; only the last of each run (the latest write) is emitted.
                        if (i + 1 < _count && IsSameKey(in e, in _index[i + 1])) continue;
                        fixed (byte* slabPtr = &MemoryMarshal.GetArrayDataReference(_slabs[e.Slab]))
                        {
                            byte* data = slabPtr + e.Offset;
                            if (e.ValLen < 0) Native.Instance.rocksdb_sstfilewriter_delete(writer, data, (UIntPtr)e.KeyLen);
                            else Native.Instance.rocksdb_sstfilewriter_put(writer, data, (UIntPtr)e.KeyLen, data + e.KeyLen, (UIntPtr)e.ValLen);
                        }
                    }
                    Native.Instance.rocksdb_sstfilewriter_finish(writer);
                }
                finally
                {
                    Native.Instance.rocksdb_sstfilewriter_destroy(writer);
                }
            }
            catch
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                throw;
            }

            _stagedFiles.Add(file);
            Clear();
        }

        public IReadOnlyList<string> SealToStagedFiles()
        {
            FlushChunk();
            return _stagedFiles;
        }

        public void IngestStagedFiles()
        {
            _columnDb.IngestStagedFiles(_stagedFiles);
            _stagedFiles.Clear();
        }

        public void DeleteStagedFiles()
        {
            foreach (string file in _stagedFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            _stagedFiles.Clear();
        }

        public void Dispose()
        {
            foreach (byte[] slab in _slabs)
            {
                if (slab.Length == SlabSize) s_slabPool.Return(slab);
            }
            _slabs.Clear();
            s_entryPool.Return(_index);
            _index = [];
        }
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
