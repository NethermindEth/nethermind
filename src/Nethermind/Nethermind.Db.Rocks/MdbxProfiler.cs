// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

internal sealed class MdbxProfiler
{
    private const double MicrosecondsPerSecond = 1_000_000.0;
    private const double MillisecondsPerSecond = 1_000.0;
    private const double BytesPerMiB = 1024.0 * 1024.0;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Func<MdbxStorageStats?> _statsProvider;
    private readonly long _intervalTicks;
    private readonly long _slowTransactionTicks;
    private readonly int _hotPathSampleRate;
    private long _nextReportTimestamp;

    private long _readTransactions;
    private long _readTicks;
    private long _writeTransactions;
    private long _writeWaitTicks;
    private long _writeTicks;
    private long _writeOperations;
    private long _gets;
    private long _getHits;
    private long _getKeyBytes;
    private long _getValueBytes;
    private long _puts;
    private long _putKeyBytes;
    private long _putValueBytes;
    private long _deletes;
    private long _merges;
    private long _queuedWrites;
    private long _queuedKeyBytes;
    private long _queuedValueBytes;
    private long _maxQueuedOperations;
    private long _batchGroups;
    private long _batchGroupBatches;
    private long _batchGroupOperations;
    private long _batchGroupBytes;
    private long _maxBatchGroupBatches;
    private long _maxBatchGroupOperations;
    private long _maxBatchGroupBytes;
    private long _compressionAttempts;
    private long _compressionRejected;
    private long _compressedValues;
    private long _escapedRawValues;
    private long _compressionInputBytes;
    private long _compressionStoredBytes;

    [ThreadStatic]
    private static int t_hotPathSampleCounter;

    private MdbxProfiler(string path, ILogger logger, TimeSpan interval, TimeSpan slowTransactionThreshold, int hotPathSampleRate, Func<MdbxStorageStats?> statsProvider)
    {
        _path = path;
        _logger = logger;
        _statsProvider = statsProvider;
        _intervalTicks = Math.Max(1, ToTicks(interval));
        _slowTransactionTicks = ToTicks(slowTransactionThreshold);
        _hotPathSampleRate = Math.Max(1, hotPathSampleRate);
        _nextReportTimestamp = Stopwatch.GetTimestamp() + _intervalTicks;
    }

    public static MdbxProfiler? Create(string path, MdbxTuningOptions options, ILogger logger, Func<MdbxStorageStats?> statsProvider)
    {
        if (!options.EnableProfiling || !logger.IsInfo)
        {
            return null;
        }

        MdbxProfiler profiler = new(path, logger, options.ProfileInterval, options.SlowTransactionThreshold, options.HotPathSampleRate, statsProvider);
        logger.Info(
            string.Create(
                CultureInfo.InvariantCulture,
                $"MDBX profiling enabled for {path}: interval={options.ProfileInterval.TotalSeconds:F0}s slowTransaction={options.SlowTransactionThreshold.TotalMilliseconds:F0}ms hotPathSampleRate={options.HotPathSampleRate}"));
        return profiler;
    }

    public long StartReadTransaction() =>
        ShouldSampleHotPath() ? Stopwatch.GetTimestamp() : 0;

    public void RecordReadTransaction(long started)
    {
        if (started == 0)
        {
            return;
        }

        int weight = _hotPathSampleRate;
        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        Interlocked.Add(ref _readTransactions, weight);
        Interlocked.Add(ref _readTicks, elapsedTicks * weight);
        MaybeReport();
    }

    public void RecordWriteTransaction(long waitTicks, long elapsedTicks, int operationCount)
    {
        Interlocked.Increment(ref _writeTransactions);
        Interlocked.Add(ref _writeWaitTicks, waitTicks);
        Interlocked.Add(ref _writeTicks, elapsedTicks);
        Interlocked.Add(ref _writeOperations, operationCount);

        if (_slowTransactionTicks > 0 && elapsedTicks >= _slowTransactionTicks && _logger.IsInfo)
        {
            _logger.Info(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"MDBX slow write transaction path={_path} elapsedMs={ToMilliseconds(elapsedTicks):F1} waitMs={ToMilliseconds(waitTicks):F1} operations={operationCount}"));
        }

