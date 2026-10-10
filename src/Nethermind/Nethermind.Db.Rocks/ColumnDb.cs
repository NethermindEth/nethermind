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
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.RocksDbBindings.Native;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnDb : IDb, ISortedKeyValueStore, IMergeableKeyValueStore, IKeyValueStoreWithSnapshot, IRangeRemovableKeyValueStore, ISstIngestible
{
    private static long _sstIngestSeq;

    private readonly RocksDb _rocksDb;
    internal readonly DbOnTheRocks _mainDb;
    internal readonly IColumnFamilyHandle _columnFamily;

    private readonly DisposableLazy<DbOnTheRocks.IteratorManager>? _iteratorManager;
    private readonly DisposableLazy<DbOnTheRocks.IteratorManager> _seekIteratorManager;
    private readonly RocksDbReader _reader;

    public ColumnDb(RocksDb rocksDb, DbOnTheRocks mainDb, string name)
    {
        _rocksDb = rocksDb;
        _mainDb = mainDb;
        if (name == "Default") name = "default";
        _columnFamily = _rocksDb.GetColumnFamily(name);
        Name = name;

        _iteratorManager = _mainDb.CreateLazyReadAheadIteratorManager(_columnFamily);
        _seekIteratorManager = _mainDb.CreateLazySeekIteratorManager(_columnFamily);
        _reader = new RocksDbReader(mainDb, mainDb.CreateReadOptions, _iteratorManager, _columnFamily);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _iteratorManager?.Dispose();
        _seekIteratorManager.Dispose();
    }

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
            _mainDb.ThrowIfDisposing();
            _mainDb.UpdateReadMetrics(keys.Length);

            IColumnFamilyHandle[] columnFamilies = new IColumnFamilyHandle[keys.Length];
            Array.Fill(columnFamilies, _columnFamily);
            try
            {
                return _rocksDb.MultiGet(keys, columnFamilies);
            }
            catch (RocksDbException e)
            {
                _mainDb.HandleFatalDbError(e);
                throw;
            }
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
    {
        _mainDb.ThrowIfDisposing();
        return _mainDb.GetAllCore(ordered, _columnFamily);
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        _mainDb.ThrowIfDisposing();
        return _mainDb.GetAllKeysCore(ordered, _columnFamily);
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        _mainDb.ThrowIfDisposing();
        return _mainDb.GetAllValuesCore(ordered, _columnFamily);
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

    private static readonly IngestExternalFileOptions IngestOptions = new IngestExternalFileOptions()
        .SetMoveFiles(true)
        .SetAllowGlobalSeqno(true)
        .SetAllowBlockingFlush(true);

    public void IngestStagedFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        try
        {
            _rocksDb.IngestExternalFiles([.. files], IngestOptions, _columnFamily);
        }
        catch (RocksDbException x)
        {
            // Ingestion also flushes this column's memtable and reads live SST metadata to pick a target level, so
            // corruption reported here is only the staged file's when the message names one; otherwise it is the
            // live DB's and must still schedule the repair marker.
            bool stagedFileCorruption = false;
            foreach (string file in files)
            {
                if (x.Message.Contains(Path.GetFileName(file), StringComparison.Ordinal))
                {
                    stagedFileCorruption = true;
                    break;
                }
            }

            _mainDb.HandleFatalDbError(x, scheduleRepairMarker: !stagedFileCorruption);
            throw;
        }
    }

    public void WaitForIngestCompactionHeadroom(CancellationToken cancellationToken)
    {
        for (int i = 0; i < L0DrainMaxPolls; i++)
        {
            if (cancellationToken.IsCancellationRequested) return;
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
        private const int RunBufferSize = 1 << 20;
        private const int RunHeaderSize = 2 * sizeof(int);

        // Worst-case permanent retention: slabs <= 1024 x 1 MiB = 1 GiB; entries <= 6 arrays/bucket over 2^16..2^22 x 32 B ~= 1.5 GiB.
        // 6 covers peak concurrency: six column batches alive per persist, one persist in flight.
        private static readonly ArrayPool<byte> _slabPool = ArrayPool<byte>.Create(SlabSize, 1024);
        private static readonly ArrayPool<Entry> _entryPool = ArrayPool<Entry>.Create(1 << 22, 6);
        // Dedicated pool for the slab-reference list backing so the per-persist list allocation stays off the shared
        // pool; sized to the worst-case 1024 slabs over the six concurrent column batches of a single in-flight persist.
        private static readonly ArrayPool<byte[]> _slabListPool = ArrayPool<byte[]>.Create(1024, 6);
        private static readonly EnvOptions _envOptions = new();

        private readonly ColumnDb _columnDb = columnDb;
        private readonly ArrayPoolList<byte[]> _slabs = new(_slabListPool, 16);
        private readonly ArrayPoolList<string> _stagedFiles = new(4);
        private readonly ArrayPoolList<string> _runFiles = new(4);
        private Entry[] _index = _entryPool.Rent(1 << 16);
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
            if (_bufferedBytes >= MaxBufferedBytes) SpillRun();
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

                if (_slabIndex == _slabs.Count) _slabs.Add(_slabPool.Rent(SlabSize));
                _slabOffset = 0;
            }

            slab = _slabIndex;
            offset = _slabOffset;
            _slabOffset += length;
            return _slabs[slab].AsSpan(offset, length);
        }

        private void GrowIndex()
        {
            Entry[] grown = _entryPool.Rent(_index.Length * 2);
            Array.Copy(_index, grown, _count);
            _entryPool.Return(_index);
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

        private readonly struct EntryComparer(byte[][] slabs) : IComparer<Entry>
        {
            private ReadOnlySpan<byte> KeySpan(in Entry e) => slabs[e.Slab].AsSpan(e.Offset, e.KeyLen);

            public bool IsSameKey(in Entry x, in Entry y) =>
                x.KeyPrefix == y.KeyPrefix && x.KeyLen == y.KeyLen && KeySpan(in x).SequenceEqual(KeySpan(in y));

            public int Compare(Entry x, Entry y)
            {
                int c = x.KeyPrefix.CompareTo(y.KeyPrefix);
                if (c != 0) return c;
                c = KeySpan(in x).SequenceCompareTo(KeySpan(in y));
                return c != 0 ? c : x.Seq.CompareTo(y.Seq);
            }
        }

        private unsafe void FlushChunk()
        {
            if (_count == 0) return;

            EntryComparer comparer = new(_slabs.UnsafeGetInternalArray());
            _index.AsSpan(0, _count).Sort(comparer);

            string file = NextStagingPath("sst");

            try
            {
                using SstFileWriter writer = new(_envOptions, WriterOptions());
                rocksdb_sstfilewriter_t* writerHandle = (rocksdb_sstfilewriter_t*)writer.Handle;
                writer.Open(file);
                for (int i = 0; i < _count; i++)
                {
                    ref Entry e = ref _index[i];
                    // Equal keys sort by ascending Seq; only the last of each run (the latest write) is emitted.
                    if (i + 1 < _count && comparer.IsSameKey(in e, in _index[i + 1])) continue;
                    // Safety: Reserve wrote KeyLen (+ ValLen for puts) contiguous bytes at [Offset, Offset + length)
                    // inside _slabs[Slab], so data, data + KeyLen and data + KeyLen + ValLen all stay within the pinned
                    // slab; the native put/delete therefore cannot read or write past the slab's bounds.
                    fixed (byte* slabPtr = &MemoryMarshal.GetArrayDataReference(_slabs[e.Slab]))
                    {
                        byte* data = slabPtr + e.Offset;
                        sbyte* err = null;
                        if (e.ValLen < 0) RocksDbNative.rocksdb_sstfilewriter_delete(writerHandle, (sbyte*)data, (UIntPtr)e.KeyLen, &err);
                        else RocksDbNative.rocksdb_sstfilewriter_put(writerHandle, (sbyte*)data, (UIntPtr)e.KeyLen, (sbyte*)(data + e.KeyLen), (UIntPtr)e.ValLen, &err);
                        if (err is not null) throw new RocksDbNativeException((IntPtr)err);
                    }
                }
                writer.Finish();
            }
            catch (Exception writerError)
            {
                HandleWriterError(writerError, file);
                throw;
            }

            _stagedFiles.Add(file);
            Clear();
        }

        private string NextStagingPath(string extension)
        {
            Directory.CreateDirectory(_columnDb.IngestStagingDir);
            return Path.Combine(_columnDb.IngestStagingDir, $"{_columnDb.Name}_{Interlocked.Increment(ref _sstIngestSeq)}.{extension}");
        }

        private ColumnFamilyOptions WriterOptions() =>
            _columnDb._mainDb.GetColumnFamilyOptions(_columnDb.Name)
                ?? throw new InvalidOperationException($"No column family options registered for column {_columnDb.Name} of {_columnDb._mainDb.Name}");

        private void HandleWriterError(Exception writerError, string? file)
        {
            if (writerError is RocksDbException dbEx) _columnDb._mainDb.HandleFatalDbError(dbEx, scheduleRepairMarker: false);
            if (file is null) return;
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception cleanupError)
            {
                if (_columnDb._mainDb.Logger.IsDebug) _columnDb._mainDb.Logger.Debug($"Failed to delete partial SST file '{file}' after a writer error; it will be swept on next startup. {cleanupError}");
            }
        }

        /// <summary>Writes the buffered entries to a temporary sorted run file and empties the buffer.</summary>
        /// <remarks>
        /// A run holds one record per key, the latest write, as <c>[i32 keyLen][i32 valLen, -1 = delete][key][value]</c>
        /// in key order. Runs are merged into the staged SST files by <see cref="MergeRunsToStagedFiles"/>, so the files
        /// of one batch never overlap however many times the buffer filled up.
        /// </remarks>
        private void SpillRun()
        {
            if (_count == 0) return;

            EntryComparer comparer = new(_slabs.UnsafeGetInternalArray());
            _index.AsSpan(0, _count).Sort(comparer);

            string file = NextStagingPath("run");
            _runFiles.Add(file);
            using (FileStream stream = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, RunBufferSize))
            {
                Span<byte> header = stackalloc byte[RunHeaderSize];
                for (int i = 0; i < _count; i++)
                {
                    ref Entry e = ref _index[i];
                    if (i + 1 < _count && comparer.IsSameKey(in e, in _index[i + 1])) continue;
                    BinaryPrimitives.WriteInt32LittleEndian(header, e.KeyLen);
                    BinaryPrimitives.WriteInt32LittleEndian(header[sizeof(int)..], e.ValLen);
                    stream.Write(header);
                    stream.Write(_slabs[e.Slab].AsSpan(e.Offset, e.KeyLen + Math.Max(e.ValLen, 0)));
                }
            }

            Clear();
        }

        /// <summary>Merges the spilled runs into staged SST files whose key ranges do not overlap.</summary>
        /// <remarks>
        /// K-way merge that streams one record per run. Of the records sharing a key only the one from the newest run is
        /// written, a delete included. A new output file starts once the current one holds <see cref="MaxBufferedBytes"/>,
        /// always between two keys, so the outputs are globally sorted and RocksDB can ingest them below L0.
        /// </remarks>
        private void MergeRunsToStagedFiles()
        {
            RunReader?[] readers = new RunReader?[_runFiles.Count];
            SstFileWriter? writer = null;
            string? file = null;
            try
            {
                ColumnFamilyOptions writerOptions = WriterOptions();
                PriorityQueue<RunReader, RunReader> heads = new(readers.Length, RunReaderComparer.Instance);
                for (int i = 0; i < readers.Length; i++)
                {
                    RunReader reader = readers[i] = new RunReader(_runFiles[i], i);
                    if (reader.MoveNext()) heads.Enqueue(reader, reader);
                }

                long writtenBytes = 0;
                while (heads.TryDequeue(out RunReader? newest, out _))
                {
                    if (writer is not null && writtenBytes >= MaxBufferedBytes)
                    {
                        writer.Finish();
                        writer.Dispose();
                        writer = null;
                        _stagedFiles.Add(file!);
                        file = null;
                    }

                    if (writer is null)
                    {
                        file = NextStagingPath("sst");
                        writer = new SstFileWriter(_envOptions, writerOptions);
                        writer.Open(file);
                        writtenBytes = 0;
                    }

                    WriteRecord(writer, newest.Record, newest.KeyLen, newest.ValLen);
                    writtenBytes += newest.KeyLen + Math.Max(newest.ValLen, 0);

                    while (heads.TryPeek(out RunReader? older, out _) && older.Key.SequenceEqual(newest.Key))
                    {
                        heads.Dequeue();
                        if (older.MoveNext()) heads.Enqueue(older, older);
                    }

                    if (newest.MoveNext()) heads.Enqueue(newest, newest);
                }

                if (writer is not null)
                {
                    writer.Finish();
                    writer.Dispose();
                    writer = null;
                    _stagedFiles.Add(file!);
                    file = null;
                }
            }
            catch (Exception writerError)
            {
                writer?.Dispose();
                HandleWriterError(writerError, file);
                throw;
            }
            finally
            {
                foreach (RunReader? reader in readers) reader?.Dispose();
                DeleteRunFiles();
            }
        }

        /// <summary>Appends one record to an open SST file writer.</summary>
        /// <remarks>
        /// Safety: <paramref name="record"/> holds <paramref name="keyLen"/> key bytes followed by the value bytes (none
        /// for a delete, which is <paramref name="valLen"/> below zero), so every pointer handed to the native call stays
        /// within the pinned array.
        /// </remarks>
        private static unsafe void WriteRecord(SstFileWriter writer, byte[] record, int keyLen, int valLen)
        {
            rocksdb_sstfilewriter_t* writerHandle = (rocksdb_sstfilewriter_t*)writer.Handle;
            fixed (byte* data = &MemoryMarshal.GetArrayDataReference(record))
            {
                sbyte* err = null;
                if (valLen < 0) RocksDbNative.rocksdb_sstfilewriter_delete(writerHandle, (sbyte*)data, (UIntPtr)keyLen, &err);
                else RocksDbNative.rocksdb_sstfilewriter_put(writerHandle, (sbyte*)data, (UIntPtr)keyLen, (sbyte*)(data + keyLen), (UIntPtr)valLen, &err);
                if (err is not null) throw new RocksDbNativeException((IntPtr)err);
            }
        }

        private void DeleteRunFiles()
        {
            foreach (string file in _runFiles)
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception e)
                {
                    if (_columnDb._mainDb.Logger.IsDebug) _columnDb._mainDb.Logger.Debug($"Failed to delete SST ingest run file '{file}'; it will be swept on next startup. {e}");
                }
            }
            _runFiles.Clear();
        }

        /// <summary>Streams the records of one spilled run, holding a single record in memory.</summary>
        private sealed class RunReader(string file, int index) : IDisposable
        {
            private readonly FileStream _stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, RunBufferSize, FileOptions.SequentialScan);

            public int Index => index;
            public byte[] Record { get; private set; } = new byte[256];
            public int KeyLen { get; private set; }
            public int ValLen { get; private set; }
            public ReadOnlySpan<byte> Key => Record.AsSpan(0, KeyLen);

            public bool MoveNext()
            {
                Span<byte> header = stackalloc byte[RunHeaderSize];
                int read = _stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                if (read == 0) return false;
                if (read < header.Length) throw new EndOfStreamException($"SST ingest run file '{file}' is truncated");

                KeyLen = BinaryPrimitives.ReadInt32LittleEndian(header);
                ValLen = BinaryPrimitives.ReadInt32LittleEndian(header[sizeof(int)..]);
                int length = KeyLen + Math.Max(ValLen, 0);
                if (Record.Length < length) Record = new byte[Math.Max(length, Record.Length * 2)];
                _stream.ReadExactly(Record, 0, length);
                return true;
            }

            public void Dispose() => _stream.Dispose();
        }

        /// <summary>Orders run heads by key; of equal keys the head of the newer run comes first.</summary>
        private sealed class RunReaderComparer : IComparer<RunReader>
        {
            public static readonly RunReaderComparer Instance = new();

            public int Compare(RunReader? x, RunReader? y)
            {
                int c = x!.Key.SequenceCompareTo(y!.Key);
                return c != 0 ? c : y.Index.CompareTo(x.Index);
            }
        }

        public IReadOnlyList<string> SealToStagedFiles()
        {
            if (_runFiles.Count == 0)
            {
                FlushChunk();
            }
            else
            {
                SpillRun();
                MergeRunsToStagedFiles();
            }

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
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception e)
                {
                    if (_columnDb._mainDb.Logger.IsDebug) _columnDb._mainDb.Logger.Debug($"Failed to delete staged SST file '{file}' during cleanup; it will be swept on next startup. {e}");
                }
            }
            _stagedFiles.Clear();
            DeleteRunFiles();
        }

        public void Dispose()
        {
            DeleteRunFiles();
            _runFiles.Dispose();
            foreach (byte[] slab in _slabs)
            {
                if (slab.Length == SlabSize) _slabPool.Return(slab);
            }
            _slabs.Dispose();
            _entryPool.Return(_index);
            _index = [];
            _stagedFiles.Dispose();
        }
    }

    public void Remove(ReadOnlySpan<byte> key) => Set(key, null);

    public void RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
        _mainDb.RemoveRange(firstKeyInclusive, lastKeyExclusive, _columnFamily);

    public void ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
        _mainDb.ReclaimRange(firstKeyInclusive, lastKeyExclusive, _columnFamily);

    public void Flush(bool onlyWal) => _mainDb.FlushWithColumnFamily(_columnFamily);

    public void Compact() => _mainDb.CompactOpenRange(_columnFamily, forceBottommost: false);

    /// <inheritdoc/>
    public bool CompactIfDeadWeightExceeds(double deadRatio)
    {
        if (!DbOnTheRocks.ExceedsDeadWeight(
                _rocksDb.GetProperty("rocksdb.aggregated-table-properties", _columnFamily),
                _rocksDb.GetProperty("rocksdb.total-sst-files-size", _columnFamily),
                deadRatio))
        {
            return false;
        }

        _mainDb.LogColumnDeadWeightCompaction(Name);
        _mainDb.CompactOpenRange(_columnFamily, forceBottommost: true);
        return true;
    }

    /// <inheritdoc/>
    public void InterruptCompactions() => _mainDb.InterruptCompactions();

    /// <summary>
    /// Clearing a single column family is not supported; it shares the underlying database with the other columns.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown; clearing a single column family is not supported.</exception>
    public void Clear() => throw new NotSupportedException();

    public IDbMeta.DbMetric GatherMetric() => _mainDb.GatherMetric();

    public void SetWriteBuffer(long sizeBytes)
    {
        KeyValuePair<string, string>[] options =
        [
            new("write_buffer_size", sizeBytes.ToString()),
            new("max_bytes_for_level_base", (sizeBytes * 4).ToString()),
        ];
        _rocksDb.SetOptions(_columnFamily, options);
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

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey, ReadFlags flags = ReadFlags.None) =>
        _mainDb.GetViewBetween(firstKey, lastKey, _columnFamily, flags);

    public bool TryGetCeiling(
        scoped ReadOnlySpan<byte> lowerBoundIncl, scoped ReadOnlySpan<byte> upperBoundExcl,
        Span<byte> keyBuffer, out int keyLength, Span<byte> valueBuffer, out int valueLength
    )
    {
        _mainDb.ThrowIfDisposing();

        return DbOnTheRocks.TryGetCeilingWithIterator(
            lowerBoundIncl, upperBoundExcl, _seekIteratorManager.Value,
            keyBuffer, out keyLength, valueBuffer, out valueLength
        );
    }

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
