// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// <see cref="ITrieNodeLog"/> backed by generation files in <c>basePath</c>. See the type's remarks for the
/// consistency protocol shared with <see cref="TrieNodeLogView"/> and <see cref="TrieNodeLogWriteBatch"/>.
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
public sealed class TrieNodeLog : ITrieNodeLog, IAsyncDisposable
{
    internal static readonly byte[] VersionKey = Keccak.Compute("TrieNodeLogVersion").BytesToArray();
    internal static readonly byte[] FlushedGenerationKey = Keccak.Compute("TrieNodeLogFlushedGeneration").BytesToArray();

    private const string FilePrefix = "gen-";
    private const string FileExtension = ".log";
    private const int ScanBufferSize = 4 * 1024 * 1024;

    private readonly string _basePath;
    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly ILogger _logger;
    private readonly TrieNodeLogScope _scope;
    private readonly long _generationBytes;

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

    public TrieNodeLog(string basePath, IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
    {
        _basePath = basePath;
        _db = db;
        _logger = logManager.GetClassLogger<TrieNodeLog>();
        _scope = config.TrieNodeLogScope;
        _generationBytes = config.TrieNodeLogGenerationBytes;

        Directory.CreateDirectory(basePath);
        foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
        {
            if (Covers(column)) db.GetColumnDb(column).SetWriteBuffer(WriteBufferAdjuster.MaxWriteBufferSize(column));
        }

        Recover();
        _flushWorker = Task.Run(FlushWorker);
    }

    internal bool Covers(FlatDbColumns column) => column switch
    {
        FlatDbColumns.StateTopNodes => _scope >= TrieNodeLogScope.StateTop,
        FlatDbColumns.StateNodes => _scope >= TrieNodeLogScope.State,
        FlatDbColumns.StorageNodes or FlatDbColumns.FallbackNodes => _scope >= TrieNodeLogScope.All,
        _ => false,
    };

    internal long GenerationBytes => _generationBytes;

    public ITrieNodeLog.IView PinLiveGenerations()
    {
        using Lock.Scope _ = _lock.EnterScope();
        return new TrieNodeLogView(this, PinAllNoLock());
    }

    public ITrieNodeLog.IWriteBatch StartWriteBatch(bool bypass)
    {
        if (bypass)
        {
            Drain();
            return NullTrieNodeLog.Instance;
        }

        if (Interlocked.CompareExchange(ref _openBatch, 1, 0) != 0)
            throw new InvalidOperationException("A trie node log write batch is already open");

        using Lock.Scope _ = _lock.EnterScope();
        return new TrieNodeLogWriteBatch(this, ++_version, PinAllNoLock());
    }

    public void Drain()
    {
        ThrowIfBatchOpen();
        SealActive();
        FlushSealedGenerations();
    }

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
        Metrics.TrieNodeLogGenerationCount = 0;
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

    private void ThrowIfBatchOpen()
    {
        if (Volatile.Read(ref _openBatch) != 0) throw new InvalidOperationException("A trie node log write batch is open");
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
        Metrics.TrieNodeLogGenerationCount = _generations.Count;
        return generation;
    }

    internal bool IsFull(TrieNodeLogGeneration generation, int pendingInserts, long pendingBytes) =>
        generation.WriteFrontier + pendingBytes >= _generationBytes || generation.Occupied + pendingInserts >= generation.Capacity / 4 * 3;

    /// <summary>
    /// Called by a write batch after the RocksDB batch that carries its version has been committed: seals every
    /// generation the batch wrote to except a still-open active one, so a sealed generation only ever holds
    /// records whose version RocksDB has confirmed.
    /// </summary>
    internal void OnBatchCommitted(List<TrieNodeLogGeneration> touched)
    {
        using (_lock.EnterScope())
        {
            foreach (TrieNodeLogGeneration generation in touched)
            {
                if (generation != _active || IsFull(generation, 0, 0))
                {
                    generation.IsSealed = true;
                    if (generation == _active) _active = null;
                }
            }
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
                FlushSealedGenerations();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_logger.IsError) _logger.Error("Trie node log flush failed", e);
            }
        }
    }

    private void FlushSealedGenerations()
    {
        using SemaphoreSlimExtensions.Scope _ = _flushLock.EnterScope();
        while (true)
        {
            TrieNodeLogGeneration? generation = null;
            using (_lock.EnterScope())
            {
                foreach (TrieNodeLogGeneration candidate in _generations)
                {
                    if (candidate.IsSealed && !candidate.IsFlushed)
                    {
                        generation = candidate;
                        break;
                    }
                }
            }

            if (generation is null) return;
            if (!generation.TryAcquire()) throw new InvalidOperationException($"Trie node log generation {generation.Number} was released before being merged");
            try
            {
                FlushGeneration(generation);
            }
            finally
            {
                generation.Dispose();
            }
        }
    }

    private void FlushGeneration(TrieNodeLogGeneration generation)
    {
        long sw = System.Diagnostics.Stopwatch.GetTimestamp();
        long written = 0;
        int records = 0;
        if (!BasePersistence.ReadWipedForSync(_db.GetColumnDb(FlatDbColumns.Metadata)))
        {
            using (IColumnsWriteBatch<FlatDbColumns> batch = _db.StartWriteBatch())
            {
                Scanner scanner = new(generation, generation.Frontier);
                try
                {
                    while (scanner.MoveNext())
                    {
                        TrieNodeLogRecord header = scanner.Header;
                        if (header.IsCommit || !generation.IsLatest(TrieNodeLogRecord.Hash(header.Column, scanner.Key), scanner.Offset)) continue;

                        Core.IWriteBatch column = batch.GetColumnBatch((FlatDbColumns)header.Column);
                        if (header.Type == TrieNodeLogRecord.Delete) column.Remove(scanner.Key);
                        else column.PutSpan(scanner.Key, scanner.Value);
                        written += header.KeyLength + header.ValueLength;
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
            Metrics.TrieNodeLogFlushedBytes += written;
        }

        using (_lock.EnterScope())
        {
            generation.IsFlushed = true;
            _generations.Remove(generation);
            Metrics.TrieNodeLogGenerationCount = _generations.Count;
        }
        generation.Dispose(); // the log's own lease

        if (_logger.IsDebug) _logger.Debug($"Merged trie node log generation {generation.Number}: {records} records, {written / (double)MemorySizes.MiB:F1} MiB of {generation.Frontier / (double)MemorySizes.MiB:F1} MiB in {System.Diagnostics.Stopwatch.GetElapsedTime(sw).TotalMilliseconds:F0} ms");
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

        Metrics.TrieNodeLogGenerationCount = _generations.Count;
        if (_generations.Count > 0)
        {
            if (_logger.IsInfo) _logger.Info($"Recovered {_generations.Count} trie node log generation(s) up to version {committedVersion}");
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
