// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// One shard of the <see cref="TrieNodeLog"/>: a sequence of generation files in <c>basePath</c> with their own
/// index, version counter and merge worker. See the type's remarks for the consistency protocol shared with
/// <see cref="TrieNodeLogView"/> and <see cref="TrieNodeLogWriteBatch"/>.
/// </summary>
/// <remarks>
/// <para>Every log-backed batch gets a version <c>V</c> and stores it in the flat DB metadata column inside the
/// same RocksDB batch as the state pointer, so a RocksDB snapshot pins the log version a reader must see; the
/// view serves only records with <c>version &lt;= V</c> and follows each record's <c>prev</c> link back to an
/// older version when needed. A metadata marker <c>N</c> records the newest generation whose latest record per
/// key is in RocksDB; a view does not pin generations at or below the <c>N</c> of its snapshot.</para>
/// <para>Recovery keeps, per surviving file, the prefix up to the last commit record whose version the metadata
/// column confirms; records of a batch whose RocksDB write did not happen are discarded, files at or below
/// <c>N</c> are deleted.</para>
/// </remarks>
internal sealed class TrieNodeLogShard : IAsyncDisposable
{
    private const string FilePrefix = "gen-";
    private const string FileExtension = ".log";
    private const int ScanBufferSize = 4 * 1024 * 1024;

    private static long _listCountTotal;

    private readonly string _basePath;
    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly ILogger _logger;
    private readonly long _generationBytes;
    private readonly int _mergeLag;
    private readonly TrieNodeLogLabel _label;

    private readonly Lock _lock = new();
    private readonly List<TrieNodeLogGeneration> _generations = []; // oldest first; every generation still in memory
    private TrieNodeLogGeneration? _active;
    private ulong _nextGeneration;
    private ulong _version;
    private int _openBatch;

    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _flushSignal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _flushWorker;

    // The gauges are shared by every shard, so each shard contributes the change since its last report.
    private int _reportedActive;
    private int _reportedSealed;
    private int _reportedListCount;
    private long _reportedBytes;

    public TrieNodeLogShard(string name, string basePath, IColumnsDb<FlatDbColumns> db, long generationBytes, int mergeLag, ILogManager logManager)
    {
        Name = name;
        VersionKey = Keccak.Compute($"TrieNodeLogVersion:{name}").BytesToArray();
        FlushedGenerationKey = Keccak.Compute($"TrieNodeLogFlushedGeneration:{name}").BytesToArray();
        _basePath = basePath;
        _db = db;
        _logger = logManager.GetClassLogger<TrieNodeLogShard>();
        _generationBytes = generationBytes;
        _mergeLag = mergeLag;
        _label = new TrieNodeLogLabel(name);

        Directory.CreateDirectory(basePath);
        Recover();
        _flushWorker = Task.Run(FlushWorker);
    }

    public string Name { get; }

    internal byte[] VersionKey { get; }

    internal byte[] FlushedGenerationKey { get; }

    internal TrieNodeLogLabel Label => _label;

    public TrieNodeLogView PinLiveGenerations()
    {
        using Lock.Scope _ = _lock.EnterScope();
        return new TrieNodeLogView(this, PinAllNoLock());
    }

    public TrieNodeLogWriteBatch StartWriteBatch()
    {
        if (Interlocked.CompareExchange(ref _openBatch, 1, 0) != 0)
            throw new InvalidOperationException($"A trie node log write batch is already open on shard {Name}");

        using Lock.Scope _ = _lock.EnterScope();
        return new TrieNodeLogWriteBatch(this, ++_version, PinAllNoLock());
    }

    /// <summary>Merges every generation into RocksDB synchronously.</summary>
    public void Drain()
    {
        ThrowIfBatchOpen();
        SealActive();
        FlushSealedGenerations(mergeLag: 0);
    }

