// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class WalkProgress(ILogger logger, int items, ulong from, ulong to) : IDisposable
{
    public const int RowsPerUpdate = 1 << 20;
    public const ulong BlocksPerUpdate = 1 << 16;
    private const int UnitsPerItem = 10_000;
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly int[] _units = new int[items];
    private readonly string?[] _phases = new string?[items];
    private readonly ulong[] _replayBlocks = new ulong[items];
    private readonly double[] _base = new double[items];
    private readonly double[] _scale = new double[items];
    private readonly Stack<(double Base, double Scale)>[] _frames = new Stack<(double Base, double Scale)>[items];
    private readonly CancellationTokenSource _stop = new();
    private long _blocksReplayed;
    private readonly Lock _foldLock = new();
    private ulong _folded;
    private long _foldStartedAt;
    private ulong _foldStartFolded;
    private long _foldLastReportAt;
    private ulong _foldLastFolded;
    private long _lastBlocksReplayed;
    private long _lastReportAt;
    private int _completed;
    private int _startingUnits;
    private Task _loop = Task.CompletedTask;

    public void Start()
    {
        _lastReportAt = Stopwatch.GetTimestamp();
        if (!logger.IsInfo) return;

        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(Heartbeat, _stop.Token);
                    try
                    {
                        logger.Info(Report());
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // A progress line must never end the heartbeat, let alone the walk.
                        if (logger.IsWarn) logger.Warn($"History walk progress report failed: {e}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void PreviouslyCompleted(int item)
    {
        _units[item] = UnitsPerItem;
        _startingUnits += UnitsPerItem;
        _completed++;
    }

    public void EnterChild(int item, int nibble, int children)
    {
        (_frames[item] ??= new Stack<(double Base, double Scale)>()).Push((_base[item], Scale(item)));
        double scale = Scale(item) / children;
        _base[item] += nibble * scale;
        _scale[item] = scale;
    }

    public void ExitChild(int item)
    {
        (double Base, double Scale) frame = _frames[item]!.Pop();
        _base[item] = frame.Base;
        _scale[item] = frame.Scale;
    }

    /// <remarks>
    /// A storage item's replay block is best-effort: forked groups of one range replay on other threads and the last writer wins,
    /// so the block shown can step back between heartbeats, and a scan tick hides it until the next replay update.
    /// </remarks>
    public void Replaying(int item, ulong block)
    {
        if (item < HistoryWalkRun.AccountPartitions) _units[item] = (int)((_base[item] + Fraction(block) * Scale(item)) * UnitsPerItem);
        else Volatile.Write(ref _replayBlocks[item], block);
        _phases[item] = "replay";
    }

    public void ScanningKeySpace(int item, uint position, uint span)
    {
        _units[item] = (int)((ulong)position * UnitsPerItem / span);
        Volatile.Write(ref _replayBlocks[item], 0);
        _phases[item] = "scan";
    }

    public void AddReplayedBlocks(long blocks) => Interlocked.Add(ref _blocksReplayed, blocks);

    public void Completed(int item)
    {
        _units[item] = UnitsPerItem;
        Volatile.Write(ref _replayBlocks[item], 0);
        _phases[item] = null;
        Interlocked.Increment(ref _completed);
    }

    public void Folding(ulong blocks)
    {
        if (blocks == 0 || !logger.IsInfo) return;

        string line;
        lock (_foldLock)
        {
            _folded += blocks;
            long now = Stopwatch.GetTimestamp();
            if (_foldStartedAt == 0)
            {
                _foldStartedAt = now;
                _foldStartFolded = _folded;
                _foldLastReportAt = now;
                _foldLastFolded = _folded;
            }
            else if (Stopwatch.GetElapsedTime(_foldLastReportAt, now) < Heartbeat)
            {
                return;
            }

            double seconds = Stopwatch.GetElapsedTime(_foldLastReportAt, now).TotalSeconds;
            double blocksPerSecond = seconds > 0 ? (_folded - _foldLastFolded) / seconds : 0;
            ulong total = to - from;
            string eta = Eta(Stopwatch.GetElapsedTime(_foldStartedAt), total - _folded, _folded - _foldStartFolded);
            _foldLastReportAt = now;
            _foldLastFolded = _folded;

            float fraction = total == 0 ? 1 : _folded / (float)total;
            line = $"{"Walk root fold",ProgressLogger.PrefixAlignment}{_folded,ProgressLogger.BlockPaddingLength:N0} / {total,ProgressLogger.BlockPaddingLength:N0} ({fraction.ToString("P2", CultureInfo.InvariantCulture),8}) {Progress.GetMeter(fraction, 1)}| {blocksPerSecond,ProgressLogger.SpeedPaddingLength:N0} blocks/s | ETA {eta}";
        }

        logger.Info(line);
    }

    private double Fraction(ulong block) => to == from ? 1 : (block - from) / (double)(to - from);

    private double Scale(int item) => _scale[item] == 0 ? 1 : _scale[item];

    internal string Report()
    {
        long done = 0;
        StringBuilder inFlight = new();
        for (int item = 0; item < items; item++)
        {
            done += _units[item];
            string? phase = _phases[item];
            if (phase is null) continue;

            inFlight.Append(inFlight.Length == 0 ? " | " : ", ").Append(Name(item)).Append(' ').Append(phase).Append(' ').Append((_units[item] / (UnitsPerItem / 100d)).ToString("F1", CultureInfo.InvariantCulture)).Append('%');
            ulong replayBlock = Volatile.Read(ref _replayBlocks[item]);
            if (replayBlock != 0) inFlight.Append(CultureInfo.InvariantCulture, $" (block {replayBlock:N0} / {to:N0})");
        }

        long total = (long)items * UnitsPerItem;
        float fraction = Math.Clamp(done / (float)total, 0, 1);
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_lastReportAt, now).TotalSeconds;
        long replayed = Volatile.Read(ref _blocksReplayed);
        double stepsPerSecond = seconds > 0 ? (replayed - _lastBlocksReplayed) / seconds : 0;
        double blocksPerSecond = stepsPerSecond / items;
        _lastBlocksReplayed = replayed;
        _lastReportAt = now;

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_startedAt);
        long doneThisRun = done - _startingUnits;
        string eta = Eta(elapsed, total - done, doneThisRun);

        return $"{"History walk",ProgressLogger.PrefixAlignment}{Volatile.Read(ref _completed),ProgressLogger.BlockPaddingLength:N0} / {items,ProgressLogger.BlockPaddingLength:N0} ({fraction.ToString("P2", CultureInfo.InvariantCulture),8}) {Progress.GetMeter(fraction, 1)}| {stepsPerSecond,ProgressLogger.SpeedPaddingLength:N0} subtree steps/s (~{blocksPerSecond:N0} per subtree) | ETA {eta} | {GC.GetTotalMemory(false) >> 20:N0} MB managed{inFlight}";
    }

    private static string Name(int item) => item < HistoryWalkRun.AccountPartitions ? $"accounts 0x{item:x2}" : $"storage 0x{item - HistoryWalkRun.AccountPartitions:x2}";

    /// <remarks>
    /// Divides before multiplying and works in double ticks: <c>elapsed * remaining</c> overflows <see cref="TimeSpan"/> days into a long walk.
    /// </remarks>
    internal static string Eta(TimeSpan elapsed, double remaining, double doneThisRun)
    {
        if (doneThisRun <= 0) return "n/a";

        double ticks = elapsed.Ticks * (remaining / doneThisRun);
        return ticks < TimeSpan.MaxValue.Ticks ? Format(TimeSpan.FromTicks((long)ticks)) : "n/a";
    }

    private static string Format(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours:D2}h" : $"{(int)span.TotalHours}h {span.Minutes:D2}m";

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // Disposal follows the walk's verdict; a reporter fault must not replace it.
            if (logger.IsWarn) logger.Warn($"History walk progress reporter failed: {e}");
        }

        _stop.Dispose();
    }
}
