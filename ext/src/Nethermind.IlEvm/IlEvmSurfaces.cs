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
/// <remarks>
/// The core loads a read-only query module into the environments of the kinds it names, one kind per environment, so the
/// plugin registers a module per kind, each attaching a runner of its own surface; block processing's runner (Scope=All)
/// is <see cref="Blocks"/>.
/// </remarks>
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
/// <para>
/// A part counts as compiled when compiled code ran at least one of its opcodes, to the part's end or until it handed
/// back; a part declined, or handed back before its first opcode, counts as offered only. Every rules' runner counts its
/// parts the same way, at its entry, so the counts say which environments ran compiled code whatever the rules: a
/// benchmark of a trace endpoint whose trace count of compiled parts stays at zero measured the interpreter.
/// </para>
/// <para>
/// Each thread counts into an array of its own, with plain writes, and the totals are read without stopping the threads:
/// a total read while parts run is as of some moment during the read. The counters sit between a cache line of padding
/// on either side, so two threads' arrays, however close a collection moves them, never write to one line. A thread's
/// array outlives it here, for the totals: one per thread that ever ran a part.
/// </para>
/// </remarks>
internal sealed class IlSurfaceCounts
{
    /// <summary>The surfaces counted: <see cref="IlEvmSurface"/>'s values.</summary>
    public const int Surfaces = 4;

    /// <summary>The names the exit line and the stats give the surfaces, in <see cref="IlEvmSurface"/> order.</summary>
    private static readonly string[] _names = ["call", "trace", "simulate", "blocks"];

    [ThreadStatic] private static IlSurfaceCounts? _current;
    private static readonly List<IlSurfaceCounts> _all = [];

    /// <summary>A cache line of padding, in counters.</summary>
    private const int Padding = 8;

    /// <summary>Where the counters of the parts offered start in a thread's array: <see cref="Offered"/> plus the surface.</summary>
    public const int Offered = Padding;

    /// <summary>Where the counters of the parts compiled code ran start in a thread's array: <see cref="Compiled"/> plus the surface.</summary>
    public const int Compiled = Padding + Surfaces;

    /// <summary>Per surface, the parts offered at <see cref="Offered"/> plus the surface and those compiled code ran at <see cref="Compiled"/> plus it, padded.</summary>
    private readonly long[] _counts = new long[Padding + Surfaces * 2 + Padding];

    private IlSurfaceCounts()
    {
    }

    /// <summary>
    /// This thread's counts, which only this thread writes: a runner adds one at <see cref="Offered"/> plus its surface for
    /// each part it is offered, and one at <see cref="Compiled"/> plus its surface for each part compiled code ran.
    /// </summary>
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
