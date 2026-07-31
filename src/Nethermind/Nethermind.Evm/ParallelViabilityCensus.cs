// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// Diagnostic census sizing PARALLEL execution of a batch's depth-1 siblings - no caching, no
/// repeated requests: the question is what share of siblings could run concurrently from the
/// transaction-initial state on a first-time request.
///
/// The design being sized rests on one fact: a sibling's OUTPUT does not depend on access-list
/// warmth; warmth changes only its gas. So siblings can execute concurrently from the state the
/// transaction started with, and the gas is corrected exactly at arbitration - speculation begins
/// from a subset of any later warm set, so it can only over-charge, and the correction is
/// (cold - warm) per cell it charged cold that was in fact already warm. That is arithmetic over
/// EIP-2929 constants, not estimation.
///
/// A sibling qualifies when all of these hold, and this measures each one:
/// - it reads nothing an earlier sibling of the same request WROTE AND KEPT (a stale read would
///   change the output, which no gas correction can repair);
/// - every GAS it executes is matched by a call, i.e. the value is only forwarded as a budget. Gas
///   consumption is budget-independent as long as nothing runs out of gas, since unused gas is
///   refunded, so a generous speculative budget yields the real consumption; a GAS value that
///   escapes into anything other than a call operand is the case this cannot cover;
/// - it leaves no net state, no logs, no destroys, no refund, and takes no value, so applying its
///   effect at arbitration is trivial.
/// Enabled by NETHERMIND_PARALLEL_CENSUS; the flag is static readonly so a disabled build carries
/// nothing in the hot paths.
/// </summary>
public static class ParallelViabilityCensus
{
    public static readonly bool IsEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETHERMIND_PARALLEL_CENSUS"));

    [ThreadStatic] private static bool t_insideSibling;
    [ThreadStatic] private static HashSet<int>? t_reads;
    [ThreadStatic] private static HashSet<int>? t_writes;
    [ThreadStatic] private static HashSet<int>? t_survivingWrites;
    [ThreadStatic] private static int t_gasOps;
    [ThreadStatic] private static int t_callOps;
    [ThreadStatic] private static int t_siblings;
    [ThreadStatic] private static int t_qualified;
    [ThreadStatic] private static int t_failStale;
    [ThreadStatic] private static int t_failGas;
    [ThreadStatic] private static int t_failDirty;
    [ThreadStatic] private static int t_firstDirty;
    [ThreadStatic] private static int t_unmatchedGas;

    private static long s_frames;
    private static long s_siblings;
    private static long s_qualified;
    private static long s_failStale;
    private static long s_failGas;
    private static long s_failDirty;
    private static long s_bigFrames;
    private static long s_bigSiblings;
    private static long s_bigQualified;
    private static long s_gasOps;
    private static long s_callOps;
    private static long s_bigPrefix;
    private static System.Threading.Timer? s_timer;

