// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Nethermind.Core.Container;

namespace Nethermind.IlEvm;

/// <summary>The environments a runner instance serves, which its frame parts are counted under.</summary>
public enum IlEvmSurface : byte
{
    /// <summary>The read-only call environments (<see cref="ReadOnlyQueryKind.Call"/>): eth_call, eth_estimateGas, eth_createAccessList.</summary>
    Call,
    /// <summary>The replay and traced-call environments (<see cref="ReadOnlyQueryKind.Trace"/>): debug_trace*, trace_*, debug_intermediateRoots, proof_getTransactionReceipt.</summary>
    Trace,
    /// <summary>The simulation environments (<see cref="ReadOnlyQueryKind.Simulate"/>): eth_simulateV1, debug_simulateV1, trace_simulateV1, debug_traceCallMany with overrides.</summary>
    Simulate,
    /// <summary>Every other machine the container builds, block processing first, which only Scope=All attaches a runner to.</summary>
    Blocks,
}

/// <summary>The frame parts each surface's runner was offered, and of those the parts compiled code ran, summed over threads.</summary>
/// <remarks>
/// A part counts as compiled when compiled code ran at least one of its opcodes; every rules' runner counts the same way,
/// so the counts say which environments ran compiled code. Each thread counts into its own array with plain writes, and
/// totals are read without stopping the threads, so a total is as of some moment during the read. A cache line of padding
/// on either side keeps two threads' counters off one line however close a collection moves the arrays. A thread's array
/// outlives it, for the totals: one per thread that ever ran a part.
/// </remarks>
internal sealed class IlSurfaceCounts
{
    public const int Surfaces = 4;

    /// <summary>The names the exit line and the stats give the surfaces, in <see cref="IlEvmSurface"/> order.</summary>
    private static readonly string[] _names = ["call", "trace", "simulate", "blocks"];

    [ThreadStatic] private static IlSurfaceCounts? _current;
    private static readonly List<IlSurfaceCounts> _all = [];

    /// <summary>A cache line of padding, in counters.</summary>
    private const int Padding = 8;

    public const int Offered = Padding;

    public const int Compiled = Padding + Surfaces;

    /// <summary>
    /// The temperature gate's sampling clock (<see cref="IlTemperature"/>): per surface, the thread's offered count from
    /// which its next part is a sample. <see cref="long.MaxValue"/> while the gate is off, and always for every surface but
    /// the trace runner's, so the entry's one compare never takes a call's or a simulation's part out of line.
    /// </summary>
    public const int SampleAt = Padding + 2 * Surfaces;

    /// <summary>The temperature gate: the opcodes the thread's samples measured, each times its weight, over the thread's life.</summary>
    public const int SampledOps = Padding + 3 * Surfaces;

    /// <summary>The temperature gate: the parts its slow path found hot and ran as the other rules say, and those it found cold.</summary>
    public const int HotDecisions = SampledOps + 1;

    public const int ColdDecisions = SampledOps + 2;

    /// <summary>The temperature gate: the hot frames whose compile it held back, the code not being hot at run time.</summary>
    public const int HeldBackCompiles = SampledOps + 3;

    /// <summary>The temperature gate: the parts the thread's samples stand for, each sample its weight, over the thread's life.</summary>
    public const int SampledParts = Padding + 4 * Surfaces;

    private readonly long[] _counts = new long[Padding + Surfaces * 5 + Padding];

    private IlSurfaceCounts()
    {
        StartSampling(_counts, IlTemperature.On);
    }

    /// <summary>Starts or stops the trace runner's sampling clock in <paramref name="counts"/>, due at once or never; the other surfaces' never run.</summary>
    private static void StartSampling(long[] counts, bool on)
    {
        for (int surface = 0; surface < Surfaces; surface++)
            Volatile.Write(ref counts[SampleAt + surface], on && surface == (int)IlEvmSurface.Trace ? 0 : long.MaxValue);
    }

    /// <summary>Starts or stops every thread's sampling clocks, as the temperature gate is turned on or off.</summary>
    /// <remarks>Racy with the threads' own writes: a thread may take one sample more or less, which only one part feels.</remarks>
    public static void ResetSampling(bool on)
    {
        lock (_all)
        {
            foreach (IlSurfaceCounts thread in _all)
                StartSampling(thread._counts, on);
        }
    }

    /// <summary>The sum over every thread so far of the counter at <paramref name="index"/>.</summary>
    public static long Sum(int index)
    {
        long total = 0;
        lock (_all)
        {
            foreach (IlSurfaceCounts thread in _all)
                total += Volatile.Read(ref thread._counts[index]);
        }

        return total;
    }

    /// <summary>This thread's counts, which only it writes: parts offered at <see cref="Offered"/> plus the surface, parts compiled at <see cref="Compiled"/> plus it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long[] OfThisThread() => (_current ?? Create())._counts;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IlSurfaceCounts Create()
    {
        IlSurfaceCounts created = new();
        lock (_all)
            _all.Add(created);
        _current = created;
        return created;
    }

    /// <summary>The parts offered to <paramref name="surface"/>'s runner, and those compiled code ran, over every thread so far.</summary>
    public static (long Offered, long Compiled) Totals(IlEvmSurface surface)
    {
        long offered = 0, compiled = 0;
        lock (_all)
        {
            foreach (IlSurfaceCounts thread in _all)
            {
                offered += Volatile.Read(ref thread._counts[Offered + (int)surface]);
                compiled += Volatile.Read(ref thread._counts[Compiled + (int)surface]);
            }
        }

        return (offered, compiled);
    }

    /// <summary>The exit line's part: <c>parts offered/compiled: call o/c, trace o/c, simulate o/c, blocks o/c</c>.</summary>
    public static string Describe()
    {
        StringBuilder line = new("parts offered/compiled:");
        for (int surface = 0; surface < Surfaces; surface++)
        {
            (long offered, long compiled) = Totals((IlEvmSurface)surface);
            line.Append(CultureInfo.InvariantCulture, $"{(surface == 0 ? " " : ", ")}{_names[surface]} {offered}/{compiled}");
        }

        return line.ToString();
    }

    /// <summary>The stats header's token: <c>surfaces=call:o/c,trace:o/c,simulate:o/c,blocks:o/c</c>.</summary>
    public static string StatsToken()
    {
        StringBuilder token = new("surfaces=");
        for (int surface = 0; surface < Surfaces; surface++)
        {
            (long offered, long compiled) = Totals((IlEvmSurface)surface);
            token.Append(CultureInfo.InvariantCulture, $"{(surface == 0 ? "" : ",")}{_names[surface]}:{offered}/{compiled}");
        }

        return token.ToString();
    }

    /// <summary>The surface of a query module's kind; exactly one kind's bit, else null.</summary>
    public static IlEvmSurface? Of(ReadOnlyQueryKind kind) => kind switch
    {
        ReadOnlyQueryKind.Call => IlEvmSurface.Call,
        ReadOnlyQueryKind.Trace => IlEvmSurface.Trace,
        ReadOnlyQueryKind.Simulate => IlEvmSurface.Simulate,
        _ => null,
    };

    /// <summary>The query kinds the core has, one per surface but <see cref="IlEvmSurface.Blocks"/>, in surface order.</summary>
    public static readonly ReadOnlyQueryKind[] Kinds = [ReadOnlyQueryKind.Call, ReadOnlyQueryKind.Trace, ReadOnlyQueryKind.Simulate];
}