    /// <summary>Discards every generation without merging it.</summary>
    public void Clear()
    {
        ThrowIfBatchOpen();
        using SemaphoreSlimExtensions.Scope _ = _flushLock.EnterScope();
        using Lock.Scope __ = _lock.EnterScope();
        foreach (TrieNodeLogGeneration generation in _generations)
        {
            generation.IsFlushed = true;
            generation.Dispose();
        }
        _generations.Clear();
        _active = null;
        RefreshGaugesNoLock();
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try { await _flushWorker; }
        catch (OperationCanceledException) { }
        using (_lock.EnterScope())
        {
            foreach (TrieNodeLogGeneration generation in _generations)
            {
                if (!generation.IsFlushed) generation.PreserveOnDispose();
                generation.Dispose();
            }
            _generations.Clear();
            _active = null;
        }
        _cancellation.Dispose();
    }

    internal void ThrowIfBatchOpen()
    {
        if (Volatile.Read(ref _openBatch) != 0) throw new InvalidOperationException($"A trie node log write batch is open on shard {Name}");
    }

    private List<TrieNodeLogGeneration> PinAllNoLock()
    {
        List<TrieNodeLogGeneration> pinned = new(_generations.Count);
        foreach (TrieNodeLogGeneration generation in _generations)
        {
            if (generation.TryAcquire()) pinned.Add(generation);
        }
        return pinned;
    }

    /// <summary>Pins generations added since <paramref name="alreadyPinned"/> was taken (a roll that raced the RocksDB snapshot).</summary>
    internal void PinNewer(List<TrieNodeLogGeneration> alreadyPinned)
    {
        ulong newest = alreadyPinned.Count == 0 ? 0 : alreadyPinned[^1].Number;
        using Lock.Scope _ = _lock.EnterScope();
        foreach (TrieNodeLogGeneration generation in _generations)
        {
            if (generation.Number > newest && generation.TryAcquire()) alreadyPinned.Add(generation);
        }
    }

    internal TrieNodeLogGeneration? Active => _active;

    /// <summary>
    /// Starts a new active generation. The previous one is not sealed here: the open batch may still hold
    /// unpublished records for it, so it is sealed by <see cref="OnBatchCommitted"/>.
    /// </summary>
    internal TrieNodeLogGeneration Roll()
    {
        using Lock.Scope _ = _lock.EnterScope();
        TrieNodeLogGeneration generation = new(_nextGeneration, System.IO.Path.Combine(_basePath, $"{FilePrefix}{_nextGeneration:D8}{FileExtension}"), TrieNodeLogGeneration.CapacityFor(_generationBytes));
        _nextGeneration++;
        _generations.Add(generation);
        _active = generation;
        RefreshGaugesNoLock();
        return generation;
    }

    internal bool IsFull(TrieNodeLogGeneration generation, int pendingInserts, long pendingBytes) =>
        generation.WriteFrontier + pendingBytes >= _generationBytes || generation.Occupied + pendingInserts >= generation.Capacity / 4 * 3;

    /// <summary>
    /// Called by a write batch after the RocksDB batch that carries its version has been committed: seals every
    /// generation except a still-open active one, so a sealed generation only ever holds records whose version
    /// RocksDB has confirmed, and no generation older than a merged one can remain unsealed.
    /// </summary>
    internal void OnBatchCommitted()
    {
        using (_lock.EnterScope())
        {
            foreach (TrieNodeLogGeneration generation in _generations)
            {
                if (generation != _active) generation.IsSealed = true;
            }

            if (_active is not null && IsFull(_active, 0, 0))
            {
                _active.IsSealed = true;
                _active = null;
            }
            RefreshGaugesNoLock();
        }
        Volatile.Write(ref _openBatch, 0);
        _flushSignal.Release();
    }

    internal void OnBatchAborted() => Volatile.Write(ref _openBatch, 0);

    private void SealActive()
    {
        using Lock.Scope _ = _lock.EnterScope();
        if (_active is null) return;
        _active.IsSealed = true;
        _active = null;
    }

