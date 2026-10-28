// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Nethermind.Blockchain;

/// <summary>Bounded, process-wide path evidence for the diagnostic image only.</summary>
/// <remarks>
/// Counters are cumulative and individually atomic, not a transactional snapshot. Compare positive deltas
/// between timestamps strictly inside the measured interval; calls are not request frequency. Diagnostic
/// timings are not performance evidence. Native balance is meaningful only after callers are quiescent.
/// </remarks>
internal static class RpcLookupPaths
{
    internal enum Counter
    {
        BlockAttempts, BlockCache, BlockPending,
        BodyLt16KiB, Body16To64KiB, Body64To256KiB, BodyGe256KiB,
        NativeBodies, NativeAcquired, NativeReleased,
        ReceiptAttempts, ReceiptCache, ReceiptPending, ReceiptDeclined,
        ReceiptUnsupportedCompact, ReceiptMissingTarget, SelectedWithoutHashes, SelectedWithHashes
    }

    private static readonly string[] Names =
    [
        nameof(Counter.BlockAttempts), nameof(Counter.BlockCache), nameof(Counter.BlockPending),
        nameof(Counter.BodyLt16KiB), nameof(Counter.Body16To64KiB), nameof(Counter.Body64To256KiB), nameof(Counter.BodyGe256KiB),
        nameof(Counter.NativeBodies), nameof(Counter.NativeAcquired), nameof(Counter.NativeReleased),
        nameof(Counter.ReceiptAttempts), nameof(Counter.ReceiptCache), nameof(Counter.ReceiptPending), nameof(Counter.ReceiptDeclined),
        nameof(Counter.ReceiptUnsupportedCompact), nameof(Counter.ReceiptMissingTarget), nameof(Counter.SelectedWithoutHashes), nameof(Counter.SelectedWithHashes)
    ];
    private static readonly long[] Counts = new long[Names.Length];
    private static readonly Emitter Output;

    // Explicit initialization prevents timer construction from first occurring while a native lease is held.
    static RpcLookupPaths() => Output = new Emitter(Console.Out, TimeSpan.FromSeconds(1));

    internal static void Increment(Counter counter) => Interlocked.Increment(ref Counts[(int)counter]);

    internal static void RecordBody(int encodedLength, bool native)
    {
        Increment(encodedLength switch
        {
            < 16 * 1024 => Counter.BodyLt16KiB,
            < 64 * 1024 => Counter.Body16To64KiB,
            < 256 * 1024 => Counter.Body64To256KiB,
            _ => Counter.BodyGe256KiB
        });
        if (native) Increment(Counter.NativeBodies);
    }

    internal sealed class Emitter : IDisposable
    {
        internal const int MaxPeriodicRecords = 600;
        private readonly object _gate = new();
        private readonly TextWriter _output;
        private readonly Timer _timer;
        private int _records;
        private bool _disposed;

        internal Emitter(TextWriter output, TimeSpan dueTime)
        {
            _output = output;
            _timer = new Timer(_ => EmitPeriodic(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                _timer.Change(dueTime, TimeSpan.FromSeconds(1));
            }
            catch
            {
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _timer.Dispose();
                throw;
            }
        }

        private void OnProcessExit(object? sender, EventArgs args) => Dispose();

        internal void EmitPeriodic()
        {
            lock (_gate)
            {
                if (_disposed || _records >= MaxPeriodicRecords) return;
                WriteRecord(final: false);
                if (++_records == MaxPeriodicRecords) _timer.Dispose();
            }
        }

        private void WriteRecord(bool final)
        {
            StringBuilder line = new("RPC_LOOKUP_PATHS {\"utc\":\"");
            line.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            line.Append("\",\"final\":").Append(final ? "true" : "false");
            for (int i = 0; i < Counts.Length; i++)
            {
                line.Append(",\"").Append(Names[i]).Append("\":")
                    .Append(Interlocked.Read(ref Counts[i]).ToString(CultureInfo.InvariantCulture));
            }
            line.Append('}');
            _output.WriteLine(line.ToString());
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _timer.Dispose();
                WriteRecord(final: true);
            }
        }
    }
}
