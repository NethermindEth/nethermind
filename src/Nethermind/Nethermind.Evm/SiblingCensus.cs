// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;

/// <summary>
/// Diagnostic census sizing speculative parallel execution of depth-1 sibling subcalls, gathered
/// during ordinary serial execution. A speculative sibling is validated by its EIP-2929
/// assumptions: it runs against the transaction-initial warm sets, so its memo survives exactly
/// when nothing it touches was first-warmed by an earlier sibling - otherwise its recorded gas
/// charges cold where reality is warm and the frame must re-execute. That condition is fully
/// computable from a serial run: sibling k is wave-one viable iff its touch set is disjoint from
/// the union of first-touches of siblings 1..k-1. Touches are captured at the two access-tracker
/// funnels every 2929 charge consults, only while a depth-1 frame under a top-level frame is
/// executing, and only when NETHERMIND_SIBLING_CENSUS is set - the static flag keeps the
/// disabled build's hot paths untouched. Execution is single-threaded per frame, so the
/// per-frame state is thread-static and needs no synchronization; totals are merged on flush.
/// </summary>
public static class SiblingCensus
{
    private static readonly string? s_path = Environment.GetEnvironmentVariable("NETHERMIND_SIBLING_CENSUS");
    public static readonly bool IsEnabled = !string.IsNullOrWhiteSpace(s_path);

    [ThreadStatic] private static bool t_recording;
    [ThreadStatic] private static List<(HashSet<int> Touches, HashSet<int> FirstTouches, bool Reverted, bool Clean, long SiteKey)>? t_siblings;
    [ThreadStatic] private static long t_currentSiteKey;

    /// <summary>Last observed touch fingerprints per call site, for the wave-two question: does
    /// the same site touch the same cells on the next request. Order-independent fingerprint, so
    /// internal ordering differences do not read as instability.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long TouchFp, int TouchCount)> s_predictions = new();
    [ThreadStatic] private static HashSet<int>? t_currentTouches;
    [ThreadStatic] private static HashSet<int>? t_currentFirstTouches;

    // Buckets by sibling count per frame: the bench mixes warmup traffic (few siblings) with the
    // measured multicalls (dozens), and an aggregate average hides the shape that matters.
    private static readonly long[] s_bucketFrames = new long[4];
    private static readonly long[] s_bucketSiblings = new long[4];
    private static readonly long[] s_bucketReverted = new long[4];
    private static readonly long[] s_bucketViable = new long[4];
    private static readonly long[] s_bucketClean = new long[4];
    private static readonly long[] s_bucketMemoable = new long[4];
    private static readonly long[] s_bucketPredicted = new long[4];
    private static readonly long[] s_bucketPredictionHits = new long[4];
    private static readonly long[] s_bucketTouches = new long[4];
    private static Timer? s_timer;
    private static int s_timerStarted;

    private static int BucketOf(int siblingCount) => siblingCount switch
    {
        <= 4 => 0,
        <= 16 => 1,
        <= 48 => 2,
        _ => 3,
    };

    public static bool Recording => t_recording;

    public static void BeginSibling(Address? to, ReadOnlyMemory<byte> input, in Nethermind.Int256.UInt256 value)
    {
        ValueHash256 inputHash = ValueKeccak.Compute(input.Span);
        long key = System.HashCode.Combine(to, inputHash, value.GetHashCode());
        t_currentSiteKey = key;
        t_siblings ??= new List<(HashSet<int>, HashSet<int>, bool, bool, long)>(64);
        t_currentTouches = new HashSet<int>(128);
        t_currentFirstTouches = new HashSet<int>(64);
        t_recording = true;
    }

    public static void EndSibling(bool reverted, bool netWriteEmpty)
    {
        t_recording = false;
        if (t_currentTouches is null || t_currentFirstTouches is null) return;
        t_siblings!.Add((t_currentTouches, t_currentFirstTouches, reverted, netWriteEmpty, t_currentSiteKey));
        t_currentTouches = null;
        t_currentFirstTouches = null;
    }

    public static void TouchAddress(Address? address, bool cold)
    {
        if (address is null) return;
        int hash = address.GetHashCode();
        t_currentTouches?.Add(hash);
        if (cold) t_currentFirstTouches?.Add(hash);
    }

    public static void TouchSlot(in StorageCell cell, bool cold)
    {
        int hash = cell.GetHashCode();
        t_currentTouches?.Add(hash);
        if (cold) t_currentFirstTouches?.Add(hash);
    }

