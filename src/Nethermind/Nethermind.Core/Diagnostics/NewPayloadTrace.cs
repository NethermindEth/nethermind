// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Nethermind.Core.Diagnostics;

/// <summary>
/// Diagnostic timeline of one engine_newPayload request, from the HTTP request to the block's commit, enabled by
/// NETHERMIND_NP_TRACE=1. Requests are assumed one at a time (a benchmark driver); each is printed as one line
/// when the next newPayload starts, so the commit that follows its answer is in it too.
/// </summary>
public static class NewPayloadTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_NP_TRACE") == "1";

    public const int HttpStart = 0, BodyRead = 1, MethodEntry = 2, Locked = 3, GcRegion = 4, HandleStart = 5, Decoded = 6,
        PreSuggest = 7, Suggested = 8, EnqueueStart = 9, Dequeued = 10, BranchStart = 11, ProcessOneStart = 12, TxsDone = 13,
        ProcessOneEnd = 14, Verdict = 15, HandlerResumed = 16, HandleEnd = 17, ResponseDone = 18, CommitDone = 19, BranchEnd = 20;

    private const int Count = 21;
    private static readonly string[] Names =
    [
        "http", "body", "entry", "locked", "gcregion", "handle", "decoded", "presuggest", "suggested", "enqueue", "dequeued",
        "branch", "p1start", "txsdone", "p1end", "verdict", "resumed", "handleend", "response", "commit", "branchend"
    ];

    private sealed class Record
    {
        public readonly long[] Stamps = new long[Count];
        public long Block = -1;
    }

    private static readonly AsyncLocal<Record?> s_request = new();
    private static Record? s_active;

    public static void BeginRequest()
    {
        if (!Enabled) return;
        Record record = new();
        record.Stamps[HttpStart] = Stopwatch.GetTimestamp();
        s_request.Value = record;
    }

    /// <summary>Stamps the request this async flow belongs to, whatever its method.</summary>
    public static void StampRequest(int point)
    {
        if (Enabled && s_request.Value is { } record && record.Stamps[point] == 0) record.Stamps[point] = Stopwatch.GetTimestamp();
    }

    /// <summary>Makes this flow's request the active newPayload and prints the previous one.</summary>
    public static void BeginNewPayload()
    {
        if (!Enabled) return;
        Record record = s_request.Value ?? new Record();
        record.Stamps[MethodEntry] = Stopwatch.GetTimestamp();
        Record? previous = Interlocked.Exchange(ref s_active, record);
        // Off the request path: the next payload's latency must not include the previous one's line.
        if (previous is not null) ThreadPool.UnsafeQueueUserWorkItem(static r => Print(r), previous, preferLocal: false);
    }

    public static void SetBlock(long number)
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record) record.Block = number;
    }

    /// <summary>Stamps the active newPayload; only the first stamp of a point counts.</summary>
    public static void Stamp(int point)
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record && record.Stamps[point] == 0) record.Stamps[point] = Stopwatch.GetTimestamp();
    }

    private static void Print(Record record)
    {
        long start = record.Stamps[HttpStart] != 0 ? record.Stamps[HttpStart] : record.Stamps[MethodEntry];
        StringBuilder line = new("NP-TRACE block=");
        line.Append(record.Block);
        for (int i = 1; i < Count; i++)
        {
            long stamp = record.Stamps[i];
            line.Append(' ').Append(Names[i]).Append('=');
            if (stamp == 0) line.Append("na");
            else line.Append(((stamp - start) * 1_000_000 / Stopwatch.Frequency).ToString());
        }

        Console.Out.WriteLine(line.ToString());
    }
}
