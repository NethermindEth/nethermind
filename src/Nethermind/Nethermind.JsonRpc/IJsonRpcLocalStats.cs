// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.JsonRpc;

public class MethodStats
{
    private long _successes;
    private long _errors;
    private long _totalTimeOfSuccessesMicros;
    private long _totalTimeOfErrorsMicros;
    private long _maxTimeOfSuccess;
    private long _maxTimeOfError;
    private long _totalSizeBytes;

    public long Successes { get => Volatile.Read(ref _successes); set => Volatile.Write(ref _successes, value); }

    public long Errors { get => Volatile.Read(ref _errors); set => Volatile.Write(ref _errors, value); }

    public long TotalTimeOfSuccessesMicros { get => Volatile.Read(ref _totalTimeOfSuccessesMicros); set => Volatile.Write(ref _totalTimeOfSuccessesMicros, value); }

    public long TotalTimeOfErrorsMicros { get => Volatile.Read(ref _totalTimeOfErrorsMicros); set => Volatile.Write(ref _totalTimeOfErrorsMicros, value); }

    public decimal AvgTimeOfErrors => Average(TotalTimeOfErrorsMicros, Errors);

    public decimal AvgTimeOfSuccesses => Average(TotalTimeOfSuccessesMicros, Successes);

    public long MaxTimeOfError { get => Volatile.Read(ref _maxTimeOfError); set => Volatile.Write(ref _maxTimeOfError, value); }

    public long MaxTimeOfSuccess { get => Volatile.Read(ref _maxTimeOfSuccess); set => Volatile.Write(ref _maxTimeOfSuccess, value); }

    public long TotalSizeBytes { get => Volatile.Read(ref _totalSizeBytes); set => Volatile.Write(ref _totalSizeBytes, value); }

    public decimal TotalSize => TotalSizeBytes;

    public decimal AvgSize => Average(TotalSizeBytes, Calls);

    public long Calls => Successes + Errors;

    internal void Record(long handlingTimeMicroseconds, long size, bool success)
    {
        if (success)
        {
            Interlocked.Increment(ref _successes);
            Interlocked.Add(ref _totalTimeOfSuccessesMicros, handlingTimeMicroseconds);
            SetMax(ref _maxTimeOfSuccess, handlingTimeMicroseconds);
        }
        else
        {
            Interlocked.Increment(ref _errors);
            Interlocked.Add(ref _totalTimeOfErrorsMicros, handlingTimeMicroseconds);
            SetMax(ref _maxTimeOfError, handlingTimeMicroseconds);
        }

        Interlocked.Add(ref _totalSizeBytes, size);
    }

    internal MethodStats Snapshot() =>
        new()
        {
            Successes = Successes,
            Errors = Errors,
            TotalTimeOfSuccessesMicros = TotalTimeOfSuccessesMicros,
            TotalTimeOfErrorsMicros = TotalTimeOfErrorsMicros,
            MaxTimeOfSuccess = MaxTimeOfSuccess,
            MaxTimeOfError = MaxTimeOfError,
            TotalSizeBytes = TotalSizeBytes
        };

    private static void SetMax(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value > current)
        {
            long previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private static decimal Average(long total, long count) => count == 0 ? 0 : (decimal)total / count;
}

public interface IJsonRpcLocalStats
{
    bool IsEnabled { get; }

    void ReportCall(RpcReport report, long elapsedMicroseconds = 0, long? size = null);

    MethodStats GetMethodStats(string methodName);
}