    /// <summary>Top frame completed: run the wave-one validation replay over the recorded
    /// siblings and fold the numbers into the process totals.</summary>
    public static void EndTopFrame()
    {
        t_recording = false;
        t_currentTouches = null;
        t_currentFirstTouches = null;
        List<(HashSet<int> Touches, HashSet<int> FirstTouches, bool Reverted, bool Clean, long SiteKey)>? siblings = t_siblings;
        if (Interlocked.CompareExchange(ref s_timerStarted, 1, 0) == 0) StartTimer();
        if (siblings is null || siblings.Count == 0)
        {
            Interlocked.Increment(ref s_bucketFrames[0]);
            return;
        }

        HashSet<int> warmedByPrefix = new(256);
        long viable = 0;
        long touches = 0;
        long reverts = 0;
        long clean = 0;
        long memoable = 0;
        long predicted = 0;
        long predictionHits = 0;
        foreach ((HashSet<int> touched, HashSet<int> firstTouched, bool reverted, bool netWriteEmpty, long siteKey) in siblings)
        {
            long fp = 0;
            foreach (int h in touched) fp += unchecked((long)((ulong)(uint)h * 0x9E3779B97F4A7C15UL));
            if (s_predictions.TryGetValue(siteKey, out (long TouchFp, int TouchCount) prev))
            {
                predicted++;
                if (prev.TouchFp == fp && prev.TouchCount == touched.Count) predictionHits++;
            }

            s_predictions[siteKey] = (fp, touched.Count);

            bool overlaps = false;
            foreach (int h in touched)
            {
                if (warmedByPrefix.Contains(h)) { overlaps = true; break; }
            }

            if (!overlaps) viable++;
            if (reverted) reverts++;
            if (netWriteEmpty) clean++;
            if (netWriteEmpty && !overlaps) memoable++;
            touches += touched.Count;
            warmedByPrefix.UnionWith(firstTouched);
        }

        int bucket = BucketOf(siblings.Count);
        Interlocked.Increment(ref s_bucketFrames[bucket]);
        Interlocked.Add(ref s_bucketSiblings[bucket], siblings.Count);
        Interlocked.Add(ref s_bucketReverted[bucket], reverts);
        Interlocked.Add(ref s_bucketViable[bucket], viable);
        Interlocked.Add(ref s_bucketClean[bucket], clean);
        Interlocked.Add(ref s_bucketMemoable[bucket], memoable);
        Interlocked.Add(ref s_bucketPredicted[bucket], predicted);
        Interlocked.Add(ref s_bucketPredictionHits[bucket], predictionHits);
        Interlocked.Add(ref s_bucketTouches[bucket], touches);
        siblings.Clear();
    }

    private static void StartTimer()
    {
        s_timer = new Timer(static _ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
    }

    private static void Flush()
    {
        string[] labels = ["1-4 siblings", "5-16 siblings", "17-48 siblings", "49+ siblings"];
        StringBuilder report = new();
        report.AppendLine("sibling census (serial execution replay of wave-one speculation validity)");
        for (int i = 0; i < 4; i++)
        {
            long frames = Interlocked.Read(ref s_bucketFrames[i]);
            long siblings = Interlocked.Read(ref s_bucketSiblings[i]);
            long reverted = Interlocked.Read(ref s_bucketReverted[i]);
            long viable = Interlocked.Read(ref s_bucketViable[i]);
            long clean = Interlocked.Read(ref s_bucketClean[i]);
            long memoable = Interlocked.Read(ref s_bucketMemoable[i]);
            long predicted = Interlocked.Read(ref s_bucketPredicted[i]);
            long predictionHits = Interlocked.Read(ref s_bucketPredictionHits[i]);
            long touches = Interlocked.Read(ref s_bucketTouches[i]);
            report.AppendLine($"[{labels[i]}] frames {frames}, siblings {siblings}"
                + (frames > 0 ? $", per frame {(double)siblings / Math.Max(frames, 1):F2}" : "")
                + (siblings > 0
                    ? $", reverted {100.0 * reverted / siblings:F2}%, wave-one viable {100.0 * viable / siblings:F2}%, net-write-empty {100.0 * clean / siblings:F2}%, wave-one memoable {100.0 * memoable / siblings:F2}%, site seen before {100.0 * predicted / siblings:F2}%, touch-set stable {(predicted > 0 ? 100.0 * predictionHits / predicted : 0):F2}%, touches/sibling {(double)touches / siblings:F1}"
                    : ""));
        }

        try
        {
            File.WriteAllText(s_path!, report.ToString());
        }
        catch (IOException)
        {
        }
    }
}
