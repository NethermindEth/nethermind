// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// DIAGNOSTIC-ONLY recorder that correlates speculative prewarming against main-thread
/// execution without perturbing their natural ordering.
/// </summary>
/// <remarks>
/// Synchronous per-tx logging would force the parallel prewarmer threads and the single
/// execution thread to contend on one logger lock, serializing them and masking the real
/// order. Instead, each event captures a process-global monotonic timestamp
/// (<see cref="Stopwatch.GetTimestamp"/>, comparable across threads) and is enqueued
/// lock-free; string formatting and I/O are deferred to <see cref="Flush"/>, which runs on
/// a background thread between blocks. The per-event hot-path cost is therefore a timestamp
/// read plus a lock-free enqueue — no shared logger lock — so the captured timestamps
/// reflect the true emission order once sorted offline.
/// </remarks>
internal static class PrewarmDiag
{
    public const byte KindExec = 0;
    public const byte KindPrewarm = 1;

    private static readonly double s_ticksToMicros = 1_000_000.0 / Stopwatch.Frequency;
    private static readonly ConcurrentQueue<Event> s_events = new();

    private readonly struct Event(long ts, byte kind, long block, int tx, int thread, int group, int seq)
    {
        public readonly long Ts = ts;
        public readonly byte Kind = kind;
        public readonly long Block = block;
        public readonly int Tx = tx;
        public readonly int Thread = thread;
        public readonly int Group = group;
        public readonly int Seq = seq;
    }

    /// <summary>Capture-time-stamps and queues a single event. Cheap and lock-free; safe from any thread.</summary>
    public static void Record(byte kind, long block, int tx, int group = -1, int seq = -1)
    {
        // Timestamp first so the recorded order reflects when the event actually happened,
        // independent of any enqueue jitter.
        long ts = Stopwatch.GetTimestamp();
        s_events.Enqueue(new Event(ts, kind, block, tx, Environment.CurrentManagedThreadId, group, seq));
    }

    /// <summary>Drains everything queued so far, orders by capture time, and emits one INFO line per event.</summary>
    public static void Flush(ILogger logger)
    {
        if (!logger.IsInfo)
        {
            while (s_events.TryDequeue(out _)) { }
            return;
        }

        List<Event> batch = new(1024);
        while (s_events.TryDequeue(out Event e)) batch.Add(e);
        if (batch.Count == 0) return;

        batch.Sort(static (a, b) => a.Ts.CompareTo(b.Ts));
        foreach (Event e in batch)
        {
            long tsUs = (long)(e.Ts * s_ticksToMicros);
            logger.Info(e.Kind == KindExec
                ? $"Starting tx execution number {e.Tx} in block {e.Block} t_us={tsUs} thr={e.Thread}"
                : $"Starting prewarming tx number {e.Tx} in block {e.Block} t_us={tsUs} thr={e.Thread} group={e.Group} seq={e.Seq}");
        }
    }
}
