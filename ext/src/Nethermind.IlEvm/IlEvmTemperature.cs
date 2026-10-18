// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.IlEvm;

/// <summary>
/// The temperature gate (<see cref="IlEvmSettings.Temperature"/>, n): on the query runners, compiled code runs a contract
/// only while the contract is hot at run time, that is while it ran at least 1/n of the opcodes, or of the frame parts,
/// the query runners ran in the last one-second window.
/// </summary>
/// <remarks>
/// <para>
/// Compiled code wins on what it covers, but a contract run rarely meets its compiled code cold in the CPU's caches, and
/// its short parts then lose to the interpreter, whose handlers every contract keeps hot. Replays of blocks spread over a
/// thousand and more contracts that each run a few times a second; calls keep most of their opcodes in a few dozen. A
/// contract with a large share of the parts runs often enough to stay warm, and one with a large share of the opcodes
/// runs long enough per part to gain anyway, or often enough too. Shares tell the two workloads apart where an average
/// part length does not, calls splitting frames into short parts too, and they do not depend on how fast the machine runs.
/// </para>
/// <para>
/// How it is measured: each thread takes one part in about <see cref="SampleEvery"/> of each query surface as a sample, at
/// a random gap drawn from the thread's own counter of parts offered (<see cref="IlSurfaceCounts.SampleAt"/>), so the
/// entry's steady path only compares that counter with the next sample's. A sample goes out of line, runs as the rules
/// say, and adds its opcodes, compiled or interpreted, and the part, each times <see cref="SampleEvery"/>, to its code
/// state's counts for the current window and to the thread's totals. The first sample after a window's end sums the
/// threads' totals into the window's and voids every decided entry, so each code's next part is decided again on the new
/// shares.
/// </para>
/// <para>
/// What is decided: a code is hot while its opcodes or its parts in the current window, or in the one before, are at
/// least 1/n of the last window's; its parts must also be two samples' worth at least, about 32 parts a second, as one
/// sample cannot tell a code run a few times a second from one run thirty times. That floor binds only where the query
/// runners run few parts in all, as replays do, their time going to state reads, the tracer and the response, which
/// keep the caches as busy as calls do: there a share of the parts alone would call warm a code that runs ten times a
/// second. A part of a compiled contract that is not hot goes to the interpreter, and so do the code's next
/// parts, decided in its code state until its next sample or the next window. A hot code runs as the other rules say.
/// Run-time compiles wait for the code to be hot too. Code a request brings (<see cref="IlEvm.IsRequestCode"/>) is left to
/// the other rules: it is counted by its content across code infos, and a new code info of it would start cold. Before
/// the first window ends nothing is hot. Block processing's runner takes no part in any of it.
/// </para>
/// </remarks>
internal static class IlTemperature
{
    /// <summary>One part in this many, on average, is a sample, per thread and surface; each sample's opcodes count this many times.</summary>
    internal static int SampleEvery = 16;

    /// <summary>The length of a window, in milliseconds.</summary>
    internal static int WindowMs = 1_000;

    private static readonly object _rolling = new();
    private static long _windowEnd;
    private static int _window;
    private static long _lastTotal;
    private static long _lastParts;
    private static long _sampledAtRoll;
    private static long _partsAtRoll;
    /// <summary>Whether a window has started: the one ending first has nothing before it to share by.</summary>
    private static bool _started;

    [ThreadStatic] private static uint _random;

    /// <summary>The sampled part the interpreter is finishing on this thread, the opcodes compiled code ran of it first, and whether the plugin's other counting wanted its report.</summary>
    [ThreadStatic] private static CodeInfo? _markCode;
    [ThreadStatic] private static IlEvm.IlCodeState? _markState;
    [ThreadStatic] private static int _markOps;
    [ThreadStatic] private static bool _markHotness;

    /// <summary>The windows so far.</summary>
    internal static int Window => Volatile.Read(ref _window);

    /// <summary>The weighted opcodes the samples measured in the last window that ended; 0 before one has.</summary>
    internal static long LastTotal => Volatile.Read(ref _lastTotal);

    /// <summary>The parts the samples stood for in the last window that ended; 0 before one has.</summary>
    internal static long LastParts => Volatile.Read(ref _lastParts);

    /// <summary>The gap to this thread's next sample: uniform in 1 to 2 <see cref="SampleEvery"/> - 1, so one part in <see cref="SampleEvery"/> on average.</summary>
    /// <remarks>A random gap rather than every nth part, which parts that alternate between codes would alias.</remarks>
    internal static long NextGap()
    {
        int every = SampleEvery;
        if (every <= 1)
            return 1;
        uint random = _random == 0 ? (uint)Environment.CurrentManagedThreadId * 2654435761u | 1 : _random;
        random ^= random << 13;
        random ^= random >> 17;
        random ^= random << 5;
        _random = random;
        return 1 + random % (uint)(2 * every - 1);
    }

