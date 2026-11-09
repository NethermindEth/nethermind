// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Paprika;
using Paprika.Store;
using Paprika.Store.PageManagers;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>
/// The Paprika file of <see cref="FlatLayout.PaprikaFlat"/>: the flat accounts and storage, the state pointer and the
/// markers confirming the trie node log, written by one writer. It is the layout's <see cref="IFlatMetadataStore"/>, as
/// its commits carry the markers.
/// </summary>
/// <remarks>
/// <para>
/// The file is opened on first use, so reading <see cref="Committed"/> while it is absent neither creates it nor needs
/// it. Opening it stamps the layout into both the RocksDB metadata column and Paprika, so another layout, an older
/// binary, a lost <c>paprika.db</c> or a lost flat DB is refused before the trie node log recovers against them.
/// </para>
/// <para>
/// Write buffers are queued and committed in groups by whichever caller holds the commit lock: a buffer of a log-backed
/// batch alone, up to 64 consecutive other buffers together. Readers never wait: each reads the
/// root it opened, whose pages are not reused until it is disposed, so a long reader only grows the file
/// (<see cref="Metrics.PaprikaFlatOldestReaderLag"/>).
/// </para>
/// </remarks>
public sealed class PaprikaFlatStore : IFlatMetadataStore, IDisposable
{
    /// <summary>The roots Paprika keeps: the latest and the one before it.</summary>
    public const byte HistoryDepth = 2;

    private const int MaxGroupSize = 64;
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReaderPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly FlatDbColumns[] TrieColumns = [FlatDbColumns.StateTopNodes, FlatDbColumns.StateNodes, FlatDbColumns.StorageNodes, FlatDbColumns.FallbackNodes];

    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly string? _filePath;
    private readonly Func<PagedDb> _openDb;
    private readonly TimeSpan _readerWait;
    private readonly ILogger _logger;
    private readonly Lock _commitLock = new();
    private readonly ConcurrentQueue<Pending> _queue = new();
    private PagedDb? _pagedDb;
    private PaprikaFlatMetadata _committed = PaprikaFlatMetadata.Empty;
    private Timer? _metricsTimer;
    private bool _disposed;

