// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
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
    ILogManager logManager) : IDisposable
{
    internal const ulong ChunkBlocks = 128;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    private readonly int _dutyCyclePercent = Math.Clamp(config.HistoryTransactionIndexDutyCyclePercent, 1, 100);
    private readonly ulong _retrofitFromBlock = config.HistoryTransactionIndexRetrofitFromBlock;
    private readonly int _workers = Math.Clamp(config.HistoryTransactionIndexWorkers, 1, 64);
    private readonly ILogger _logger = logManager.GetClassLogger<TransactionChangesetBuilder>();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _chunks = new();
    private readonly Stack<Chunk> _retry = new();
    private readonly Dictionary<ulong, ulong> _completedByTop = [];
    private readonly List<Thread> _threads = [];
    private IHistoryBlockExecutor? _tipExecutor;
    private ulong? _nextChunkTop;
    private long _progressReportedAt;
    private long _builtSinceReport;
    private bool _disposed;

    private bool RetrofitOnWorkers => _retrofitFromBlock != 0 && _workers > 1;

    public void Start()
    {
        if (!index.Enabled || _threads.Count > 0) return;

        _threads.Add(StartThread(FollowTip, "Transaction changeset builder"));
        if (RetrofitOnWorkers)
        {
            for (int worker = 0; worker < _workers; worker++) _threads.Add(StartThread(Retrofit, $"Transaction changeset retrofit {worker}"));
        }

        if (_logger.IsInfo) _logger.Info(
            $"Transaction changeset index building at {_dutyCyclePercent}% duty cycle" +
            (_retrofitFromBlock == 0 ? "." : $", retrofitting down to block {_retrofitFromBlock} on {(RetrofitOnWorkers ? _workers : 1)} thread(s)."));
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

    /// <summary>One step of a retrofit worker: takes the next chunk below the coverage edge and builds it whole.</summary>
    public bool TryBuildNextChunk(IHistoryBlockExecutor executor)
    {
        if (!TryClaimChunk(out Chunk chunk)) return false;

        if (BuildChunk(chunk, executor)) Complete(chunk);
        else Requeue(chunk);
        return true;
    }

    internal bool TryClaimChunk(out Chunk chunk)
    {
        chunk = default;
        if (!index.Enabled || !index.TryGetCoverage(out ulong from, out _)) return false;

        lock (_chunks)
        {
            if (_retry.TryPop(out chunk)) return true;

            ulong top = _nextChunkTop ?? from - 1;
            if (from == 0 || top < _retrofitFromBlock || _retrofitFromBlock == 0) return false;

            ulong bottom = top >= ChunkBlocks - 1 + _retrofitFromBlock ? top - (ChunkBlocks - 1) : _retrofitFromBlock;
            while (availability.IsBelowGlobalFloor(bottom) && bottom < top) bottom++;
            if (availability.IsBelowGlobalFloor(bottom)) return false;

            chunk = new Chunk(bottom, top);
            _nextChunkTop = bottom == 0 ? null : bottom - 1;
            return true;
        }
    }

    internal bool BuildChunk(in Chunk chunk, IHistoryBlockExecutor executor)
    {
        for (ulong block = chunk.Top; ; block--)
        {
            if (!Build(block, executor)) return false;
            if (block == chunk.Bottom) return true;
        }
    }

    /// <summary>Joins the chunk to coverage when it touches the edge, then every completed chunk that now touches
    /// it in turn.</summary>
    internal void Complete(in Chunk chunk)
    {
        lock (_chunks)
        {
            _completedByTop[chunk.Top] = chunk.Bottom;
            while (index.TryGetCoverage(out ulong from, out _) && from > 0 && _completedByTop.Remove(from - 1, out ulong bottom))
            {
                index.TryClaim(bottom, from - 1);
            }
        }
    }

    private void Requeue(in Chunk chunk)
    {
        lock (_chunks)
        {
            _retry.Push(chunk);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cancellation.Cancel();
        foreach (Thread thread in _threads) thread.Join(TimeSpan.FromSeconds(5));
        _tipExecutor?.Dispose();
        _cancellation.Dispose();
    }

    private Thread StartThread(Action body, string name)
    {
        Thread thread = new(() => body()) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        thread.Start();
        return thread;
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
            if (built) Throttle(startedAt, token);
            else token.WaitHandle.WaitOne(IdleDelay);
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

            if (built) Throttle(startedAt, token);
            else token.WaitHandle.WaitOne(IdleDelay);
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
            if (_logger.IsWarn) _logger.Warn($"Transaction changeset build failed, retrying: {exception.Message}");
            return false;
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
        if (!_logger.IsInfo) return;

        long now = Stopwatch.GetTimestamp();
        if (_progressReportedAt == 0)
        {
            _progressReportedAt = now;
            return;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_progressReportedAt, now);
        if (elapsed < ProgressInterval) return;

        long built = Interlocked.Exchange(ref _builtSinceReport, 0);
        double blocksPerSecond = built / elapsed.TotalSeconds;
        string coverage = index.TryGetCoverage(out ulong from, out ulong to) ? $"{from}-{to}" : "none";
        string remaining = _retrofitFromBlock != 0 && from > _retrofitFromBlock
            ? $", {from - _retrofitFromBlock} blocks to {_retrofitFromBlock}, about {TimeSpan.FromSeconds((from - _retrofitFromBlock) / Math.Max(blocksPerSecond, 0.001)):d\\.hh\\:mm}"
            : "";
        _logger.Info($"Transaction changeset index covers {coverage}, {blocksPerSecond:F1} blocks/s{remaining}");
        _progressReportedAt = now;
    }

    private void Throttle(long startedAt, CancellationToken token)
    {
        if (_dutyCyclePercent >= 100) return;

        TimeSpan worked = Stopwatch.GetElapsedTime(startedAt);
        TimeSpan rest = worked * (100 - _dutyCyclePercent) / _dutyCyclePercent;
        if (rest > TimeSpan.Zero) token.WaitHandle.WaitOne(rest);
    }

    /// <summary>The tip comes first: a block that just became durable is what a trace is most likely to ask for.
    /// Only once coverage has caught up does this thread walk backwards, and only when no workers do it for it.</summary>
    private bool TryNextBlock(out ulong block)
    {
        block = 0;
        if (!index.Enabled || !availability.TryGetWatermark(out ulong watermark)) return false;

        if (!index.TryGetCoverage(out ulong from, out ulong to))
        {
            block = watermark;
            return availability.IsCovered(block);
        }

        if (to < watermark)
        {
            block = to + 1;
            return availability.IsCovered(block);
        }

        if (RetrofitOnWorkers || _retrofitFromBlock == 0 || from <= _retrofitFromBlock) return false;

        block = from - 1;
        return !availability.IsBelowGlobalFloor(block) && availability.IsCovered(block);
    }

    internal readonly record struct Chunk(ulong Bottom, ulong Top);
}
