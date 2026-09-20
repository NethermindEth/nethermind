// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Fills the changeset column behind the history watermark. One thread follows the tip and never runs ahead
/// of the watermark, so it only indexes blocks the capture has already made durable. The backwards retrofit runs on
/// that thread or, when workers are configured, on threads of its own that take chunks downward from the coverage
/// edge; a finished chunk joins coverage only once every chunk above it has, so coverage stays one contiguous range
/// and a restart resumes from a single edge. Every thread sleeps out the rest of its duty cycle so that re-execution
/// stays invisible to the RPC the node is serving.</summary>
public sealed class TransactionChangesetBuilder(
    TransactionChangesetIndex index,
    IHistoryBlockExecutorFactory executors,
    HistoryAvailability availability,
    IFlatDbConfig config,
    ILogManager logManager,
    ITransactionIndexBulkFill? bulkFill = null) : IDisposable, IStoppableService
{
    internal const ulong ChunkBlocks = 128;
    internal const int WarnAfterAttempts = 8;
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RepeatedFailureInterval = TimeSpan.FromMinutes(1);

    private readonly int _dutyCyclePercent = Math.Clamp(config.HistoryTransactionIndexDutyCyclePercent, 1, 100);
    private readonly ulong _retrofitFromBlock = config.HistoryTransactionIndexRetrofitFromBlock;
    private readonly int _workers = Math.Clamp(config.HistoryTransactionIndexWorkers, 1, 64);
    private readonly ILogger _logger = logManager.GetClassLogger<TransactionChangesetBuilder>();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _chunks = new();
    private readonly Lock _shutdown = new();
    private readonly Stack<Chunk> _retry = new();
    private readonly Dictionary<ulong, ulong> _completedByTop = [];
    private readonly HashSet<ulong> _stalledTops = [];
    private readonly List<Thread> _threads = [];
    private IHistoryBlockExecutor? _tipExecutor;
    private ulong? _nextChunkTop;
    private long _progressReportedAt;
    private string? _lastFailure;
    private long _lastFailureReportedAt;
    private bool _wasRetrofitting;
    private ulong? _reportedUnsupportedBoundary;
    private long _builtSinceReport;
    private bool _disposed;

    private bool RetrofitOnWorkers => bulkFill is { Enabled: true } || _retrofitFromBlock != 0 && _workers > 1;

    public void Start()
    {
        if (!index.Enabled || _threads.Count > 0) return;

        _threads.Add(StartThread(FollowTip, "Transaction changeset builder"));
        if (bulkFill is { Enabled: true })
        {
            _threads.Add(StartThread(() => bulkFill.Run(_cancellation.Token), "Transaction changeset bulk fill"));
        }
        else if (RetrofitOnWorkers)
        {
            for (int worker = 0; worker < _workers; worker++) _threads.Add(StartThread(Retrofit, $"Transaction changeset retrofit {worker}"));
        }

        index.ReportCoverage();
        if (_logger.IsInfo) _logger.Info(
            $"Transaction changeset index building at {_dutyCyclePercent}% duty cycle" +
            (bulkFill is { Enabled: true } ? $", using isolated bulk replay from block {_retrofitFromBlock}." :
                _retrofitFromBlock == 0 ? "." : $", retrofitting down to block {_retrofitFromBlock} on {(RetrofitOnWorkers ? _workers : 1)} thread(s)."));
    }

    /// <summary>One step of the tip thread: the next block above coverage, or, without workers, the next below it.</summary>
    public bool TryBuildNext()
    {
        if (!TryNextBlock(out ulong block)) return false;

        _tipExecutor ??= executors.Create();
        if (!Build(block, _tipExecutor)) return false;

        index.TryClaim(block, block);
        return true;
    }

    /// <summary>One step of a retrofit worker: takes the next chunk below the coverage edge and builds it whole. False
    /// when there was nothing to take or the chunk could not be built, so the caller backs off instead of spinning.</summary>
    public bool TryBuildNextChunk(IHistoryBlockExecutor executor)
    {
        if (!TryClaimChunk(out Chunk chunk)) return false;

        bool built = false;
        try
        {
            built = BuildChunk(chunk, executor);
        }
        finally
        {
            if (built) Complete(chunk);
            else Requeue(chunk);
        }

        return built;
    }

    internal bool TryClaimChunk(out Chunk chunk)
    {
        chunk = default;
        if (!index.Enabled || !index.TryGetCoverage(out ulong from, out _)) return false;

        lock (_chunks)
        {
            availability.TryGetGlobalFloor(out ulong floor);
            ulong minimum = Math.Max(_retrofitFromBlock, floor + 1);
            ReconcileFloor(minimum);
            while (_retry.TryPop(out chunk))
            {
                if (chunk.Top < minimum) continue;
                chunk = chunk with { Bottom = Math.Max(chunk.Bottom, minimum) };
                return true;
            }
            if (_stalledTops.Count > 0) return false;

            ulong top = _nextChunkTop ?? from - 1;
            if (from == 0 || top < _retrofitFromBlock || _retrofitFromBlock == 0) return false;

            ulong bottom = top >= ChunkBlocks - 1 + _retrofitFromBlock ? top - (ChunkBlocks - 1) : _retrofitFromBlock;
            bottom = Math.Max(bottom, minimum);
            if (bottom > top) return false;

            chunk = new Chunk(bottom, top);
            _nextChunkTop = bottom == 0 ? null : bottom - 1;
            return true;
        }
    }

    private void ReconcileFloor(ulong minimum)
    {
        _stalledTops.RemoveWhere(top => top < minimum);
        using ArrayPoolList<ulong> tops = new(_completedByTop.Count);
        foreach (ulong top in _completedByTop.Keys) tops.Add(top);
        foreach (ulong top in tops)
        {
            if (top < minimum) _completedByTop.Remove(top);
            else _completedByTop[top] = Math.Max(_completedByTop[top], minimum);
        }
    }

    /// <summary>A chunk runs ascending on one open state, so a key the chunk touches is read from history once and
    /// then from memory; on a disk-bound archive that is most of the cost of the retrofit.</summary>
    internal bool BuildChunk(in Chunk chunk, IHistoryBlockExecutor executor)
    {
        using IHistoryBlockRun? run = executor.BeginRun(chunk.Bottom);
        if (run is null) return false;

        for (ulong block = chunk.Bottom; block <= chunk.Top; block++)
        {
            using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(block);
            if (!run.TryExecuteNext(capture.Tracer, _cancellation.Token) || !capture.Commit()) return false;

            Interlocked.Increment(ref _builtSinceReport);
        }

        return true;
    }

    /// <summary>Joins the chunk to coverage when it touches the edge, then every completed chunk that now touches
    /// it in turn.</summary>
    internal void Complete(in Chunk chunk)
    {
        lock (_chunks)
        {
            _stalledTops.Remove(chunk.Top);
            availability.TryGetGlobalFloor(out ulong floor);
            ReconcileFloor(floor + 1);
            if (chunk.Top <= floor) return;
            _completedByTop[chunk.Top] = Math.Max(chunk.Bottom, floor + 1);
            while (index.TryGetCoverage(out ulong from, out _) && from > 0 && _completedByTop.Remove(from - 1, out ulong bottom))
            {
                index.TryClaim(bottom, from - 1);
            }
        }
    }

    /// <summary>A chunk that keeps failing is never dropped, since coverage could then never cross it; it is retried
    /// at the idle interval and, past the cap, said so, because a coverage edge that stops moving is otherwise silent.
    /// Past the cap no new chunk is handed out either, until every chunk past it has built: coverage cannot reach
    /// anything below a failing one, so the other workers would only be writing rows nothing can ever claim.</summary>
    internal void Requeue(in Chunk chunk)
    {
        Chunk again = chunk with { Attempts = chunk.Attempts + 1 };
        if (again.Attempts % WarnAfterAttempts == 0 && _logger.IsWarn) _logger.Warn(
            $"Transaction changeset chunk {again.Bottom}-{again.Top} has failed {again.Attempts} times; coverage cannot advance below it until it builds.");

        lock (_chunks)
        {
            _retry.Push(again);
            if (again.Attempts >= WarnAfterAttempts) _stalledTops.Add(again.Top);
        }
    }

    public Task StopAsync() => Task.Run(Dispose);

    public void Dispose()
    {
        lock (_shutdown)
        {
            if (_disposed) return;
            _disposed = true;

            _cancellation.Cancel();
            foreach (Thread thread in _threads) thread.Join();
            try
            {
                _tipExecutor?.Dispose();
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }

    private Thread StartThread(Action body, string name)
    {
        Thread thread = new(() => RunThread(body)) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        thread.Start();
        return thread;
    }

    internal void RunThread(Action body)
    {
        try
        {
            body();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Transaction changeset background indexing stopped.", exception);
            _cancellation.Cancel();
        }
    }

    private void FollowTip()
    {
        CancellationToken token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            long startedAt = Stopwatch.GetTimestamp();
            bool built = Guarded(TryBuildNext, token);
            if (token.IsCancellationRequested) return;

            ReportProgress();
            Rest(RestFor(Stopwatch.GetElapsedTime(startedAt), built), token);
        }
    }

    private void Retrofit()
    {
        CancellationToken token = _cancellation.Token;
        using IHistoryBlockExecutor executor = executors.Create();
        while (!token.IsCancellationRequested)
        {
            long startedAt = Stopwatch.GetTimestamp();
            bool built = Guarded(() => TryBuildNextChunk(executor), token);
            if (token.IsCancellationRequested) return;

            Rest(RestFor(Stopwatch.GetElapsedTime(startedAt), built), token);
        }
    }

    /// <summary>Work done is rested off at the duty cycle whether or not it ended in a built block, so a chunk that
    /// fails after most of its work cannot turn the builder into a full-speed loop; a step that built nothing also
    /// waits out the idle delay, so a failure that costs nothing cannot spin.</summary>
    internal TimeSpan RestFor(TimeSpan worked, bool built)
    {
        TimeSpan rest = _dutyCyclePercent >= 100 ? TimeSpan.Zero : worked * (100 - _dutyCyclePercent) / _dutyCyclePercent;
        return built ? rest : rest + IdleDelay;
    }

    private static void Rest(TimeSpan duration, CancellationToken token)
    {
        TimeSpan maximum = TimeSpan.FromMilliseconds(int.MaxValue);
        while (duration > TimeSpan.Zero && !token.IsCancellationRequested)
        {
            TimeSpan interval = duration > maximum ? maximum : duration;
            if (token.WaitHandle.WaitOne(interval)) return;
            duration -= interval;
        }
    }

    private bool Guarded(Func<bool> step, CancellationToken token)
    {
        try
        {
            return step();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn && ShouldReportFailure(exception.Message)) _logger.Warn($"Transaction changeset build failed, retrying: {exception.Message}");
            return false;
        }
    }

    /// <summary>A step retries at the idle interval, so a failure that persists would otherwise be logged every two
    /// seconds for as long as it lasts. The first one is worth seeing and a change of reason is worth seeing; the
    /// same reason again is worth seeing once a minute.</summary>
    private bool ShouldReportFailure(string message)
    {
        lock (_chunks)
        {
            long now = Stopwatch.GetTimestamp();
            if (message == _lastFailure && Stopwatch.GetElapsedTime(_lastFailureReportedAt, now) < RepeatedFailureInterval) return false;

            _lastFailure = message;
            _lastFailureReportedAt = now;
            return true;
        }
    }

    private bool Build(ulong block, IHistoryBlockExecutor executor)
    {
        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(block);
        if (!executor.TryExecute(block, capture.Tracer, _cancellation.Token) || !capture.Commit()) return false;

        Interlocked.Increment(ref _builtSinceReport);
        return true;
    }

    private void ReportProgress()
    {
        index.ReportCoverage();
        if (!_logger.IsInfo) return;

        long now = Stopwatch.GetTimestamp();
        if (_progressReportedAt == 0)
        {
            _progressReportedAt = now;
            return;
        }

        bool retrofitting = index.TryGetCoverage(out ulong from, out ulong to) && _retrofitFromBlock != 0 && from > _retrofitFromBlock;
        if (!retrofitting)
        {
            if (_wasRetrofitting) _logger.Info($"Transaction changeset index covers {from}-{to}; the retrofit is complete and the index follows the tip.");
            _wasRetrofitting = false;
            _progressReportedAt = now;
            Interlocked.Exchange(ref _builtSinceReport, 0);
            return;
        }

        _wasRetrofitting = true;
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_progressReportedAt, now);
        if (elapsed < ProgressInterval) return;

        long built = Interlocked.Exchange(ref _builtSinceReport, 0);
        double blocksPerSecond = built / elapsed.TotalSeconds;
        if (bulkFill is { Enabled: true })
            _logger.Info($"Transaction changeset index covers {from}-{to}; tip-following {blocksPerSecond:F1} blocks/s. Historical bulk replay reports progress separately.");
        else
            _logger.Info(
                $"Transaction changeset index covers {from}-{to}, {blocksPerSecond:F1} blocks/s, {from - _retrofitFromBlock} blocks to {_retrofitFromBlock}, " +
                $"about {TimeSpan.FromSeconds((from - _retrofitFromBlock) / Math.Max(blocksPerSecond, 0.001)):d\\.hh\\:mm}");
        _progressReportedAt = now;
    }

    /// <summary>The tip comes first: a block that just became durable is what a trace is most likely to ask for.
    /// Only once coverage has caught up does this thread walk backwards, and only when no workers do it for it.</summary>
    private bool TryNextBlock(out ulong block)
    {
        block = 0;
        if (!index.Enabled || !availability.TryGetWatermark(out ulong watermark)) return false;
        availability.TryGetGlobalFloor(out ulong floor);
        if (executors.GetLastSupportedBlock(floor + 1, watermark) is not { } supported) return false;
        if (supported < watermark && _reportedUnsupportedBoundary != supported)
        {
            _reportedUnsupportedBoundary = supported;
            if (_logger.IsInfo) _logger.Info($"Transaction changeset index limited to block {supported}: later blocks require block-level access lists.");
        }
        watermark = Math.Min(watermark, supported);

        if (!index.TryGetCoverage(out ulong from, out ulong to))
        {
            block = watermark;
            return CanExecute(block) && availability.IsCovered(block);
        }

        if (to < watermark)
        {
            block = to + 1;
            return CanExecute(block) && availability.IsCovered(block);
        }

        if (RetrofitOnWorkers || _retrofitFromBlock == 0 || from <= _retrofitFromBlock) return false;

        block = from - 1;
        return CanExecute(block) && availability.IsCovered(block);
    }

    /// <summary>A block is re-executed against the state of its parent, so the floor that decides whether it can be
    /// built at all is the parent's, not its own. The lowest buildable block is therefore one above the floor, and a
    /// seeded sync pivot - where the watermark and the floor are the same block - has nothing to build until the
    /// capture moves the watermark past it.</summary>
    private bool CanExecute(ulong block) => block > 0 && !availability.IsBelowGlobalFloor(block - 1);

    internal readonly record struct Chunk(ulong Bottom, ulong Top, int Attempts = 0);
}
