// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Writes the state of a snap-sync pivot as unwindowed (v2) history rows, in the background, so the sync completes in
/// constant time. v2 rows are post-values with no fallback to the live state, so a key with no row reads as absent:
/// the whole pivot state must be in rows before the history can start there.
/// </summary>
/// <remarks>
/// The rows come from the persisted flat columns, which hold the pivot's state for as long as the flat persist is held
/// (<see cref="IFlatPersistenceCaptureHook.HoldsFlatPersist"/>). Block processing goes on meanwhile; its snapshots
/// wait in memory and in the persisted-snapshot tier, and are captured as usual once the image is complete.
/// <para>The work is split into <see cref="PendingPivotImage.Partitions"/> partitions by the first byte of the address
/// hash. A partition is recorded as done only after its rows, in the same write-ahead log, so a restart writes again
/// only the partitions that were not done. Writing a row again gives the same row.</para>
/// <para>The rows go at the start of the pivot's checkpoint window, and every block from there up to the pivot is
/// marked with the pivot's root, so the every-block walk can build proof commitments for the pivot itself. Reads below
/// the floor are refused, so nothing ever sees that image at the blocks before the pivot.</para>
/// </remarks>
public sealed class PivotHistoryImageWriter : IDisposable
{
    private const int RowsPerBatch = 100_000;
    private const int RowsPerCancellationCheck = 4_096;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PersistedStatePollDelay = TimeSpan.FromMilliseconds(100);

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly ISortedKeyValueStore _persistedStorage;
    private readonly HistoryStore _accountHistory;
    private readonly HistoryStore _storageHistory;
    private readonly HistoryAvailability _availability;
    private readonly Lazy<IPivotStateSource> _source;
    private readonly byte _formatVersion;
    private readonly int _parallelism;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();

    private PendingPivotImage? _pending;
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _run = Task.CompletedTask;
    private bool _started;
    private int _disposed;
    private long _accountRows;
    private long _slotRows;
    private long _startedAt;
    private long _lastProgressAt;

