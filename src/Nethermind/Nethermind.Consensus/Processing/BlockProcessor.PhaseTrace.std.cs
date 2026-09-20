// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    private readonly bool _phaseTraceEnabled = Environment.GetEnvironmentVariable("NETHERMIND_PROCESSING_PHASE_TRACE") == "1";
    private ProcessingPhaseTrace? _phaseTrace;

    partial void BeginPhaseTrace(ref IDisposable? scope, Block block, IReleaseSpec spec, ProcessingOptions options)
    {
        if (_phaseTraceEnabled && _logger.IsInfo)
        {
            scope = _phaseTrace = new ProcessingPhaseTrace(this, block, spec, options);
        }
    }

    partial void MarkProcessingPhase(string name) => _phaseTrace?.Mark(name);
    partial void CompletePhaseTrace() => _phaseTrace?.Complete();

    // Capture on the processing thread; serialization and log delivery are outside the recorded windows.
    private sealed class ProcessingPhaseTrace : IDisposable
    {
        private const int LinuxClockMonotonic = 1;
        private readonly BlockProcessor _owner;
        private readonly ulong _number;
        private readonly string? _hash;
        private readonly ProcessingOptions _options;
        private readonly bool _balEnabled;
        private readonly bool _hasBal;
        private readonly bool _mainProcessing;
        private readonly long _calibrationBefore;
        private readonly long _calibrationAfter;
        private readonly long _utcTicks;
        private readonly long _monotonicNanoseconds;
        private readonly List<PhasePoint> _points = new(8);
        private bool _completed;

        internal ProcessingPhaseTrace(BlockProcessor owner, Block block, IReleaseSpec spec, ProcessingOptions options)
        {
            _owner = owner;
            _number = block.Number;
            _hash = block.Hash?.ToString();
            _options = options;
            _balEnabled = spec.BlockLevelAccessListsEnabled;
            _hasBal = block.BlockAccessList is not null;
            _mainProcessing = BlockchainProcessor.IsMainProcessingThread;
            _calibrationBefore = Stopwatch.GetTimestamp();
            _utcTicks = DateTime.UtcNow.Ticks;
            _monotonicNanoseconds = OperatingSystem.IsLinux() && ClockGetTime(LinuxClockMonotonic, out Timespec time) == 0
                ? checked(time.Seconds * 1_000_000_000 + time.Nanoseconds)
                : -1;
            _calibrationAfter = Stopwatch.GetTimestamp();
            Mark("process_one_start");
        }

        internal void Mark(string name) => _points.Add(new PhasePoint(name, Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId, OperatingSystem.IsLinux() ? GetTid() : -1));

        internal void Complete() => _completed = true;

        public void Dispose()
        {
            Mark("process_one_end");
            _owner._phaseTrace = null;
            ArrayBufferWriter<byte> buffer = new(2048);
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("msg", "Processing phases");
                writer.WriteNumber("block_number", _number);
                writer.WriteString("block_hash", _hash);
                writer.WriteNumber("processing_options", (int)_options);
                writer.WriteBoolean("main_processing", _mainProcessing);
                writer.WriteBoolean("bal_enabled", _balEnabled);
                writer.WriteBoolean("has_bal", _hasBal);
                writer.WriteBoolean("completed", _completed);
                writer.WriteNumber("process_id", Environment.ProcessId);
                writer.WriteNumber("stopwatch_frequency", Stopwatch.Frequency);
                writer.WriteNumber("calibration_before_ticks", _calibrationBefore);
                writer.WriteNumber("calibration_after_ticks", _calibrationAfter);
                writer.WriteNumber("utc_ticks", _utcTicks);
                writer.WriteNumber("linux_monotonic_ns", _monotonicNanoseconds);
                writer.WriteStartArray("points");
                foreach (PhasePoint point in _points)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", point.Name);
                    writer.WriteNumber("ticks", point.Ticks);
                    writer.WriteNumber("managed_thread_id", point.ManagedThreadId);
                    writer.WriteNumber("os_thread_id", point.OsThreadId);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            _owner._logger.Info(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }

        private readonly record struct PhasePoint(string Name, long Ticks, int ManagedThreadId, int OsThreadId);

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public long Seconds;
            public long Nanoseconds;
        }

        [DllImport("libc", EntryPoint = "clock_gettime")]
        private static extern int ClockGetTime(int clockId, out Timespec time);

        [DllImport("libc", EntryPoint = "gettid")]
        private static extern int GetTid();
    }
}
