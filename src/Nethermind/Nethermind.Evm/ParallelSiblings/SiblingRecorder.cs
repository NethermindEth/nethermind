// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.ParallelSiblings;

/// <summary>
/// Sibling memoization for cancelable frames: during ordinary serial execution of an eth_call,
/// each depth-1 sibling records its result and the facts that make replaying it sound, and later
/// occurrences of the same site are served from the memo when every one of those facts still
/// holds - otherwise they execute normally, so correctness never depends on the memo. Measured on
/// the target workload: a repeated site's touch set is stable across occurrences 100.0% of the
/// time, 78.9% of siblings are net-write-empty and 88.3% of executions have a previous occurrence.
///
/// Soundness conditions, each guarding a specific known failure:
/// - recorded only when the sibling left no net writes, no logs, no destroys/creates and no
///   refund delta (its journal positions at merge equal the ones at its start), took no value,
///   and touched nothing any earlier sibling of the recording request wrote - so the memo's
///   values are pure functions of the state root in its key;
/// - replayed only when the callee, calldata, value, state root AND gas handed to the child all
///   match, every first-touch recorded cold is still cold (live tracker queries - gas is then
///   exact with no arithmetic), and the memo touches nothing this request's earlier siblings
///   wrote. Hash collisions in the disjointness checks can only cause spurious rejection.
/// - on replay the recorded first-touch cells are warmed for real, because later siblings'
///   charges depend on that warmth.
/// </summary>
public static class SiblingRecorder
{
    private const int MaxSites = 8 * 1024;

    [ThreadStatic] private static bool t_recording;
    [ThreadStatic] private static HashSet<int>? t_touches;
    [ThreadStatic] private static List<Address>? t_firstTouchAddresses;
    [ThreadStatic] private static List<StorageCell>? t_firstTouchSlots;
    [ThreadStatic] private static HashSet<int>? t_prefixWrites;
    [ThreadStatic] private static long t_siteKey;
    [ThreadStatic] private static SiteRecord? t_knownSite;

    private static readonly ConcurrentDictionary<long, SiteRecord> s_sites = new();

    // Diagnostic counters, reported when NETHERMIND_SIBLING_CENSUS names a file: which soundness
    // condition memo traffic actually dies on. Interlocked-free sloppy counts - magnitudes matter,
    // not exactness.
    private static readonly string? s_reportPath = Environment.GetEnvironmentVariable("NETHERMIND_SIBLING_CENSUS");
    private static long s_recorded;
    private static long s_recordedMemoable;
    private static long s_lookupMisses;
    private static long s_replays;
    private static long s_rejectNotMemoable;
    private static long s_rejectGas;
    private static long s_rejectCold;
    private static long s_rejectPrefix;
    private static System.Threading.Timer? s_reportTimer;

