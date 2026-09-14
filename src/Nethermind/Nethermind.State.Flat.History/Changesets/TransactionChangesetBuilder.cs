// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Fills the changeset column behind the history watermark, one block at a time, on its own thread. It
/// never runs ahead of the watermark, so it only ever indexes blocks the capture has already made durable, and it
/// sleeps out the rest of its duty cycle so that re-execution stays invisible to the RPC the node is serving.</summary>
public sealed class TransactionChangesetBuilder(
    TransactionChangesetIndex index,
    IHistoryBlockExecutor executor,
    HistoryAvailability availability,
    IFlatDbConfig config,
    ILogManager logManager) : IDisposable
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    private readonly int _dutyCyclePercent = Math.Clamp(config.HistoryTransactionIndexDutyCyclePercent, 1, 100);
    private readonly ulong _retrofitFromBlock = config.HistoryTransactionIndexRetrofitFromBlock;
    private readonly ILogger _logger = logManager.GetClassLogger<TransactionChangesetBuilder>();
    private readonly CancellationTokenSource _cancellation = new();
    private Thread? _thread;
    private long _progressReportedAt;
    private long _builtSinceReport;

    public void Start()
    {
        if (!index.Enabled || _thread is not null) return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Transaction changeset builder",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
        if (_logger.IsInfo) _logger.Info($"Transaction changeset index building at {_dutyCyclePercent}% duty cycle.");
    }

    public bool TryBuildNext()
    {
        if (!TryNextBlock(out ulong block)) return false;

        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(block);
        if (!executor.TryExecute(block, capture.Tracer, _cancellation.Token)) return false;

        capture.Commit();
        return true;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _cancellation.Dispose();
    }

    private void Run()
    {
        CancellationToken token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            long startedAt = Stopwatch.GetTimestamp();
            bool built;
            try
            {
                built = TryBuildNext();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Transaction changeset build failed, retrying: {exception.Message}");
                built = false;
            }

            if (built)
            {
                _builtSinceReport++;
                ReportProgress();
                Throttle(startedAt, token);
            }
            else token.WaitHandle.WaitOne(IdleDelay);
        }
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

        double blocksPerSecond = _builtSinceReport / elapsed.TotalSeconds;
        string coverage = index.TryGetCoverage(out ulong from, out ulong to) ? $"{from}-{to}" : "none";
        string remaining = _retrofitFromBlock != 0 && from > _retrofitFromBlock
            ? $", {from - _retrofitFromBlock} blocks to {_retrofitFromBlock}, about {TimeSpan.FromSeconds((from - _retrofitFromBlock) / Math.Max(blocksPerSecond, 0.001)):d\\.hh\\:mm}"
            : "";
        _logger.Info($"Transaction changeset index covers {coverage}, {blocksPerSecond:F1} blocks/s{remaining}");
        _progressReportedAt = now;
        _builtSinceReport = 0;
    }

    private void Throttle(long startedAt, CancellationToken token)
    {
        if (_dutyCyclePercent >= 100) return;

        TimeSpan worked = Stopwatch.GetElapsedTime(startedAt);
        TimeSpan rest = worked * (100 - _dutyCyclePercent) / _dutyCyclePercent;
        if (rest > TimeSpan.Zero) token.WaitHandle.WaitOne(rest);
    }

    /// <summary>The tip comes first: a block that just became durable is what a trace is most likely to ask for.
    /// Only once coverage has caught up does the builder walk backwards, down to the configured block and never
    /// below the history floor.</summary>
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

        if (_retrofitFromBlock == 0 || from <= _retrofitFromBlock) return false;

        block = from - 1;
        return !availability.IsBelowGlobalFloor(block) && availability.IsCovered(block);
    }
}