    /// <summary>Checks the RocksDB half of the flat DB for this layout; the Paprika file is opened on first use.</summary>
    /// <param name="db">The flat DB, holding the trie columns and the trie node log's merge markers.</param>
    /// <param name="directory">The directory of <c>paprika.db</c>, or <c>null</c> when <paramref name="openDb"/> keeps it in memory.</param>
    /// <param name="openDb">Opens the store with a history depth of <see cref="HistoryDepth"/>, such as <see cref="OpenFile"/>.</param>
    /// <param name="readerWait">How long <see cref="Dispose"/> waits for open readers before it leaves the file mapped.</param>
    /// <param name="logManager">The log manager.</param>
    /// <exception cref="InvalidConfigurationException">The flat DB was synced with another layout.</exception>
    public PaprikaFlatStore(IColumnsDb<FlatDbColumns> db, string? directory, Func<PagedDb> openDb, TimeSpan readerWait, ILogManager logManager)
    {
        BasePersistence.ValidateLayout(db, FlatLayout.PaprikaFlat);
        // This layout keeps the state pointer in Paprika only, so one in RocksDB is from another layout, including a
        // legacy one that predates the layout marker.
        if (BasePersistence.ReadCurrentState(db.GetColumnDb(FlatDbColumns.Metadata)) != StateId.PreGenesis)
        {
            throw new InvalidConfigurationException(
                $"The flat DB holds a state pointer in RocksDB, so it was synced with a layout other than '{FlatLayout.PaprikaFlat}'. " +
                $"Either set 'IFlatDbConfig.Layout' back to that layout, or wipe the flat DB and re-sync.",
                -1);
        }

        _db = db;
        _filePath = directory is null ? null : MemoryMappedPageManager.GetPaprikaFilePath(directory);
        _openDb = openDb;
        _readerWait = readerWait;
        _logger = logManager.GetClassLogger<PaprikaFlatStore>();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Opens the file if it exists. While it is absent, the markers read empty, as a fresh store's, unless the flat DB
    /// holds what only follows the file's creation: its layout stamp, trie nodes, a trie node log merge marker or a snap
    /// sync checkpoint. The file was then lost, and the trie node log must not recover as if nothing was confirmed.
    /// </remarks>
    /// <exception cref="InvalidDataException">The Paprika file or the flat DB was lost or replaced.</exception>
    public IReadOnlyKeyValueStore Committed
    {
        get
        {
            if (Volatile.Read(ref _pagedDb) is null)
            {
                // Under the lock, so a first open under way has created the file before it is looked for.
                using Lock.Scope _ = _commitLock.EnterScope();
                if (_pagedDb is null && (_filePath is null || !File.Exists(_filePath)))
                {
                    ThrowIfStoreLost(fileAbsent: true);
                    return PaprikaFlatMetadata.Empty;
                }

                EnsureOpen();
            }

            return Volatile.Read(ref _committed);
        }
    }

    /// <summary>
    /// Opens, or creates, the Paprika file in <paramref name="directory"/>, sized <paramref name="sizeBytes"/>, bounding the
    /// resident memory of its mapping by <paramref name="pageCacheBytes"/> on Linux, or not at all when that is 0.
    /// </summary>
    /// <remarks>
    /// The file is sparse; a larger size later grows it. The bound keeps at least 8 pages, besides the root pages, which
    /// are never released.
    /// </remarks>
    /// <exception cref="InvalidConfigurationException">The file is larger than <paramref name="sizeBytes"/>.</exception>
    public static PagedDb OpenFile(string directory, long sizeBytes, long pageCacheBytes, ILogManager logManager)
    {
        FileInfo file = new(MemoryMappedPageManager.GetPaprikaFilePath(directory));
        if (file.Exists && file.Length > sizeBytes)
        {
            throw new InvalidConfigurationException(
                $"{file.FullName} holds {file.Length} bytes, more than 'FlatDb.{nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes)}' = {sizeBytes}: {nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes)} can only grow.",
                -1);
        }

        PaprikaPageResidencyAdvisor? advisor = PaprikaPageResidencyAdvisor.Create(pageCacheBytes, HistoryDepth, logManager);
        try
        {
            return PagedDb.MemoryMappedDb(sizeBytes, HistoryDepth, directory, advisor);
        }
        catch
        {
            // The page manager owns the advisor once it is constructed, and may have disposed it already.
            advisor?.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Opens a reader of the latest Paprika root, never of a root chosen by hash; the trie node log binds it to the
    /// RocksDB snapshot taken just before.
    /// </remarks>
    public IFlatMetadataStore.ISnapshot OpenSnapshot(IColumnDbSnapshot<FlatDbColumns> rocksDbSnapshot)
    {
        IVisitableReadOnlyBatch reader = EnsureOpen().BeginReadOnlyBatch();
        try
        {
            return new Snapshot(reader, PaprikaFlatMetadata.Read(reader));
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void WipeForResync()
    {
        MarkWipeStarted();
        WipeState();
    }

    /// <summary>Opens the file if it is not open yet, stamping a fresh store.</summary>
    /// <exception cref="InvalidDataException">The flat DB was lost or replaced while the Paprika file was kept.</exception>
    internal PagedDb EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _pagedDb) is { } db) return db;

        using Lock.Scope _ = _commitLock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pagedDb ?? Open();
    }

    /// <summary>The Paprika reader and the markers of <paramref name="confirmation"/>, which this store opened.</summary>
    /// <exception cref="InvalidOperationException">The trie node log was not built over a <see cref="PaprikaFlatStore"/>.</exception>
    internal static Snapshot SnapshotOf(IFlatMetadataStore.ISnapshot confirmation) =>
        confirmation as Snapshot ?? throw new InvalidOperationException($"The trie node log was not built over a {nameof(PaprikaFlatStore)}.");

    /// <summary>Commits <paramref name="buffer"/> and returns once it is committed.</summary>
    /// <param name="buffer">The writes and markers of one write batch; their state pointer, if any, must be on the latest one.</param>
    /// <param name="options"><see cref="CommitOptions.FlushDataAndRoot"/>, or <see cref="CommitOptions.FlushDataOnly"/> for a buffer that changes no marker.</param>
    /// <param name="exclusive">Commit in a Paprika batch of its own, so no other buffer can fail it.</param>
    /// <remarks>
    /// The buffer may be committed in a group with other queued buffers, with the strongest option among them. A group
    /// whose apply fails is discarded whole and leaves the store usable; a failed Paprika commit poisons it until it is
    /// reopened.
    /// </remarks>
    internal void Commit(PaprikaWriteBuffer buffer, CommitOptions options, bool exclusive)
    {
        EnsureOpen();
        Pending pending = new(buffer, options, exclusive);
        _queue.Enqueue(pending);
        using (_commitLock.EnterScope())
        {
            // Committing only the group at the head of the queue may leave this buffer behind it.
            while (!Volatile.Read(ref pending.Done)) CommitGroupNoLock();
        }

        pending.Error?.Throw();
    }

    /// <summary>
    /// Durably removes the snap sync checkpoint, then durably marks the store wiped for a state sync, keeping its state
    /// pointer, so a wipe interrupted from here on reads back as one.
    /// </summary>
    internal void MarkWipeStarted()
    {
        PagedDb db = EnsureOpen();
        using Lock.Scope _ = _commitLock.EnterScope();
        BasePersistence.WriteSnapCheckpoint(_db, null);
        CommitMarkersNoLock(db, static changes => BasePersistence.SetWipedForSync(changes, wiped: true), clear: false);
    }

    /// <summary>
    /// Wipes the RocksDB columns and every flat entry and the state pointer of Paprika in one durable commit, keeping the
    /// format markers, the wipe marker and the trie node log's markers.
    /// </summary>
    /// <remarks>Follows <see cref="MarkWipeStarted"/>, so a crash before the Paprika commit redoes the wipe.</remarks>
    internal void WipeState()
    {
        PagedDb db = EnsureOpen();
        using Lock.Scope _ = _commitLock.EnterScope();
        BasePersistence.ClearAllColumns(_db);
        // The wipe marker ClearAllColumns writes is Paprika's to keep. Left in RocksDB, an older binary or another layout
        // would read it and recover the trie node log as wiped, deleting its generations.
        BasePersistence.SetWipedForSync(_db.GetColumnDb(FlatDbColumns.Metadata), wiped: false);
        _db.Flush();
        CommitMarkersNoLock(db, static changes => BasePersistence.RemoveCurrentState(changes), clear: true);
    }

    /// <summary>Makes every commit durable.</summary>
    internal void Flush()
    {
        if (Volatile.Read(ref _pagedDb) is not { } db) return;
        using Lock.Scope _ = _commitLock.EnterScope();
        db.Flush();
    }

    /// <summary>Makes every commit durable and refuses later commits and readers, waits a bounded time for the open readers, then closes the file.</summary>
    /// <remarks>Readers still open after the wait keep the file mapped until the process exits, as they would fault otherwise.</remarks>
    public void Dispose()
    {
        PagedDb? db;
        using (_commitLock.EnterScope())
        {
            if (_disposed) return;
            _disposed = true;
            db = _pagedDb;
            try
            {
                db?.Flush();
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("Flushing the PaprikaFlat file on shutdown failed", exception);
            }
        }

        if (db is null) return;

        // Stopped before the store is disposed, which its callback reads.
        if (_metricsTimer is { } timer)
        {
            using ManualResetEvent stopped = new(false);
            if (timer.Dispose(stopped)) stopped.WaitOne();
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(_readerWait.TotalSeconds * Stopwatch.Frequency);
        int openReaders;
        while ((openReaders = db.GetReaderStats().OpenReaders) > 0 && Stopwatch.GetTimestamp() < deadline) Thread.Sleep(ReaderPollInterval);
        if (openReaders > 0 && _logger.IsError)
            _logger.Error($"{openReaders} PaprikaFlat readers are still open after {_readerWait}; the file stays mapped until the process exits");

        db.Dispose();
    }

    private PagedDb Open()
    {
        if (_filePath is null || !File.Exists(_filePath)) ThrowIfStoreLost(fileAbsent: true);
        PagedDb db = _openDb();
        try
        {
            PaprikaFlatMetadata committed;
            using (IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch()) committed = PaprikaFlatMetadata.Read(reader);

            Db.IDb rocksDbMetadata = _db.GetColumnDb(FlatDbColumns.Metadata);
            bool rocksDbStamped = BasePersistence.ReadLayout(rocksDbMetadata) is not null;
            if (BasePersistence.ReadLayout(committed) is not null)
            {
                if (!rocksDbStamped) ThrowStoreLost($"{Describe()} holds {FlatLayout.PaprikaFlat} state, but the flat DB was never stamped with its layout: the flat DB was lost or replaced.");
                Volatile.Write(ref _committed, committed);
            }
            else
            {
                // A bare RocksDB stamp is an open interrupted before Paprika's stamp, which finishes here; anything
                // written after it means the file was replaced.
                ThrowIfStoreLost(fileAbsent: false);
                // RocksDB first, so Paprika stamped means RocksDB stamped.
                if (!rocksDbStamped)
                {
                    BasePersistence.SetLayout(rocksDbMetadata, FlatLayout.PaprikaFlat);
                    rocksDbMetadata.FlushOrThrow();
                }

                CommitMarkersNoLock(db, static changes => BasePersistence.SetLayout(changes, FlatLayout.PaprikaFlat), clear: false);
            }
        }
        catch
        {
            db.Dispose();
            throw;
        }

        Volatile.Write(ref _pagedDb, db);
        _metricsTimer = new Timer(_ => PublishMetrics(db), null, TimeSpan.Zero, MetricsInterval);
        return db;
    }

    /// <summary>
    /// Throws when the flat DB holds writes that only follow the creation of the Paprika file: trie nodes, a trie node
    /// log merge marker or a snap sync checkpoint, and, when the file is absent, the layout stamp an open writes once the
    /// file exists.
    /// </summary>
    /// <remarks>
    /// The trie node log records its shard manifest before the file is first created, so the manifest is no evidence:
    /// a first start that failed before creating the file starts fresh.
    /// </remarks>
    private void ThrowIfStoreLost(bool fileAbsent)
    {
        IReadOnlyKeyValueStore rocksDbMetadata = _db.GetColumnDb(FlatDbColumns.Metadata);
        bool written = (fileAbsent && BasePersistence.ReadLayout(rocksDbMetadata) is not null)
            || BasePersistence.ReadSnapCheckpoint(rocksDbMetadata) is not null
            || TrieNodeLog.TrieNodeLog.HasMergedGenerations(rocksDbMetadata);
        foreach (FlatDbColumns column in TrieColumns)
        {
            written |= ((ISortedKeyValueStore)_db.GetColumnDb(column)).FirstKey is not null;
        }

        if (written) ThrowStoreLost(fileAbsent
            ? $"The flat DB holds state written after {Describe()} was created, but the file is missing: it was lost."
            : $"The flat DB holds state written after {Describe()} was created, but the file is blank: it was replaced.");
    }

    private string Describe() => _filePath ?? $"the in-memory {FlatLayout.PaprikaFlat} store";

    [DoesNotReturn]
    private static void ThrowStoreLost(string reason) =>
        throw new InvalidDataException($"{reason} Restore the flat, flatPaprika and flatTrieNodeLog directories together, or delete all three and resync.");

    private void CommitGroupNoLock()
    {
        PagedDb db = _pagedDb!;
        using ArrayPoolList<Pending> group = TakeGroup();
        long start = Stopwatch.GetTimestamp();
        try
        {
            // Disposing flushed every commit before it, and may close the file once its readers are gone.
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Range deletes read the root before the group, so the reader is opened before the batch, with no commit between.
            using IVisitableReadOnlyBatch preBatch = db.BeginReadOnlyBatch();
            using IBatch batch = db.BeginNextBatch();
            PaprikaFlatMetadata metadata = PaprikaFlatMetadata.Read(preBatch);
            PaprikaFlatMetadata before = metadata;
            using ArrayPoolList<PaprikaWriteBuffer> buffers = new(group.Count);
            CommitOptions options = CommitOptions.FlushDataOnly;
            StateId currentState = BasePersistence.ReadCurrentState(metadata);
            foreach (Pending member in group)
            {
                // A batch checks the state pointer when it is created, but commits later.
                PaprikaWriteBuffer buffer = member.Buffer;
                if (buffer.From != StateId.Sync && buffer.To != StateId.Sync)
                {
                    if (buffer.From != currentState)
                        throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {buffer.From}, Db state: {currentState}");
                    currentState = buffer.To;
                }

                buffers.Add(buffer);
                if (member.Options == CommitOptions.FlushDataAndRoot) options = CommitOptions.FlushDataAndRoot;
            }

            ApplyToBatch(buffers.AsSpan(), preBatch, batch);
            foreach (Pending member in group) metadata = metadata.Merge(member.Buffer.Metadata.Changes);
            if (!ReferenceEquals(metadata, before)) metadata.WriteTo(batch);

            batch.Commit(options).AsTask().GetAwaiter().GetResult();
            Volatile.Write(ref _committed, metadata);
            Metrics.PaprikaFlatCommitTime.Observe(Stopwatch.GetTimestamp() - start);
            if (group.Count > 1) Metrics.PaprikaFlatGroupedCommits++;
        }
        catch (Exception exception)
        {
            ExceptionDispatchInfo error = ExceptionDispatchInfo.Capture(exception);
            foreach (Pending member in group) member.Error = error;
        }
        finally
        {
            foreach (Pending member in group) Volatile.Write(ref member.Done, true);
        }
    }

    /// <summary>The buffer at the head of the queue, alone if it is exclusive, or with the consecutive non-exclusive ones after it.</summary>
    private ArrayPoolList<Pending> TakeGroup()
    {
        ArrayPoolList<Pending> group = new(MaxGroupSize);
        // Only the lock holder dequeues, and the caller's own buffer is still queued.
        _queue.TryDequeue(out Pending? head);
        group.Add(head!);
        while (!head!.Exclusive && group.Count < MaxGroupSize && _queue.TryPeek(out Pending? next) && !next.Exclusive && _queue.TryDequeue(out next))
        {
            group.Add(next);
        }

        return group;
    }

    /// <exception cref="InvalidOperationException">The file is full; the store stays usable.</exception>
    private static void ApplyToBatch(ReadOnlySpan<PaprikaWriteBuffer> buffers, IVisitableReadOnlyBatch preBatch, IBatch batch)
    {
        try
        {
            PaprikaWriteBuffer.ApplyTo(buffers, preBatch, batch);
        }
        catch (IndexOutOfRangeException exception) when (exception.Message.Contains("breached its size", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The PaprikaFlat file is full. Raise 'FlatDb.{nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes)}' and restart; the file grows to the new size.",
                exception);
        }
    }

    /// <summary>Commits a change of the markers alone, optionally after deleting every key, and publishes the markers.</summary>
    private void CommitMarkersNoLock(PagedDb db, Action<PaprikaFlatMetadata.ChangeSet> change, bool clear)
    {
        PaprikaFlatMetadata.ChangeSet changes = new();
        change(changes);
        using IBatch batch = db.BeginNextBatch();
        PaprikaFlatMetadata metadata = PaprikaFlatMetadata.Read(batch).Merge(changes.Changes);
        if (clear) batch.Clear();
        metadata.WriteTo(batch);
        batch.Commit(CommitOptions.FlushDataAndRoot).AsTask().GetAwaiter().GetResult();
        Volatile.Write(ref _committed, metadata);
    }

    private static void PublishMetrics(PagedDb db)
    {
        (int openReaders, uint oldestReaderBatchId, uint lastBatchId) = db.GetReaderStats();
        Metrics.PaprikaFlatOpenReaders = openReaders;
        Metrics.PaprikaFlatOldestReaderLag = lastBatchId - oldestReaderBatchId;
        Metrics.PaprikaFlatUsedBytes = (long)db.NextFreePage * Page.PageSize;
    }

    /// <summary>A reader of the latest Paprika root and the markers of that root.</summary>
    internal sealed class Snapshot(IVisitableReadOnlyBatch reader, PaprikaFlatMetadata metadata) : IFlatMetadataStore.ISnapshot
    {
        /// <summary>The Paprika reader.</summary>
        public IVisitableReadOnlyBatch Reader => reader;

        /// <inheritdoc/>
        public IReadOnlyKeyValueStore Metadata => metadata;

        /// <summary>Closes the reader.</summary>
        public void Dispose() => reader.Dispose();
    }

    private sealed class Pending(PaprikaWriteBuffer buffer, CommitOptions options, bool exclusive)
    {
        public bool Done;
        public ExceptionDispatchInfo? Error;

        public PaprikaWriteBuffer Buffer => buffer;
        public CommitOptions Options => options;
        public bool Exclusive => exclusive;
    }
}