    private async Task FlushWorker()
    {
        while (true)
        {
            await _flushSignal.WaitAsync(_cancellation.Token);
            try
            {
                FlushSealedGenerations(_mergeLag);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_logger.IsError) _logger.Error($"Trie node log {Name} merge failed", e);
            }
        }
    }

    /// <summary>
    /// Merges sealed generations oldest-first, each only once <paramref name="mergeLag"/> newer sealed generations
    /// exist, so keys rewritten within that window are merged from the newest generation only.
    /// </summary>
    private void FlushSealedGenerations(int mergeLag)
    {
        using SemaphoreSlimExtensions.Scope _ = _flushLock.EnterScope();
        while (true)
        {
            TrieNodeLogGeneration? generation = null;
            List<TrieNodeLogGeneration> newer = []; // every later generation, the active one included
            int newerSealed = 0;
            using (_lock.EnterScope())
            {
                foreach (TrieNodeLogGeneration candidate in _generations)
                {
                    if (generation is null)
                    {
                        if (candidate.IsSealed && !candidate.IsFlushed) generation = candidate;
                        continue;
                    }

                    newer.Add(candidate);
                    if (candidate.IsSealed) newerSealed++;
                }
            }

            if (generation is null || newerSealed < mergeLag) return;
            if (!generation.TryAcquire()) throw new InvalidOperationException($"Trie node log generation {generation.Number} was released before being merged");
            try
            {
                FlushGeneration(generation, newer);
            }
            finally
            {
                generation.Dispose();
            }
        }
    }

    private void FlushGeneration(TrieNodeLogGeneration generation, List<TrieNodeLogGeneration> newer)
    {
        long sw = Stopwatch.GetTimestamp();
        long written = 0;
        int records = 0;
        long[] writtenByColumn = new long[WriteBufferAdjuster.ColumnCount];
        long[] skippedByColumn = new long[WriteBufferAdjuster.ColumnCount];
        IReadOnlyKeyValueStore metadata = _db.GetColumnDb(FlatDbColumns.Metadata);
        if (!BasePersistence.ReadWipedForSync(metadata))
        {
            // A key with a newer record in a later generation is skipped, but only if that record's batch has reached
            // RocksDB: the index is published before the RocksDB commit, and a crash in between drops the record.
            ulong committedVersion = ReadUInt64(metadata.Get(VersionKey));
            Span<byte> probeBuffer = stackalloc byte[TrieNodeLogRecord.HeaderLength + TrieNodeLogRecord.MaxKeyLength];

            using (IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch())
            {
                Scanner scanner = new(generation, generation.Frontier);
                try
                {
                    while (scanner.MoveNext())
                    {
                        TrieNodeLogRecord header = scanner.Header;
                        if (header.IsCommit) continue;
                        ulong hash = TrieNodeLogRecord.Hash(header.Column, scanner.Key);
                        if (!generation.IsLatest(hash, scanner.Offset)) continue;
                        if (HasCommittedNewerRecord(newer, hash, header.Column, scanner.Key, probeBuffer, committedVersion))
                        {
                            skippedByColumn[header.Column] += header.KeyLength + header.ValueLength;
                            continue;
                        }

                        Core.IWriteBatch column = batch.GetColumnBatch((FlatDbColumns)header.Column);
                        if (header.Type == TrieNodeLogRecord.Delete) column.Remove(scanner.Key);
                        else column.PutSpan(scanner.Key, scanner.Value);
                        written += header.KeyLength + header.ValueLength;
                        writtenByColumn[header.Column] += header.KeyLength + header.ValueLength;
                        records++;
                    }
                }
                finally
                {
                    scanner.Dispose();
                }

                Span<byte> marker = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(marker, generation.Number);
                batch.GetColumnBatch(FlatDbColumns.Metadata).PutSpan(FlushedGenerationKey, marker);
            }

            _db.SyncWal();
            for (int column = 0; column < WriteBufferAdjuster.ColumnCount; column++)
            {
                if (writtenByColumn[column] != 0) Metrics.TrieNodeLogFlushedBytes.AddBy(TrieNodeLogLabel.Column((byte)column), writtenByColumn[column]);
                if (skippedByColumn[column] != 0) Metrics.TrieNodeLogSkippedBytes.AddBy(TrieNodeLogLabel.Column((byte)column), skippedByColumn[column]);
            }
            Metrics.TrieNodeLogFlushedGeneration[_label] = (long)generation.Number;
        }

        using (_lock.EnterScope())
        {
            generation.IsFlushed = true;
            _generations.Remove(generation);
            RefreshGaugesNoLock();
        }
        generation.Dispose(); // the log's own lease
        Metrics.TrieNodeLogMergeTime.Observe(Stopwatch.GetTimestamp() - sw);

        if (_logger.IsDebug) _logger.Debug($"Merged trie node log {Name} generation {generation.Number}: {records} records, {written / (double)MemorySizes.MiB:F1} MiB of {generation.Frontier / (double)MemorySizes.MiB:F1} MiB in {Stopwatch.GetElapsedTime(sw).TotalMilliseconds:F0} ms");
    }

    private static bool HasCommittedNewerRecord(List<TrieNodeLogGeneration> newer, ulong hash, byte column, ReadOnlySpan<byte> key, Span<byte> probeBuffer, ulong committedVersion)
    {
        foreach (TrieNodeLogGeneration generation in newer)
        {
            if (generation.TryLocate(hash, column, key, probeBuffer, out TrieNodeLogRecord header, out _, out _, out _))
                return header.Version <= committedVersion;
        }
        return false;
    }

    internal void RefreshGauges()
    {
        using Lock.Scope _ = _lock.EnterScope();
        RefreshGaugesNoLock();
    }

    private void RefreshGaugesNoLock()
    {
        long bytes = 0;
        foreach (TrieNodeLogGeneration generation in _generations) bytes += generation.WriteFrontier;
        int active = _active is null ? 0 : 1;
        int sealedCount = _generations.Count - active;

        Metrics.TrieNodeLogGenerationCount.AddBy(TrieNodeLogLabel.Active, active - _reportedActive);
        Metrics.TrieNodeLogGenerationCount.AddBy(TrieNodeLogLabel.Sealed, sealedCount - _reportedSealed);
        Metrics.AddTrieNodeLogBytes(bytes - _reportedBytes);
        long listCountTotal = Interlocked.Add(ref _listCountTotal, _generations.Count - _reportedListCount);
        _reportedActive = active;
        _reportedSealed = sealedCount;
        _reportedBytes = bytes;
        _reportedListCount = _generations.Count;

        Metrics.TrieNodeLogGenerationCount[TrieNodeLogLabel.MergedPinned] = TrieNodeLogGeneration.AliveCount - listCountTotal;
        Metrics.TrieNodeLogIndexBytes = TrieNodeLogGeneration.AliveIndexBytes;
        Metrics.TrieNodeLogActiveOccupancyPercent[_label] = _active is null ? 0 : _active.Occupied * 100L / _active.Capacity;
    }

    private void Recover()
    {
        IReadOnlyKeyValueStore metadata = _db.GetColumnDb(FlatDbColumns.Metadata);
        ulong committedVersion = ReadUInt64(metadata.Get(VersionKey));
        ulong flushedGeneration = ReadUInt64(metadata.Get(FlushedGenerationKey));
        bool wiped = BasePersistence.ReadWipedForSync(metadata);
        _version = committedVersion;
        _nextGeneration = flushedGeneration + 1;

        List<(ulong Number, string Path)> files = [];
        foreach (string path in Directory.GetFiles(_basePath, $"{FilePrefix}*{FileExtension}"))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (ulong.TryParse(name.AsSpan(FilePrefix.Length), out ulong number)) files.Add((number, path));
        }
        files.Sort();

        foreach ((ulong number, string path) in files)
        {
            if (wiped || number <= flushedGeneration)
            {
                File.Delete(path);
                continue;
            }

            TrieNodeLogGeneration generation = RecoverGeneration(number, path, committedVersion);
            generation.IsSealed = true;
            _generations.Add(generation);
            _nextGeneration = Math.Max(_nextGeneration, number + 1);
        }

        Metrics.TrieNodeLogVersion[_label] = (long)committedVersion;
        Metrics.TrieNodeLogFlushedGeneration[_label] = (long)flushedGeneration;
        RefreshGaugesNoLock();
        if (_generations.Count > 0)
        {
            if (_logger.IsInfo) _logger.Info($"Recovered {_generations.Count} trie node log generation(s) of shard {Name} up to version {committedVersion}");
            _flushSignal.Release();
        }
    }

    private TrieNodeLogGeneration RecoverGeneration(ulong number, string path, ulong committedVersion)
    {
        long fileLength = new FileInfo(path).Length;
        TrieNodeLogGeneration generation = new(number, path, Math.Max(TrieNodeLogGeneration.CapacityFor(_generationBytes), TrieNodeLogGeneration.CapacityFor(fileLength)));

        // First pass: the frontier is the end of the last commit record the metadata column confirms.
        long frontier = 0;
        using (Scanner scanner = new(generation, fileLength))
        {
            while (scanner.MoveNext() && scanner.Header.Version <= committedVersion)
            {
                if (scanner.Header.IsCommit) frontier = scanner.Offset + scanner.Header.Length;
            }
        }

        generation.Truncate(frontier);

        // Second pass: index the surviving records; a later record of the same key replaces the earlier slot.
        Span<byte> buffer = stackalloc byte[TrieNodeLogRecord.HeaderLength + TrieNodeLogRecord.MaxKeyLength];
        using (Scanner scanner = new(generation, frontier))
        {
            while (scanner.MoveNext())
            {
                if (scanner.Header.IsCommit) continue;
                ulong hash = TrieNodeLogRecord.Hash(scanner.Header.Column, scanner.Key);
                generation.TryLocate(hash, scanner.Header.Column, scanner.Key, buffer, out _, out int index, out _, out _);
                generation.Publish(index, hash, scanner.Offset);
            }
        }

        return generation;
    }

    private static ulong ReadUInt64(byte[]? bytes) => bytes is { Length: 8 } ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : 0;

    /// <summary>Sequential record reader over <c>[0, end)</c> of a generation file; stops at the first implausible header.</summary>
    private sealed class Scanner(TrieNodeLogGeneration generation, long end) : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        private long _bufferOffset; // file offset of _buffer[0]
        private int _buffered;
        private long _next;
        private int _recordStart;

        public long Offset { get; private set; }
        public TrieNodeLogRecord Header { get; private set; }
        public ReadOnlySpan<byte> Key => _buffer.AsSpan(_recordStart + TrieNodeLogRecord.HeaderLength, Header.KeyLength);
        public ReadOnlySpan<byte> Value => _buffer.AsSpan(_recordStart + TrieNodeLogRecord.HeaderLength + Header.KeyLength, Header.ValueLength);

        public bool MoveNext()
        {
            if (_next + TrieNodeLogRecord.HeaderLength > end) return false;
            if (!Ensure(TrieNodeLogRecord.HeaderLength)) return false;
            TrieNodeLogRecord header = TrieNodeLogRecord.Read(_buffer.AsSpan(_recordStart));
            if (!header.IsPlausible || _next + header.Length > end || !Ensure(header.Length)) return false;
            Offset = _next;
            Header = header;
            _next += header.Length;
            return true;
        }

        /// <summary>Makes <paramref name="length"/> bytes from <see cref="_next"/> available at <see cref="_recordStart"/>.</summary>
        private bool Ensure(int length)
        {
            if (_next >= _bufferOffset && _next + length <= _bufferOffset + _buffered)
            {
                _recordStart = (int)(_next - _bufferOffset);
                return true;
            }

            if (length > _buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = ArrayPool<byte>.Shared.Rent(length);
            }

            _bufferOffset = _next;
            _buffered = generation.ReadAt(_next, _buffer.AsSpan(0, (int)Math.Min(_buffer.Length, end - _next)));
            _recordStart = 0;
            return _buffered >= length;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