    /// <summary>Ends the window if its time is up: its total is summed from the threads', and every decided entry is voided.</summary>
    internal static void Roll()
    {
        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref _windowEnd))
            return;
        lock (_rolling)
        {
            if (now < _windowEnd)
                return;
            RollLocked(now);
        }

        IlEvmSettings.TemperatureTick();
    }

    /// <summary>Ends the window now, whatever its time; for tests.</summary>
    internal static void RollNow()
    {
        lock (_rolling)
            RollLocked(Environment.TickCount64);
        IlEvmSettings.TemperatureTick();
    }

    private static void RollLocked(long now)
    {
        long sampled = IlSurfaceCounts.Sum(IlSurfaceCounts.SampledOps), parts = IlSurfaceCounts.Sum(IlSurfaceCounts.SampledParts);
        // The first window has nothing before it: its samples share by nothing yet.
        Volatile.Write(ref _lastTotal, _started ? sampled - _sampledAtRoll : 0);
        Volatile.Write(ref _lastParts, _started ? parts - _partsAtRoll : 0);
        _started = true;
        _sampledAtRoll = sampled;
        _partsAtRoll = parts;
        Volatile.Write(ref _windowEnd, now + WindowMs);
        Volatile.Write(ref _window, _window + 1);
    }

    /// <summary>Adds a sampled part of <paramref name="opCodes"/> to <paramref name="state"/>'s counts for the current window, and to this thread's totals.</summary>
    internal static void Add(IlEvm.IlCodeState state, long opCodes)
    {
        int every = SampleEvery;
        long weighted = opCodes * every;
        int window = Volatile.Read(ref _window);
        if (state.TempWindow != window)
        {
            bool previous = state.TempWindow == window - 1;
            state.PrevOps = previous ? state.TempOps : 0;
            state.PrevParts = previous ? state.TempParts : 0;
            state.TempOps = 0;
            state.TempParts = 0;
            state.TempWindow = window;
        }

        state.TempOps += weighted;
        state.TempParts += every;
        long[] counts = IlSurfaceCounts.OfThisThread();
        counts[IlSurfaceCounts.SampledOps] += weighted;
        counts[IlSurfaceCounts.SampledParts] += every;
    }

    /// <summary>The interpreter finishes a sampled part: its report (<see cref="Finish"/>) completes the sample.</summary>
    /// <param name="compiledOps">The opcodes compiled code ran of the part before it handed the part back.</param>
    /// <param name="hotness">Whether the rules wanted the report themselves; if not, the plugin counts it nowhere else.</param>
    internal static void Mark(CodeInfo codeInfo, IlEvm.IlCodeState state, int compiledOps, bool hotness)
    {
        _markCode = codeInfo;
        _markState = state;
        _markOps = compiledOps;
        _markHotness = hotness;
    }

    /// <summary>The interpreter ran <paramref name="opCodes"/> of a part of <paramref name="codeInfo"/> a runner declined reporting them.</summary>
    /// <returns>Whether the part was declined only to be measured, so that nothing else counts it.</returns>
    /// <remarks>A mark for another code is dropped: the part it was taken for never reported, as when its interpreter run threw.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Finish(CodeInfo codeInfo, int opCodes) => _markCode is not null && FinishMarked(codeInfo, opCodes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool FinishMarked(CodeInfo codeInfo, int opCodes)
    {
        bool mine = ReferenceEquals(_markCode, codeInfo);
        IlEvm.IlCodeState state = _markState!;
        _markCode = null;
        _markState = null;
        if (!mine)
            return false;
        Add(state, (long)_markOps + opCodes);
        return !_markHotness;
    }

    /// <summary>Whether the code of <paramref name="state"/> is hot: its opcodes or its parts in this window or the last are at least 1/n of the last window's.</summary>
    /// <remarks>Code a request brings always is: the other rules judge it by its content.</remarks>
    internal static bool IsHot(CodeInfo codeInfo, IlEvm.IlCodeState state)
    {
        if (IlEvm.IsRequestCode(codeInfo, state))
            return true;
        long totalOps = Volatile.Read(ref _lastTotal), totalParts = Volatile.Read(ref _lastParts);
        int window = Volatile.Read(ref _window), n = Math.Max(1, IlEvmSettings.Temperature);
        int at = state.TempWindow;
        long ops, parts;
        if (at == window)
        {
            ops = Math.Max(state.TempOps, state.PrevOps);
            parts = Math.Max(state.TempParts, state.PrevParts);
        }
        else if (at == window - 1)
        {
            ops = state.TempOps;
            parts = state.TempParts;
        }
        else
        {
            return false;
        }

        // A share of the parts needs two samples behind it: one sample is the parts of a code run too rarely to tell.
        return totalParts > 0 && ((ops > 0 && ops >= totalOps / n) || parts >= Math.Max(2L * SampleEvery, totalParts / n));
    }

    /// <summary>The exit line's part: the gate's threshold and what it decided, or nothing while it is off.</summary>
    internal static string Describe() =>
        IlEvmSettings.Temperature <= 0
            ? "temperature gate off"
            : string.Create(CultureInfo.InvariantCulture,
                $"temperature gate 1/{IlEvmSettings.Temperature} of a {WindowMs} ms window's opcodes, a sample per {SampleEvery} parts: {Window} windows, " +
                $"the last {LastTotal / 1e6:F1}M sampled opcodes in {LastParts} parts; slow-path decisions hot {IlSurfaceCounts.Sum(IlSurfaceCounts.HotDecisions)}, " +
                $"cold {IlSurfaceCounts.Sum(IlSurfaceCounts.ColdDecisions)}; hot frames whose compile it held back {IlSurfaceCounts.Sum(IlSurfaceCounts.HeldBackCompiles)}");

    /// <summary>Starts the windows over, as at startup, past every code state's last window; for tests.</summary>
    internal static void Reset()
    {
        lock (_rolling)
        {
            _started = false;
            Volatile.Write(ref _lastTotal, 0);
            Volatile.Write(ref _lastParts, 0);
            Volatile.Write(ref _windowEnd, 0);
            Volatile.Write(ref _window, _window + 2);
        }

        _markCode = null;
        _markState = null;
    }
}