    public PivotHistoryImageWriter(
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        IFlatDbConfig config,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        Lazy<IPivotStateSource> source,
        ILogManager logManager,
        int parallelism = 0)
    {
        _history = history;
        _persistedStorage = (ISortedKeyValueStore)db.GetColumnDb(FlatDbColumns.Storage);
        _logger = logManager.GetClassLogger<PivotHistoryImageWriter>();
        _accountHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.AccountHistory), _logger);
        _storageHistory = new HistoryStore(history.GetColumnDb(FlatHistoryColumns.StorageHistory), _logger);
        _availability = availability;
        _source = source;
        _formatVersion = rowFormat.FormatVersion;
        _parallelism = parallelism > 0 ? parallelism : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        _released.SetResult();

        if (config.HistoryEnabled && !rowFormat.IsV3 && availability.TryGetPendingPivotImage(out PendingPivotImage? pending))
        {
            ResumeOrDrop(pending);
        }
    }

    /// <summary>Whether a pivot image is being written; the flat persist must not move until it is complete.</summary>
    public bool Holds => Volatile.Read(ref _pending) is not null;

    /// <summary>Completes when <see cref="Holds"/> turns false.</summary>
    public Task Released
    {
        get
        {
            lock (_lock) return _released.Task;
        }
    }

    /// <summary>Raised once when the image cannot be written; the history then has no rows to start from.</summary>
    public event Action<string>? Failed;

    /// <summary>Starts the writer of a pivot image recorded before a restart, or of one begun before this call.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            if (_pending is not null) _run = Task.Run(() => RunAsync(_pending, _cts.Token));
        }
    }

    /// <summary>Records the pivot image durably and starts writing it; the flat persist is held from this call on.</summary>
    /// <remarks>Call before the persisted state advances to the pivot; the rows are read only once it has.</remarks>
    public void Begin(ulong pivotBlock, in ValueHash256 pivotStateRoot, ulong imageBlock)
    {
        PendingPivotImage image = new(pivotBlock, pivotStateRoot, imageBlock);
        lock (_lock)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException(
                    $"A history image of the sync pivot {_pending.PivotBlock} is already being written; cannot begin one at {pivotBlock}.");
            }

            _availability.WritePendingPivotImage(image);
            _history.SyncWal();
            Hold(image);
            if (_started) _run = Task.Run(() => RunAsync(image, _cts.Token));
        }

        if (_logger.IsInfo) _logger.Info(
            $"Flat history starts at the sync pivot {pivotBlock}: its state is written as history rows at block {imageBlock} in the background. " +
            "Until that completes, the flat state stays at the pivot, block processing goes on, and as-of reads report no history.");
    }

    /// <summary>After a restart the persisted state is the pivot only if the sync completed: then the image resumes.
    /// Otherwise the crash came before the state pointer advanced, the sync runs again and begins a new image.</summary>
    private void ResumeOrDrop(PendingPivotImage pending)
    {
        StateId persisted = _source.Value.PersistedState;
        if (persisted.BlockNumber == pending.PivotBlock && persisted.StateRoot == pending.PivotStateRoot)
        {
            Hold(pending);
            if (_logger.IsInfo) _logger.Info(
                $"Resuming the history image of the sync pivot {pending.PivotBlock}: {pending.DoneCount} of {PendingPivotImage.Partitions} partitions are done. " +
                "The flat state stays at the pivot until it completes.");
            return;
        }

        _availability.ClearPendingPivotImage();
        if (_logger.IsInfo) _logger.Info(
            $"Dropped the unfinished history image of the sync pivot {pending.PivotBlock}: the flat state is at {persisted}, so the sync did not complete. " +
            "The next sync starts a new image.");
    }

    private void Hold(PendingPivotImage image)
    {
        lock (_lock)
        {
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pending, image);
        }
    }

    private void Release()
    {
        TaskCompletionSource released;
        lock (_lock)
        {
            Volatile.Write(ref _pending, null);
            released = _released;
        }

        released.TrySetResult();
    }

    private async Task RunAsync(PendingPivotImage image, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForPersistedPivot(image, cancellationToken);
            await WritePartitions(image, cancellationToken);
            Complete(image);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsInfo) _logger.Info(
                $"Stopped writing the history image of the sync pivot {image.PivotBlock} at {image.DoneCount} of {PendingPivotImage.Partitions} partitions; it resumes on the next start.");
        }
        catch (Exception e)
        {
            Fail(image, e);
        }
    }

    private async Task WaitForPersistedPivot(PendingPivotImage image, CancellationToken cancellationToken)
    {
        while (true)
        {
            StateId persisted = _source.Value.PersistedState;
            if (persisted.BlockNumber == image.PivotBlock && persisted.StateRoot == image.PivotStateRoot) return;

            await Task.Delay(PersistedStatePollDelay, cancellationToken);
        }
    }

    private async Task WritePartitions(PendingPivotImage image, CancellationToken cancellationToken)
    {
        List<int> partitions = new(PendingPivotImage.Partitions);
        for (int partition = 0; partition < PendingPivotImage.Partitions; partition++)
        {
            if (!image.IsDone(partition)) partitions.Add(partition);
        }

        _startedAt = _lastProgressAt = Stopwatch.GetTimestamp();
        ParallelOptions options = new() { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(partitions, options, (partition, token) =>
        {
            WritePartition(image, (byte)partition, token);
            MarkDone(image, partition);
            return ValueTask.CompletedTask;
        });
    }

    private void WritePartition(PendingPivotImage image, byte partition, CancellationToken cancellationToken)
    {
        using ImageBatch batch = new(_history);
        ulong imageBlock = image.ImageBlock;

        _source.Value.ForEachAccount(image.PivotStateRoot, partition, (path, account) =>
        {
            using ArrayPoolSpan<byte> value = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
            _accountHistory.RecordChange(imageBlock, path.Bytes, value, batch.Accounts);
            batch.RowWritten();
            Interlocked.Increment(ref _accountRows);
        }, cancellationToken);

        // The flat storage key starts with the address hash, so a partition is one contiguous key range.
        byte[] lower = new byte[BaseFlatPersistence.StorageKeyLength];
        lower[0] = partition;
        byte[] upper;
        if (partition < byte.MaxValue)
        {
            upper = new byte[BaseFlatPersistence.StorageKeyLength];
            upper[0] = (byte)(partition + 1);
        }
        else
        {
            upper = new byte[BaseFlatPersistence.StorageKeyLength + 1];
            upper.AsSpan().Fill(byte.MaxValue);
        }

        long slots = 0;
        // One pass over the whole column: keep it out of the block cache that block processing reads from.
        using ISortedView view = _persistedStorage.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            ReadOnlySpan<byte> value = view.CurrentValue;
            if (key.Length != BaseFlatPersistence.StorageKeyLength || value.IsEmpty) continue;

            // History storage rows key and encode exactly like the flat storage column.
            _storageHistory.RecordChange(imageBlock, key, value, batch.Storage);
            batch.RowWritten();
            if (++slots % RowsPerCancellationCheck == 0) cancellationToken.ThrowIfCancellationRequested();
        }

        Interlocked.Add(ref _slotRows, slots);
    }

    private void MarkDone(PendingPivotImage image, int partition)
    {
        lock (_lock)
        {
            image.MarkDone(partition);
            _availability.WritePendingPivotImage(image);

            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastProgressAt, now) < ProgressInterval || !_logger.IsInfo) return;

            _lastProgressAt = now;
            _logger.Info(
                $"History image of the sync pivot {image.PivotBlock}: {image.DoneCount} of {PendingPivotImage.Partitions} partitions, " +
                $"{Interlocked.Read(ref _accountRows):N0} accounts and {Interlocked.Read(ref _slotRows):N0} storage slots written.");
        }
    }

    /// <summary>Marks the blocks from the image up to the pivot with the pivot's root (the rows hold the pivot's state at
    /// each of them, which the every-block walk checks), then publishes the floor and the watermark at the pivot.</summary>
    /// <remarks>The floor goes first: a read below it is refused even while no watermark exists, whereas a watermark
    /// without the floor would answer below the image from rows that do not reach that far. The pending record goes
    /// last, so a crash at any point resumes here and writes the same values again.</remarks>
    private void Complete(PendingPivotImage image)
    {
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch())
        {
            IWriteBatch availableBlocks = batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks);
            for (ulong block = image.ImageBlock; block <= image.PivotBlock; block++)
            {
                HistoryAvailability.MarkBlock(availableBlocks, block, image.PivotStateRoot, _formatVersion);
            }
        }

        // An image starting at block 0 overwrites the genesis marker a reader may already have matched.
        _availability.ForgetMatchedRoots();
        _history.SyncWal();
        _availability.PublishUnwindowedPivotFloor(image.PivotBlock, image.ImageBlock);
        _availability.PublishWatermark(image.PivotBlock, _formatVersion);
        _availability.ClearPendingPivotImage();
        _history.SyncWal();
        Metrics.FlatHistoryWatermark = (long)image.PivotBlock;

        if (_logger.IsInfo) _logger.Info(
            $"Flat history starts at the sync pivot {image.PivotBlock}: {Interlocked.Read(ref _accountRows):N0} accounts and " +
            $"{Interlocked.Read(ref _slotRows):N0} storage slots written in {Stopwatch.GetElapsedTime(_startedAt)}. The flat state moves on from the pivot.");

        Release();
    }

    /// <summary>A missing or unreadable pivot state does not resolve by itself: the hold is released so the node goes
    /// on without history, rather than wait for an image that cannot complete.</summary>
    private void Fail(PendingPivotImage image, Exception e)
    {
        if (_logger.IsError) _logger.Error($"Could not write the history image of the sync pivot {image.PivotBlock}.", e);
        try
        {
            _availability.ClearPendingPivotImage();
            Failed?.Invoke(
                $"History capture stopped: the state of the sync pivot {image.PivotBlock} could not be written as history rows ({e.Message}). " +
                "As-of reads report no history. Resync the flatHistory database to re-enable capture.");
        }
        finally
        {
            Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        Task run;
        lock (_lock) run = _run;
        run.GetAwaiter().GetResult();
        _cts.Dispose();
    }

    private sealed class ImageBatch : IDisposable
    {
        private readonly IColumnsDb<FlatHistoryColumns> _history;
        private IColumnsWriteBatch<FlatHistoryColumns> _batch;
        private int _rows;

        public ImageBatch(IColumnsDb<FlatHistoryColumns> history)
        {
            _history = history;
            _batch = Open();
        }

        public IWriteBatch Accounts { get; private set; } = null!;
        public IWriteBatch Storage { get; private set; } = null!;

        public void RowWritten()
        {
            if (++_rows < RowsPerBatch) return;

            _batch.Dispose();
            _batch = Open();
            _rows = 0;
        }

        private IColumnsWriteBatch<FlatHistoryColumns> Open()
        {
            IColumnsWriteBatch<FlatHistoryColumns> batch = _history.StartWriteBatch();
            Accounts = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
            Storage = batch.GetColumnBatch(FlatHistoryColumns.StorageHistory);
            return batch;
        }

        public void Dispose() => _batch.Dispose();
    }
}