        MaybeReport();
    }

    public void RecordGet(bool hit, int keyBytes, int valueBytes)
    {
        if (!ShouldSampleHotPath())
        {
            return;
        }

        int weight = _hotPathSampleRate;
        Interlocked.Add(ref _gets, weight);
        Interlocked.Add(ref _getKeyBytes, (long)keyBytes * weight);
        if (hit)
        {
            Interlocked.Add(ref _getHits, weight);
            Interlocked.Add(ref _getValueBytes, (long)valueBytes * weight);
        }
    }

    public void RecordPut(int keyBytes, int valueBytes)
    {
        if (!ShouldSampleHotPath())
        {
            return;
        }

        int weight = _hotPathSampleRate;
        Interlocked.Add(ref _puts, weight);
        Interlocked.Add(ref _putKeyBytes, (long)keyBytes * weight);
        Interlocked.Add(ref _putValueBytes, (long)valueBytes * weight);
    }

    public void RecordDelete() =>
        Interlocked.Increment(ref _deletes);

    public void RecordMerge() =>
        Interlocked.Increment(ref _merges);

    public void RecordQueuedWrite(int keyBytes, int valueBytes, int pendingOperations)
    {
        if (ShouldSampleHotPath())
        {
            int weight = _hotPathSampleRate;
            Interlocked.Add(ref _queuedWrites, weight);
            Interlocked.Add(ref _queuedKeyBytes, (long)keyBytes * weight);
            Interlocked.Add(ref _queuedValueBytes, (long)valueBytes * weight);
        }

        UpdateMax(ref _maxQueuedOperations, pendingOperations);
    }

    public void RecordBatchGroup(int batchCount, int operationCount, long byteCount)
    {
        Interlocked.Increment(ref _batchGroups);
        Interlocked.Add(ref _batchGroupBatches, batchCount);
        Interlocked.Add(ref _batchGroupOperations, operationCount);
        Interlocked.Add(ref _batchGroupBytes, byteCount);
        UpdateMax(ref _maxBatchGroupBatches, batchCount);
        UpdateMax(ref _maxBatchGroupOperations, operationCount);
        UpdateMax(ref _maxBatchGroupBytes, byteCount);
    }

    public void RecordValueEncoding(MdbxValueEncodingKind encodingKind, int inputLength, int storedLength)
    {
        switch (encodingKind)
        {
            case MdbxValueEncodingKind.CompressionRejected:
                Interlocked.Increment(ref _compressionAttempts);
                Interlocked.Increment(ref _compressionRejected);
                Interlocked.Add(ref _compressionInputBytes, inputLength);
                Interlocked.Add(ref _compressionStoredBytes, inputLength);
                break;
            case MdbxValueEncodingKind.Compressed:
                Interlocked.Increment(ref _compressionAttempts);
                Interlocked.Increment(ref _compressedValues);
                Interlocked.Add(ref _compressionInputBytes, inputLength);
                Interlocked.Add(ref _compressionStoredBytes, storedLength);
                break;
            case MdbxValueEncodingKind.EscapedRaw:
                Interlocked.Increment(ref _escapedRawValues);
                break;
        }
    }

    public void ReportFinal() =>
        SafeReport("final");

    private void MaybeReport()
    {
        long now = Stopwatch.GetTimestamp();
        long due = Volatile.Read(ref _nextReportTimestamp);
        if (now < due ||
            Interlocked.CompareExchange(ref _nextReportTimestamp, now + _intervalTicks, due) != due)
        {
            return;
        }

        SafeReport("cumulative");
    }

    private void SafeReport(string reason)
    {
        try
        {
            Report(reason);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or MdbxException)
        {
            if (_logger.IsWarn)
            {
                _logger.Warn($"Failed to report MDBX profile for {_path}: {exception.Message}");
            }
        }
    }

    private void Report(string reason)
    {
        long readTransactions = Volatile.Read(ref _readTransactions);
        long readTicks = Volatile.Read(ref _readTicks);
        long writeTransactions = Volatile.Read(ref _writeTransactions);
        long writeWaitTicks = Volatile.Read(ref _writeWaitTicks);
        long writeTicks = Volatile.Read(ref _writeTicks);
        long writeOperations = Volatile.Read(ref _writeOperations);
        long gets = Volatile.Read(ref _gets);
        long getHits = Volatile.Read(ref _getHits);
        long getKeyBytes = Volatile.Read(ref _getKeyBytes);
        long getValueBytes = Volatile.Read(ref _getValueBytes);
        long puts = Volatile.Read(ref _puts);
        long putKeyBytes = Volatile.Read(ref _putKeyBytes);
        long putValueBytes = Volatile.Read(ref _putValueBytes);
        long deletes = Volatile.Read(ref _deletes);
        long merges = Volatile.Read(ref _merges);
        long queuedWrites = Volatile.Read(ref _queuedWrites);
        long queuedKeyBytes = Volatile.Read(ref _queuedKeyBytes);
        long queuedValueBytes = Volatile.Read(ref _queuedValueBytes);
        long maxQueuedOperations = Volatile.Read(ref _maxQueuedOperations);
        long batchGroups = Volatile.Read(ref _batchGroups);
        long batchGroupBatches = Volatile.Read(ref _batchGroupBatches);
        long batchGroupOperations = Volatile.Read(ref _batchGroupOperations);
        long batchGroupBytes = Volatile.Read(ref _batchGroupBytes);
        long maxBatchGroupBatches = Volatile.Read(ref _maxBatchGroupBatches);
        long maxBatchGroupOperations = Volatile.Read(ref _maxBatchGroupOperations);
        long maxBatchGroupBytes = Volatile.Read(ref _maxBatchGroupBytes);
        long compressionAttempts = Volatile.Read(ref _compressionAttempts);
        long compressionRejected = Volatile.Read(ref _compressionRejected);
        long compressedValues = Volatile.Read(ref _compressedValues);
        long escapedRawValues = Volatile.Read(ref _escapedRawValues);
        long compressionInputBytes = Volatile.Read(ref _compressionInputBytes);
        long compressionStoredBytes = Volatile.Read(ref _compressionStoredBytes);

        using Process process = Process.GetCurrentProcess();
        long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        long workingSetBytes = process.WorkingSet64;
        long privateBytes = process.PrivateMemorySize64;
        MdbxStorageStats? storageStats = _statsProvider();
        string storageStatsText = storageStats is null ? string.Empty : FormatStorageStats(storageStats.Value);

        if (_logger.IsInfo)
        {
            _logger.Info(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"MDBX profile {reason} path={_path} readTx={readTransactions} avgReadUs={AverageMicroseconds(readTicks, readTransactions):F1} writeTx={writeTransactions} avgWriteMs={AverageMilliseconds(writeTicks, writeTransactions):F1} avgWriteWaitMs={AverageMilliseconds(writeWaitTicks, writeTransactions):F1} writeOps={writeOperations} avgOpsPerWriteTx={Average(writeOperations, writeTransactions):F1} batchGroups={batchGroups} avgBatchGroupBatches={Average(batchGroupBatches, batchGroups):F1} avgBatchGroupOps={Average(batchGroupOperations, batchGroups):F1} avgBatchGroupMiB={ToMiB(Average(batchGroupBytes, batchGroups)):F1} maxBatchGroupBatches={maxBatchGroupBatches} maxBatchGroupOps={maxBatchGroupOperations} maxBatchGroupMiB={ToMiB(maxBatchGroupBytes):F1} compressionAttempts={compressionAttempts} compressedValues={compressedValues} compressionRejected={compressionRejected} compressionRejectPct={Percent(compressionRejected, compressionAttempts):F1} escapedRawValues={escapedRawValues} compressionInputMiB={ToMiB(compressionInputBytes):F1} compressionStoredMiB={ToMiB(compressionStoredBytes):F1} compressionSavedMiB={ToMiB(Math.Max(0, compressionInputBytes - compressionStoredBytes)):F1} gets={gets} getHitPct={Percent(getHits, gets):F1} getKeyMiB={ToMiB(getKeyBytes):F1} getValueMiB={ToMiB(getValueBytes):F1} puts={puts} putKeyMiB={ToMiB(putKeyBytes):F1} putValueMiB={ToMiB(putValueBytes):F1} deletes={deletes} merges={merges} queuedWrites={queuedWrites} queuedKeyMiB={ToMiB(queuedKeyBytes):F1} queuedValueMiB={ToMiB(queuedValueBytes):F1} maxQueuedOps={maxQueuedOperations} managedMiB={ToMiB(managedBytes):F1} workingSetMiB={ToMiB(workingSetBytes):F1} privateMiB={ToMiB(privateBytes):F1}{storageStatsText}"));
        }
    }

    private static void UpdateMax(ref long location, long candidate)
    {
        long current;
        do
        {
            current = Volatile.Read(ref location);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref location, candidate, current) != current);
    }

    private static long ToTicks(TimeSpan value) =>
        value <= TimeSpan.Zero ? 0 : checked((long)(value.TotalSeconds * Stopwatch.Frequency));

    private bool ShouldSampleHotPath() =>
        _hotPathSampleRate <= 1 || unchecked(++t_hotPathSampleCounter % _hotPathSampleRate) == 0;

    private static double ToMicroseconds(long ticks) =>
        ticks * MicrosecondsPerSecond / Stopwatch.Frequency;

    private static double ToMilliseconds(long ticks) =>
        ticks * MillisecondsPerSecond / Stopwatch.Frequency;

    private static double AverageMicroseconds(long ticks, long count) =>
        count == 0 ? 0 : ToMicroseconds(ticks) / count;

    private static double AverageMilliseconds(long ticks, long count) =>
        count == 0 ? 0 : ToMilliseconds(ticks) / count;

    private static double Average(long value, long count) =>
        count == 0 ? 0 : (double)value / count;

    private static double Percent(long value, long count) =>
        count == 0 ? 0 : (double)value * 100 / count;

    private static double ToMiB(long bytes) =>
        bytes / BytesPerMiB;

    private static double ToMiB(double bytes) =>
        bytes / BytesPerMiB;

    private static double ToMiB(ulong bytes) =>
        bytes / BytesPerMiB;

    private static string FormatStorageStats(MdbxStorageStats stats) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $" pageSize={stats.PageSize} entries={stats.Entries} depth={stats.Depth} branchPages={stats.BranchPages} leafPages={stats.LeafPages} overflowPages={stats.OverflowPages} overflowPct={Percent((long)Math.Min(stats.OverflowPages, (ulong)long.MaxValue), (long)Math.Min(stats.TotalPages, (ulong)long.MaxValue)):F1} usedMiB={ToMiB(stats.UsedBytes):F1} modTxn={stats.ModTxnId}");
}

internal readonly record struct MdbxStorageStats(
    uint PageSize,
    uint Depth,
    ulong BranchPages,
    ulong LeafPages,
    ulong OverflowPages,
    ulong Entries,
    ulong ModTxnId)
{
    public ulong TotalPages => BranchPages + LeafPages + OverflowPages;

    public ulong UsedBytes => TotalPages * PageSize;
}