    static ParallelViabilityCensus()
    {
        if (IsEnabled)
        {
            s_timer = new System.Threading.Timer(static _ => Report(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        }
    }

    public static bool InsideSibling => t_insideSibling;

    public static void BeginSibling()
    {
        if (t_siblings == 0) t_firstDirty = -1;
        t_insideSibling = true;
        t_gasOps = 0;
        t_callOps = 0;
        t_unmatchedGas = 0;
        (t_reads ??= new HashSet<int>(1024)).Clear();
        (t_writes ??= new HashSet<int>(64)).Clear();
    }

    /// <summary>GAS executions and call-family executions per sibling. Solidity emits gas() as the
    /// forwarding operand of every external call, so counts that track each other mean the value is
    /// only forwarded - and a forwarded budget cannot change a result that does not run out of gas.
    /// A surplus of GAS over calls is the case that would need a real taint proof.</summary>
    public static void ObserveGas()
    {
        if (!t_insideSibling) return;
        t_gasOps++;
        t_unmatchedGas++;
    }

    /// <summary>A call whose gas operand came straight from GAS is a forwarding use, which is the
    /// sound case. Solidity pushes the forwarded budget last, so the operand sits on top when the
    /// call executes and the preceding opcode is the GAS that produced it.</summary>
    public static void ObserveCall(bool gasForwarded)
    {
        if (!t_insideSibling) return;
        t_callOps++;
        if (gasForwarded && t_unmatchedGas > 0) t_unmatchedGas--;
    }

    public static void ObserveRead(int hash)
    {
        if (t_insideSibling) t_reads?.Add(hash);
    }

    public static void ObserveWrite(int hash)
    {
        if (t_insideSibling) t_writes?.Add(hash);
    }

    public static void EndSibling(bool cleanFrame)
    {
        t_insideSibling = false;
        t_siblings++;

        bool stale = false;
        HashSet<int>? surviving = t_survivingWrites;
        if (surviving is { Count: > 0 } && t_reads is not null)
        {
            foreach (int h in t_reads)
            {
                if (surviving.Contains(h)) { stale = true; break; }
            }
        }

        if (stale) t_failStale++;
        // Forwarding-only use of GAS is the sound case: a larger forwarded budget cannot change a
        // result that does not run out of gas. A surplus of GAS over calls is what would need a
        // real taint proof, so that is what this counts as a failure.
        bool gasBeyondForwarding = t_unmatchedGas > 0;
        if (gasBeyondForwarding) t_failGas++;
        if (!cleanFrame)
        {
            t_failDirty++;
            // The position of the first sibling that keeps state is the honest bound on a
            // prefix-parallel design: everything before it started from the same state and can run
            // concurrently with no validation at all.
            if (t_firstDirty < 0) t_firstDirty = t_siblings - 1;
        }
        if (!stale && !gasBeyondForwarding && cleanFrame) t_qualified++;
        System.Threading.Interlocked.Add(ref s_gasOps, t_gasOps);
        System.Threading.Interlocked.Add(ref s_callOps, t_callOps);

        // Only writes that survived the frame can make a later sibling's speculation stale.
        if (!cleanFrame && t_writes is { Count: > 0 } writes)
        {
            (t_survivingWrites ??= new HashSet<int>(256)).UnionWith(writes);
        }
    }

    public static void EndTopFrame()
    {
        t_insideSibling = false;
        if (t_siblings > 0)
        {
            System.Threading.Interlocked.Increment(ref s_frames);
            System.Threading.Interlocked.Add(ref s_siblings, t_siblings);
            System.Threading.Interlocked.Add(ref s_qualified, t_qualified);
            System.Threading.Interlocked.Add(ref s_failStale, t_failStale);
            System.Threading.Interlocked.Add(ref s_failGas, t_failGas);
            System.Threading.Interlocked.Add(ref s_failDirty, t_failDirty);
            if (t_siblings >= 20)
            {
                System.Threading.Interlocked.Increment(ref s_bigFrames);
                System.Threading.Interlocked.Add(ref s_bigSiblings, t_siblings);
                System.Threading.Interlocked.Add(ref s_bigQualified, t_qualified);
                System.Threading.Interlocked.Add(ref s_bigPrefix, t_firstDirty < 0 ? t_siblings : t_firstDirty);
            }
        }

        t_siblings = 0;
        t_qualified = 0;
        t_failStale = 0;
        t_failGas = 0;
        t_failDirty = 0;
        t_survivingWrites?.Clear();
    }

    private static void Report()
    {
        long siblings = System.Threading.Interlocked.Read(ref s_siblings);
        if (siblings == 0) return;
        long big = System.Threading.Interlocked.Read(ref s_bigSiblings);
        Console.WriteLine(
            $"PARALLEL-CENSUS frames {System.Threading.Interlocked.Read(ref s_frames)}, siblings {siblings}, " +
            $"qualified {100.0 * System.Threading.Interlocked.Read(ref s_qualified) / siblings:F2}%, " +
            $"fails: stale-read {100.0 * System.Threading.Interlocked.Read(ref s_failStale) / siblings:F2}%, " +
            $"GAS {100.0 * System.Threading.Interlocked.Read(ref s_failGas) / siblings:F2}%, " +
            $"dirty {100.0 * System.Threading.Interlocked.Read(ref s_failDirty) / siblings:F2}%, " +
            $"GAS/call ops {System.Threading.Interlocked.Read(ref s_gasOps)}/{System.Threading.Interlocked.Read(ref s_callOps)} | " +
            $"batches>=20: frames {System.Threading.Interlocked.Read(ref s_bigFrames)}, siblings {big}" +
            (big > 0
                ? $", qualified {100.0 * System.Threading.Interlocked.Read(ref s_bigQualified) / big:F2}%"
                  + $", clean prefix {100.0 * System.Threading.Interlocked.Read(ref s_bigPrefix) / big:F2}% of siblings"
                : ""));
    }
}
