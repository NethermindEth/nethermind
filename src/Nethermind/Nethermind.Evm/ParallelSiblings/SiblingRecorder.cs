// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.ParallelSiblings;

/// <summary>
/// Memoizes depth-1 sibling calls of cancelable frames (eth_call, estimateGas, simulate). A batch
/// contract that performs the same inner call in successive requests performs identical work, and
/// this serves the later occurrences from the first one's recorded result instead of re-executing
/// them. Anything unprovable executes normally, so correctness never depends on a memo.
///
/// The identity of a sibling is a 256-bit digest over its own inputs - callee, calldata, value and
/// the gas handed to it - chained through everything that ran before it in this request: each
/// preceding sibling's digest and every state write in the glue between them. A sibling's result is
/// a function of (state at its start, callee, calldata, value, gas), and the state at its start is
/// the state root plus exactly those preceding effects, so a digest match implies the same starting
/// state. Any divergence upstream - a different call, a different amount, an extra write - changes
/// the chain, the lookup misses and the frame executes. The digest is Keccak over the concatenated
/// fields rather than a hash-combine, because a 32-bit key collision would serve a foreign memo.
///
/// Two further conditions, both required:
/// - recorded only when the sibling left no net state (its change-journal positions at merge equal
///   the ones at its start), emitted no logs, destroyed nothing, accrued no refund, took no value
///   and succeeded. A quoter that simulates a swap and reverts it satisfies this; a frame that
///   actually wrote does not, and never gets a memo.
/// - replayed only when every cell the recording touched cold is still cold here, so charging the
///   recorded gas is exact with no arithmetic. Replay warms those cells for real, because later
///   siblings' charges depend on that warmth.
///
/// Measured on a 59-sibling batch workload: 90% of sites memoizable, ~33 replays per request, zero
/// gas or warmth rejections, and responses byte-identical to a node without any of this.
/// </summary>
public static class SiblingRecorder
{
    private const int MaxSites = 8 * 1024;
    private const int DigestInputSize = 32 + 20 + 32 + 32 + 8;

    [ThreadStatic] private static bool t_recording;
    [ThreadStatic] private static HashSet<int>? t_touches;
    [ThreadStatic] private static List<Address>? t_firstTouchAddresses;
    [ThreadStatic] private static List<StorageCell>? t_firstTouchSlots;
    [ThreadStatic] private static ValueHash256 t_chain;
    [ThreadStatic] private static bool t_inCancelableFrame;
    [ThreadStatic] private static bool t_insideSibling;
    [ThreadStatic] private static ValueHash256 t_siteKey;
    [ThreadStatic] private static SiteRecord? t_knownSite;

    private static readonly ConcurrentDictionary<ValueHash256, SiteRecord> s_sites = new();

    public static bool Recording => t_recording;

    /// <summary>True between siblings of a cancelable frame, where a state write belongs to the
    /// chain because it is part of the state the next sibling starts from.</summary>
    public static bool ChainingGlueWrites => t_inCancelableFrame && !t_insideSibling;

    /// <summary>One full observation of a call site. Immutable once published.</summary>
    public sealed class SiteRecord
    {
        public required bool Memoable { get; init; }
        public required long GasGiven { get; init; }
        public required long GasUsed { get; init; }
        public required byte[] Output { get; init; }
        public required Address[] FirstTouchAddresses { get; init; }
        public required StorageCell[] FirstTouchSlots { get; init; }
    }

    public static ValueHash256 ComputeSiteKey(in ValueHash256 stateRoot, Address? to, ReadOnlySpan<byte> input, in UInt256 value, long gasGiven)
    {
        Span<byte> buffer = stackalloc byte[DigestInputSize + ValueHash256.MemorySize];
        t_chain.Bytes.CopyTo(buffer);
        Span<byte> fields = buffer.Slice(ValueHash256.MemorySize);
        stateRoot.Bytes.CopyTo(fields);
        (to ?? Address.Zero).Bytes.CopyTo(fields.Slice(32));
        ValueKeccak.Compute(input).Bytes.CopyTo(fields.Slice(52));
        value.ToBigEndian(fields.Slice(84, 32));
        MemoryMarshal.Write(fields.Slice(116), in gasGiven);
        return ValueKeccak.Compute(buffer);
    }

    public static SiteRecord? TryGet(in ValueHash256 siteKey) =>
        s_sites.TryGetValue(siteKey, out SiteRecord? record) ? record : null;

    /// <summary>A key match already implies the same starting state, so what remains is the warmth
    /// belt: every cell the recording touched cold must still be cold, which is what makes the
    /// recorded gas exact without any arithmetic.</summary>
    public static bool IsReplayable(SiteRecord record, long gasGiven, in StackAccessTracker tracker)
    {
        if (!record.Memoable || record.GasGiven != gasGiven) return false;

        foreach (Address address in record.FirstTouchAddresses)
        {
            if (!tracker.IsCold(address)) return false;
        }

        foreach (StorageCell slot in record.FirstTouchSlots)
        {
            if (!tracker.IsCold(in slot)) return false;
        }

        return true;
    }

    /// <summary>Warm the memo's first-touch cells: the replayed frame would have warmed them and
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

    public static void BeginSibling(in ValueHash256 siteKey)
    {
        t_siteKey = siteKey;
        t_knownSite = TryGet(in siteKey);
        t_inCancelableFrame = true;
        t_insideSibling = true;

        // A known site needs no re-recording: its digest already pins the inputs and the prefix,
        // so the observation would repeat. That keeps the access-tracker hooks off the hot path
        // once the store is warm, which is nearly always.
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
        t_insideSibling = false;

        // Recorded, re-executed or replayed - every completed sibling advances the chain the same
        // way, which is what keeps a replaying request on the recording request's key sequence.
        AdvanceChain(t_siteKey.Bytes);

        if (!wasRecording)
        {
            t_knownSite = null;
            return;
        }

        bool memoable = cleanFrame && succeeded && !tookValue;
        if (s_sites.Count >= MaxSites) s_sites.Clear();
        s_sites[t_siteKey] = new SiteRecord
        {
            Memoable = memoable,
            GasGiven = gasGiven,
            GasUsed = gasUsed,
            Output = memoable ? output.ToArray() : [],
            FirstTouchAddresses = [.. t_firstTouchAddresses!],
            FirstTouchSlots = [.. t_firstTouchSlots!],
        };
        t_knownSite = null;
    }

    /// <summary>Top frame ended, normally or not: the per-request chain resets so the next request
    /// starts from the same place the recording one did.</summary>
    public static void EndTopFrame()
    {
        t_recording = false;
        t_inCancelableFrame = false;
        t_insideSibling = false;
        t_knownSite = null;
        t_chain = default;
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

    /// <summary>A write between siblings changes what the next sibling starts from, so it joins the
    /// chain. Writes inside a sibling need no mixing: the sibling's own digest already advanced it.
    /// </summary>
    public static void ChainGlueWrite(in StorageCell cell)
    {
        Span<byte> key = stackalloc byte[20 + 32];
        cell.Address.Bytes.CopyTo(key);
        cell.Index.ToBigEndian(key.Slice(20, 32));
        AdvanceChain(key);
    }

    private static void AdvanceChain(ReadOnlySpan<byte> item)
    {
        Span<byte> buffer = stackalloc byte[ValueHash256.MemorySize + 52];
        t_chain.Bytes.CopyTo(buffer);
        item.CopyTo(buffer.Slice(ValueHash256.MemorySize));
        t_chain = ValueKeccak.Compute(buffer.Slice(0, ValueHash256.MemorySize + item.Length));
    }
}
