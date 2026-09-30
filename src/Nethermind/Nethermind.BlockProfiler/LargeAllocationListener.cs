// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.BlockProfiler;

/// <summary>
/// <c>NETHERMIND_COUNT_ALLOC_TRACE=1</c>: logs every allocation of 1 MB or more the runtime samples (GC allocation
/// ticks), with its type, size, the allocating OS thread and the block being processed, so an allocation that lands in
/// one run's block and not another's can be named.
/// </summary>
internal sealed class LargeAllocationListener : EventListener
{
    private const long MinimumSize = 1 << 20;
    private const int GCAllocationTickEventId = 10;
    private const EventKeywords GCKeyword = (EventKeywords)0x1;

    private static LargeAllocationListener? s_instance;
    private static int s_processingThreadId;

    // Block starts by time: events arrive on the dispatch thread later, so each is matched to the block that had
    // started by its timestamp. Written by the processing thread only.
    private const int StartsLength = 4096;
    private static readonly (long Block, long Ticks)[] s_starts = new (long, long)[StartsLength];
    private static int s_startCount;

    private readonly ILogger _logger;
    private readonly ConcurrentQueue<string> _lines = new();

    private LargeAllocationListener(ILogger logger) => _logger = logger;

    private static int s_started;

    public static void StartIfEnabled(ILogManager logManager)
    {
        // Branch processors are scoped, so this runs more than once; a listener starts receiving events when built.
        if (Environment.GetEnvironmentVariable("NETHERMIND_COUNT_ALLOC_TRACE") != "1" || Interlocked.Exchange(ref s_started, 1) != 0) return;
        LargeAllocationListener listener = new(logManager.GetClassLogger<LargeAllocationListener>());
        Volatile.Write(ref s_instance, listener);
        if (listener._logger.IsInfo) listener._logger.Info("EXPB-COUNT allocation trace on: allocations of 1 MB or more");
    }

    /// <summary>Marks the block the processing thread works on, for the lines logged meanwhile.</summary>
    public static void SetBlock(ulong number, int processingOsThreadId)
    {
        if (s_instance is null) return;
        Volatile.Write(ref s_processingThreadId, processingOsThreadId);
        int count = s_startCount;
        s_starts[count % StartsLength] = ((long)number, DateTime.UtcNow.Ticks);
        Volatile.Write(ref s_startCount, count + 1);
    }

    private static long BlockAt(long ticks)
    {
        int count = Volatile.Read(ref s_startCount);
        for (int i = count - 1; i >= 0 && i > count - StartsLength; i--)
        {
            (long block, long startTicks) = s_starts[i % StartsLength];
            if (startTicks <= ticks) return block;
        }
        return -1;
    }

    /// <summary>Writes the lines collected so far; called outside the counted windows.</summary>
    public static void Flush()
    {
        LargeAllocationListener? listener = s_instance;
        if (listener is null) return;
        while (listener._lines.TryDequeue(out string? line))
        {
            if (listener._logger.IsInfo) listener._logger.Info(line);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, EventLevel.Verbose, GCKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId != GCAllocationTickEventId || eventData.Payload is null || eventData.PayloadNames is null) return;
        long size = 0;
        string type = "?";
        int kind = -1;
        for (int i = 0; i < eventData.PayloadNames.Count; i++)
        {
            switch (eventData.PayloadNames[i])
            {
                case "ObjectSize":
                    size = Convert.ToInt64(eventData.Payload[i]);
                    break;
                case "TypeName":
                    type = eventData.Payload[i] as string ?? "?";
                    break;
                case "AllocationKind":
                    kind = Convert.ToInt32(eventData.Payload[i]);
                    break;
            }
        }
        if (size < MinimumSize) return;
        long osThread = eventData.OSThreadId;
        bool processing = osThread == Volatile.Read(ref s_processingThreadId);
        _lines.Enqueue($"EXPB-ALLOC block={BlockAt(eventData.TimeStamp.ToUniversalTime().Ticks)} size={size} kind={kind} processing={(processing ? 1 : 0)} thread={osThread} type={type}");
    }
}
