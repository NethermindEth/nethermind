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
/// view serves only records with <c>version &lt;= V</c>, following a record's <c>prev</c> link within its
/// generation and the older generations' indexes beyond it. A metadata marker <c>N</c> records the newest generation whose latest record per
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
    private readonly int _maxBacklog;
    private readonly SemaphoreSlim _mergeLimiter;
    private readonly ManualResetEventSlim _merged = new();
    private readonly TrieNodeLogLabel _label;

    private readonly Lock _lock = new();
    private readonly List<TrieNodeLogGeneration> _generations = []; // oldest first; every generation still in memory
    private TrieNodeLogGeneration? _active;
    private ulong _nextGeneration;
    private ulong _version;
    private int _openBatch;
    private int _poisoned;
    private int _disposed;

    // One batch is open per shard at a time; its pending-key map is kept across batches so its entry arrays
    // (which grow past the LOH threshold) are not reallocated every batch.
    private Dictionary<ulong, TrieNodeLogWriteBatch.Pending>? _pendingPool;

    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly SemaphoreSlim _flushSignal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _flushWorker;

    // The gauges are shared by every shard, so each shard contributes the change since its last report.
    private int _reportedActive;
    private int _reportedSealed;
    private int _reportedListCount;
    private long _reportedBytes;

    /// <param name="mergeLimiter">Caps concurrent merges across every shard of the log.</param>
    /// <param name="backlogMargin">Sealed generations allowed beyond <paramref name="mergeLag"/> before a roll waits for a merge.</param>
    public TrieNodeLogShard(string name, FlatDbColumns column, string basePath, IColumnsDb<FlatDbColumns> db, long generationBytes, int mergeLag, int backlogMargin, SemaphoreSlim mergeLimiter, ILogManager logManager)
    {
        Name = name;
        Column = column;
        VersionKey = Keccak.Compute($"TrieNodeLogVersion:{name}").BytesToArray();
        FlushedGenerationKey = Keccak.Compute($"TrieNodeLogFlushedGeneration:{name}").BytesToArray();
        _basePath = basePath;
        _db = db;
        _logger = logManager.GetClassLogger<TrieNodeLogShard>();
        _generationBytes = generationBytes;
        _mergeLag = mergeLag;
        _maxBacklog = mergeLag + backlogMargin;
        _mergeLimiter = mergeLimiter;
        _label = new TrieNodeLogLabel(name);

        Directory.CreateDirectory(basePath);
        Recover();
        _flushWorker = Task.Run(FlushWorker);
    }

    public string Name { get; }

    /// <summary>The one trie column this shard holds records of.</summary>
    public FlatDbColumns Column { get; }

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
        EnterExclusive();
        try
        {
            Dictionary<ulong, TrieNodeLogWriteBatch.Pending> pending = Interlocked.Exchange(ref _pendingPool, null) ?? [];
            using Lock.Scope _ = _lock.EnterScope();
            return new TrieNodeLogWriteBatch(this, ++_version, new ArrayPoolList<TrieNodeLogGeneration>(2), pending);
        }
        catch
        {
            ExitExclusive();
            throw;
        }
    }

    /// <summary>
    /// Takes the batch gate: held by an open log-backed batch, and by <see cref="DrainExclusive"/> and
    /// <see cref="ClearExclusive"/> so neither runs under an open batch nor has a batch open under it.
    /// </summary>
    internal void EnterExclusive()
    {
        if (Volatile.Read(ref _poisoned) != 0)
            throw new InvalidOperationException($"Trie node log shard {Name} holds records RocksDB never confirmed; restart the node to recover");
        if (Interlocked.CompareExchange(ref _openBatch, 1, 0) != 0)
            throw new InvalidOperationException($"A trie node log write batch is open on shard {Name}");
    }

    internal void ExitExclusive() => Volatile.Write(ref _openBatch, 0);

    /// <summary>Refuses every further batch, drain and clear until a restart; see <see cref="ITrieNodeLog.IWriteBatch.Confirm"/>.</summary>
    internal void Poison()
    {
        Volatile.Write(ref _poisoned, 1);
        if (_logger.IsError) _logger.Error($"Trie node log shard {Name} holds records RocksDB never confirmed; it takes no more writes until the node is restarted");
    }

    internal void ReturnPending(Dictionary<ulong, TrieNodeLogWriteBatch.Pending> pending)
    {
        pending.Clear();
        Volatile.Write(ref _pendingPool, pending);
    }

    /// <summary>Merges every generation into RocksDB synchronously.</summary>
    public void Drain()
    {
        EnterExclusive();
        try
        {
            DrainExclusive();
        }
        finally
        {
            ExitExclusive();
        }
    }

    /// <summary><see cref="Drain"/> for a caller holding the gate through <see cref="EnterExclusive"/>.</summary>
    internal void DrainExclusive()
    {
        SealActive();
        FlushSealedGenerations(mergeLag: 0);
    }

    /// <summary>Discards every generation without merging it.</summary>
    public void Clear()
    {
        EnterExclusive();
        try
        {
            ClearExclusive();
        }
        finally
        {
            ExitExclusive();
        }
    }

    /// <summary><see cref="Clear"/> for a caller holding the gate through <see cref="EnterExclusive"/>.</summary>
    internal void ClearExclusive()
    {
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
        _merged.Dispose();
    }

    private ArrayPoolList<TrieNodeLogGeneration> PinAllNoLock()
    {
        ArrayPoolList<TrieNodeLogGeneration> pinned = new(_generations.Count);
        foreach (TrieNodeLogGeneration generation in _generations)
        {
            if (generation.TryAcquire()) pinned.Add(generation);
        }
        return pinned;
    }

    /// <summary>Pins generations added since <paramref name="alreadyPinned"/> was taken (a roll that raced the RocksDB snapshot).</summary>
    internal void PinNewer(ArrayPoolList<TrieNodeLogGeneration> alreadyPinned)
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
        WaitForBacklog();
        using Lock.Scope _ = _lock.EnterScope();
        TrieNodeLogGeneration generation = new(_nextGeneration, System.IO.Path.Combine(_basePath, $"{FilePrefix}{_nextGeneration:D8}{FileExtension}"), TrieNodeLogGeneration.CapacityFor(_generationBytes));
        generation.WriteFileHeader();
        _nextGeneration++;
        _generations.Add(generation);
        _active = generation;
        RefreshGaugesNoLock();
        return generation;
    }

    /// <summary>
    /// Blocks the appending worker while the shard holds more sealed, unmerged generations than the merge lag plus
    /// the configured margin, so a persistence that outruns the merges backs up instead of growing the log unbounded.
    /// </summary>
    private void WaitForBacklog()
    {
        long sw = 0;
        while (true)
        {
            _merged.Reset();
            int backlog = 0;
            using (_lock.EnterScope())
            {
                foreach (TrieNodeLogGeneration generation in _generations)
                {
                    if (generation.IsSealed && !generation.IsFlushed) backlog++;
                }
            }
            if (backlog < _maxBacklog) break;

            if (sw == 0)
            {
                sw = Stopwatch.GetTimestamp();
                if (_logger.IsWarn) _logger.Warn($"Trie node log {Name} has {backlog} unmerged generations (limit {_maxBacklog}); persistence is waiting for a merge");
            }
            _flushSignal.Release(); // a merge that failed is retried rather than leaving persistence stuck
            if (!_merged.Wait(TimeSpan.FromSeconds(10)) && _logger.IsWarn)
                _logger.Warn($"Trie node log {Name} still has {backlog} unmerged generations after {Stopwatch.GetElapsedTime(sw).TotalSeconds:F0} s; persistence is still waiting");
        }

        if (sw != 0)
        {
            Metrics.TrieNodeLogBackpressureTime.Observe(Stopwatch.GetTimestamp() - sw);
            if (_logger.IsInfo) _logger.Info($"Trie node log {Name} merge caught up; persistence waited {Stopwatch.GetElapsedTime(sw).TotalMilliseconds:F0} ms");
        }
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
                using SemaphoreSlimExtensions.Scope __ = _mergeLimiter.EnterScope();
                FlushGeneration(generation, newer);
            }
            finally
            {
                generation.Dispose();
                _merged.Set();
            }
        }
    }

    private void FlushGeneration(TrieNodeLogGeneration generation, List<TrieNodeLogGeneration> newer)
    {
        long sw = Stopwatch.GetTimestamp();
        long written = 0;
        int records = 0;
        IReadOnlyKeyValueStore metadata = _db.GetColumnDb(FlatDbColumns.Metadata);
        if (!BasePersistence.ReadWipedForSync(metadata))
        {
            // A key with a newer record in a later generation is skipped, but only if that record's batch has reached
            // RocksDB: the index is published before the RocksDB commit, and a crash in between drops the record.
            ulong committedVersion = ReadUInt64(metadata.Get(VersionKey));
            Span<byte> probeBuffer = stackalloc byte[TrieNodeLogRecord.HeaderLength + TrieNodeLogRecord.MaxKeyLength];

            using (IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch())
            {
                Core.IWriteBatch column = batch.GetColumnBatch(Column);
                Scanner scanner = new(generation, generation.Frontier);
                try
                {
                    while (scanner.MoveNext())
                    {
                        TrieNodeLogRecord header = scanner.Header;
                        if (header.IsCommit) continue;
                        ulong hash = TrieNodeLogRecord.Hash(scanner.Key);
                        if (!generation.IsLatest(hash, scanner.Offset)) continue;
                        if (HasCommittedNewerRecord(newer, hash, scanner.Key, probeBuffer, committedVersion)) continue;

                        if (header.Type == TrieNodeLogRecord.Delete) column.Set(scanner.Key, null, WriteFlags.DisableWAL);
                        else column.PutSpan(scanner.Key, scanner.Value, WriteFlags.DisableWAL);
                        written += header.KeyLength + header.ValueLength;
                        records++;
                    }
                }
                finally
                {
                    scanner.Dispose();
                }
            }

            // The log file is the WAL of this merge: the data goes in without one, the column family is flushed
            // (throwing, so a failure never reaches the marker), and only then is the marker written and flushed.
            // Marker durable therefore implies data durable; a crash before that replays the file.
            _db.GetColumnDb(Column).FlushOrThrow();

            using (IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch())
            {
                Span<byte> marker = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(marker, generation.Number);
                batch.GetColumnBatch(FlatDbColumns.Metadata).PutSpan(FlushedGenerationKey, marker, WriteFlags.DisableWAL);
            }
            _db.GetColumnDb(FlatDbColumns.Metadata).FlushOrThrow();
            if (written != 0) Metrics.TrieNodeLogFlushedBytes.AddBy(TrieNodeLogLabel.Column((byte)Column), written);
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

    private static bool HasCommittedNewerRecord(List<TrieNodeLogGeneration> newer, ulong hash, ReadOnlySpan<byte> key, Span<byte> probeBuffer, ulong committedVersion)
    {
        foreach (TrieNodeLogGeneration generation in newer)
        {
            if (generation.TryLocate(hash, key, probeBuffer, out TrieNodeLogRecord header, out _, out _, out _))
                return header.Version <= committedVersion;
        }
        return false;
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
            // A file without a complete header is one whose creation was cut short before anything in it was
            // confirmed.
            if (wiped || number <= flushedGeneration || !TrieNodeLogGeneration.HasFileHeader(path))
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
        long frontier = TrieNodeLogGeneration.FileHeaderLength;
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
                ulong hash = TrieNodeLogRecord.Hash(scanner.Key);
                generation.TryLocate(hash, scanner.Key, buffer, out _, out int index, out _, out _);
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
        private long _next = TrieNodeLogGeneration.FileHeaderLength; // file offset of the next record
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
