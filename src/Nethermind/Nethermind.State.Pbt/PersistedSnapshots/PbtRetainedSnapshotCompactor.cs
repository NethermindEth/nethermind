// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Pbt.PersistedSnapshots;

internal sealed class PbtRetainedSnapshotCompactor(
    PbtSnapshotRepository repository,
    [KeyFilter(DbNames.Pbt)] IArenaManager arena,
    [KeyFilter(DbNames.Pbt)] BlobArenaManager blobs,
    IRefCountingMemoryProvider nodeGroupMemory,
    IPbtConfig config,
    [KeyFilter(DbNames.Pbt)] ICompactionSchedule schedule,
    IPbtRetainedSnapshotLoader loader,
    IProcessExitSource processExitSource,
    ILogManager logManager) : IPbtRetainedSnapshotCompactor
{
    private const int QueueCapacity = 16;
    private const int BoundaryWorkerCount = 4;
    private readonly ILogger _logger = logManager.GetClassLogger<PbtRetainedSnapshotCompactor>();
    private readonly Channel<(ArrayPoolList<StateId> Batch, ulong Floor)> _jobs = Channel.CreateBounded<(ArrayPoolList<StateId>, ulong)>(QueueCapacity);
    private readonly Channel<(StateId State, ulong Floor)> _largeJobs = Channel.CreateBounded<(StateId, ulong)>(QueueCapacity);
    private readonly CancellationToken _shutdown = processExitSource.Token;
    private readonly Lock _startLock = new();
    private Task? _worker;
    private Task[]? _largeWorkers;
    private Task? _disposeTask;
    private bool _disposed;

    public async ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, ulong persistedBlockNumber, CancellationToken cancellationToken)
    {
        try
        {
            EnsureStarted();
            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.ThrowIfCancellationRequested();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown);
            await _jobs.Writer.WriteAsync((batch, persistedBlockNumber), linked.Token);
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    private void EnsureStarted()
    {
        lock (_startLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= RunBatches();
            if (_largeWorkers is null)
            {
                _largeWorkers = new Task[BoundaryWorkerCount];
                for (int i = 0; i < _largeWorkers.Length; i++) _largeWorkers[i] = RunLargeBoundaries();
            }
        }
    }

    private async Task RunBatches()
    {
        try
        {
            await foreach ((ArrayPoolList<StateId> batch, ulong floor) in _jobs.Reader.ReadAllAsync(_shutdown))
            {
                try { await ProcessBatch(batch, floor); }
                catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
                {
                    _logger.Error("Error compacting retained PBT snapshot batch", ex);
                }
                finally { batch.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _jobs.Writer.TryComplete();
            while (_jobs.Reader.TryRead(out (ArrayPoolList<StateId> Batch, ulong Floor) item)) item.Batch.Dispose();
        }
    }

    private async Task ProcessBatch(ArrayPoolList<StateId> batch, ulong floor)
    {
        using ArrayPoolList<StateId> boundaries = new(batch.Count);
        using ArrayPoolList<StateId> largeBoundaries = new(batch.Count);
        SortedDictionary<ulong, List<StateId>> buckets = [];
        foreach (StateId state in batch)
        {
            if (state.BlockNumber == 0) continue;
            if (schedule.IsLargeCompactionBoundary(state.BlockNumber))
            {
                boundaries.Add(state);
                largeBoundaries.Add(state);
            }
            else if (schedule.IsCompactSizeBoundary(state.BlockNumber)) boundaries.Add(state);
            else
            {
                ulong width = schedule.GetPersistedSnapshotCompactSize(state.BlockNumber);
                if (!buckets.TryGetValue(width, out List<StateId>? bucket)) buckets.Add(width, bucket = []);
                bucket.Add(state);
            }
        }
        foreach (List<StateId> bucket in buckets.Values)
            Parallel.ForEach(bucket, new ParallelOptions { CancellationToken = _shutdown }, state =>
                RunJob(state, () => DoCompactSnapshot(state, floor, _shutdown)));
        foreach (StateId state in boundaries)
        {
            _shutdown.ThrowIfCancellationRequested();
            RunJob(state, () => DoCompactCompactSized(state, _shutdown));
        }
        foreach (StateId state in largeBoundaries)
            await _largeJobs.Writer.WriteAsync((state, floor), _shutdown);
    }

    private void RunJob(StateId state, Action job)
    {
        try { job(); }
        catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
        {
            _logger.Error($"Error compacting retained PBT snapshot {state}", ex);
        }
    }

    private async Task RunLargeBoundaries()
    {
        try
        {
            await foreach ((StateId state, ulong floor) in _largeJobs.Reader.ReadAllAsync(_shutdown))
                RunJob(state, () => DoCompactSnapshot(state, floor, _shutdown));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_startLock)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _jobs.Writer.TryComplete();
            _disposeTask = DrainAsync();
            return new(_disposeTask);
        }
    }

    private async Task DrainAsync()
    {
        if (_worker is not null) await _worker;
        _largeJobs.Writer.TryComplete();
        if (_largeWorkers is not null) await Task.WhenAll(_largeWorkers);
        while (_jobs.Reader.TryRead(out (ArrayPoolList<StateId> Batch, ulong Floor) item)) item.Batch.Dispose();
        while (_largeJobs.Reader.TryRead(out _)) { }
    }

    internal bool DoCompactSnapshot(StateId snapshotTo, ulong persistedBlockNumber = 0, CancellationToken cancellationToken = default)
    {
        ulong width = schedule.GetPersistedSnapshotCompactSize(snapshotTo.BlockNumber);
        if (snapshotTo.BlockNumber == 0 || width <= 1 || repository.RetainedCount < 2) return false;
        ulong floor = unchecked((ulong)Math.Max(unchecked((long)snapshotTo.BlockNumber) - checked((long)width), unchecked((long)persistedBlockNumber)));
        return CompactRange(snapshotTo, floor, isCompactSized: false, cancellationToken);
    }

    internal bool DoCompactCompactSized(StateId snapshotTo, CancellationToken cancellationToken = default)
    {
        if (snapshotTo.BlockNumber == 0 || !schedule.IsCompactSizeBoundary(snapshotTo.BlockNumber) && !schedule.IsLargeCompactionBoundary(snapshotTo.BlockNumber)
            || repository.RetainedCount < 2) return false;
        ulong width = schedule.GetCompactSize(snapshotTo.BlockNumber);
        return CompactRange(snapshotTo, unchecked(snapshotTo.BlockNumber - width), isCompactSized: true, cancellationToken);
    }

    private bool CompactRange(StateId target, ulong floor, bool isCompactSized, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using PbtSnapshotChain? chain = repository.TryLeaseRetainedChain(target, floor);
        if (chain is null || chain.Layers.Count < 2) return false;
        using ArrayPoolList<PbtRetainedSnapshot> sources = new(chain.Layers.Count);
        long estimatedSize = 16384, bloomCapacity = 0;
        HashSet<RefCountedBloomFilter> counted = [];
        foreach (PbtSnapshotLease layer in chain.Layers)
        {
            PbtRetainedSnapshot snapshot = layer.Retained!;
            sources.Add(snapshot);
            estimatedSize = checked(estimatedSize + snapshot.Size);
            if (counted.Add(snapshot.BloomRef)) bloomCapacity = checked(bloomCapacity + snapshot.BloomRef.Filter.Count);
        }
        SnapshotTier tier = isCompactSized ? SnapshotTier.PersistedCompactSized
            : schedule.IsLargeCompactionBoundary(target.BlockNumber) ? SnapshotTier.PersistedLargeCompacted : SnapshotTier.PersistedSmallCompacted;
        using RefCountedBloomFilter bloom = new(config.PersistedSnapshotBloomBitsPerKey > 0 && bloomCapacity > 0
            ? new BloomFilter(tier == SnapshotTier.PersistedSmallCompacted ? 1 : bloomCapacity, config.PersistedSnapshotBloomBitsPerKey)
            : BloomFilter.AlwaysTrue());
        PbtRetainedMetadata metadata = new(sources[0].From, sources[^1].To, sources[^1].TreeRoot);
        ArenaReservation reservation;
        SnapshotLocation location;
        using (ArenaWriter writer = arena.CreateWriter(estimatedSize, small: tier == SnapshotTier.PersistedSmallCompacted))
        {
            PbtRetainedSnapshotMerger.Merge(sources.AsSpan(), metadata, ref writer.GetWriter(), bloom.Filter, cancellationToken);
            (location, reservation) = writer.Complete();
        }
        using (reservation)
        {
            reservation.Fsync();
            cancellationToken.ThrowIfCancellationRequested();
            using PbtRetainedSnapshot output = new(new(metadata.From, metadata.To, location, tier), reservation, blobs, nodeGroupMemory, bloom);
            cancellationToken.ThrowIfCancellationRequested();
            bool registered = loader.RegisterCompacted(output, sources.AsSpan());
            if (registered)
            {
                if (tier == SnapshotTier.PersistedSmallCompacted) reservation.AdviseAndFadviseDontNeed();
                else
                {
                    WarmIndex(reservation);
                    if (tier == SnapshotTier.PersistedLargeCompacted)
                        repository.ShareBloomAcrossRange(output.From, output.To, output.BloomRef);
                }
            }
            return registered;
        }
    }

    private static void WarmIndex(ArenaReservation reservation)
    {
        ArenaByteReader reader = reservation.CreateReader();
        Bound table = new(0, reader.Length);
        if (SortedTable.TryReadFooter<ArenaByteReader, NoOpPin>(reader, table, out SortedTable.Footer footer))
        {
            long start = SortedTable.IndexBlockStart(table, footer);
            reservation.TouchRangePopulate(start, table.Length - start);
        }
    }
}