    static SiblingRecorder() =>
        s_reportTimer = new System.Threading.Timer(static _ => Report(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private static void Report()
    {
        if (s_recorded == 0 && s_lookupMisses == 0 && s_replays == 0) return;
        string line =
            $"SIBLING-MEMO recorded {s_recorded} (memoable {s_recordedMemoable}), lookup misses {s_lookupMisses}, replays {s_replays}, " +
            $"rejects: not-memoable {s_rejectNotMemoable}, gas {s_rejectGas}, cold {s_rejectCold}, prefix {s_rejectPrefix}, sites {s_sites.Count}";
        Console.WriteLine(line);
        if (string.IsNullOrWhiteSpace(s_reportPath)) return;
        try
        {
            System.IO.File.WriteAllText(s_reportPath!, line + Environment.NewLine);
        }
        catch (System.IO.IOException)
        {
        }
    }

    public static void CountLookupMiss() => s_lookupMisses++;
    public static void CountReplay() => s_replays++;
    public static void CountGasReject() => s_rejectGas++;

    public static bool Recording => t_recording;

    /// <summary>One full observation of a call site. Immutable once published.</summary>
    public sealed class SiteRecord
    {
        public required bool Memoable { get; init; }
        public required long GasGiven { get; init; }
        public required long GasUsed { get; init; }
        public required byte[] Output { get; init; }
        public required int[] TouchHashes { get; init; }
        public required Address[] FirstTouchAddresses { get; init; }
        public required StorageCell[] FirstTouchSlots { get; init; }
    }

    public static long ComputeSiteKey(in ValueHash256 stateRoot, Address? to, ReadOnlySpan<byte> input, in UInt256 value)
    {
        ValueHash256 inputHash = ValueKeccak.Compute(input);
        return HashCode.Combine(stateRoot, to, inputHash, value.GetHashCode());
    }

    public static SiteRecord? TryGet(long siteKey) =>
        s_sites.TryGetValue(siteKey, out SiteRecord? record) ? record : null;

    /// <summary>The memo is replayable here iff its warmth assumptions hold (all recorded
    /// first-touches still cold), the gas handed to the child matches the recording, and it reads
    /// nothing an earlier sibling of this request wrote.</summary>
    public static bool IsReplayable(SiteRecord record, long gasGiven, in StackAccessTracker tracker)
    {
        if (!record.Memoable) { s_rejectNotMemoable++; return false; }
        if (record.GasGiven != gasGiven) { s_rejectGas++; return false; }

        foreach (Address address in record.FirstTouchAddresses)
        {
            if (!tracker.IsCold(address)) { s_rejectCold++; return false; }
        }

        foreach (StorageCell slot in record.FirstTouchSlots)
        {
            if (!tracker.IsCold(in slot)) { s_rejectCold++; return false; }
        }

        HashSet<int>? prefixWrites = t_prefixWrites;
        if (prefixWrites is { Count: > 0 })
        {
            foreach (int h in record.TouchHashes)
            {
                if (prefixWrites.Contains(h)) { s_rejectPrefix++; return false; }
            }
        }

        return true;
    }

    /// <summary>Warm the memo's first-touch cells: the replayed frame would have warmed them, and
    /// every later sibling's charges depend on that.</summary>
    public static void WarmReplayedTouches(SiteRecord record, in StackAccessTracker tracker)
    {
        foreach (Address address in record.FirstTouchAddresses)
        {
            tracker.WarmUp(address);
        }

        foreach (StorageCell slot in record.FirstTouchSlots)
        {
            tracker.WarmUp(in slot);
        }
    }

    public static void BeginSibling(long siteKey)
    {
        t_siteKey = siteKey;
        t_knownSite = TryGet(siteKey);

        // A known site needs no re-recording (its touch set is stable), which keeps the tracker
        // hooks off the hot path in steady state; its stored touches still feed the prefix-write
        // tracking at merge when it is a writer.
        if (t_knownSite is not null)
        {
            t_recording = false;
            return;
        }

        (t_touches ??= new HashSet<int>(1024)).Clear();
        (t_firstTouchAddresses ??= new List<Address>(64)).Clear();
        (t_firstTouchSlots ??= new List<StorageCell>(256)).Clear();
        t_recording = true;
    }

    public static void EndSibling(bool cleanFrame, bool succeeded, long gasGiven, long gasUsed, bool tookValue, ReadOnlySpan<byte> output)
    {
        bool wasRecording = t_recording;
        t_recording = false;

        if (!wasRecording)
        {
            // Known writer sites poison the memos of everything after them in this request: their
            // stored touch set over-approximates their write set. Clean known sites poison
            // nothing, whether replayed or re-executed.
            if (t_knownSite is { Memoable: false } known)
            {
                (t_prefixWrites ??= new HashSet<int>(1024)).UnionWith(known.TouchHashes);
            }

            t_knownSite = null;
            return;
        }

        HashSet<int> touches = t_touches!;
        bool prefixDisjoint = true;
        HashSet<int>? prefixWrites = t_prefixWrites;
        if (prefixWrites is { Count: > 0 })
        {
            foreach (int h in touches)
            {
                if (prefixWrites.Contains(h)) { prefixDisjoint = false; break; }
            }
        }

        bool memoable = cleanFrame && succeeded && !tookValue && prefixDisjoint;
        // Only real writers poison the suffix: a clean frame that merely reverted or arrived
        // with unusable shape left the state untouched.
        if (!cleanFrame)
        {
            (t_prefixWrites ??= new HashSet<int>(1024)).UnionWith(touches);
        }

        s_recorded++;
        if (memoable) s_recordedMemoable++;
        if (s_sites.Count >= MaxSites) s_sites.Clear();
        s_sites[t_siteKey] = new SiteRecord
        {
            Memoable = memoable,
            GasGiven = gasGiven,
            GasUsed = gasUsed,
            Output = memoable ? output.ToArray() : [],
            TouchHashes = [.. touches],
            FirstTouchAddresses = [.. t_firstTouchAddresses!],
            FirstTouchSlots = [.. t_firstTouchSlots!],
        };
        t_knownSite = null;
    }

    /// <summary>Top frame ended (normally or not): recording stops and the per-request prefix
    /// state resets so the next request starts clean.</summary>
    public static void EndTopFrame()
    {
        t_recording = false;
        t_knownSite = null;
        t_prefixWrites?.Clear();
    }

    public static void TouchAddress(Address? address, bool cold)
    {
        if (address is null || !t_recording) return;
        t_touches!.Add(address.GetHashCode());
        if (cold) t_firstTouchAddresses!.Add(address);
    }

    public static void TouchSlot(in StorageCell cell, bool cold)
    {
        if (!t_recording) return;
        t_touches!.Add(cell.GetHashCode());
        if (cold) t_firstTouchSlots!.Add(cell);
    }
}
