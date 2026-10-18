// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.IlEvm;

/// <summary>
/// The temperature gate (<see cref="IlEvmSettings.TemperatureShare"/> or <see cref="IlEvmSettings.TemperatureRate"/>): on
/// the trace runner only, compiled code runs a contract only while the contract is hot at run time in the trace work.
/// </summary>
/// <remarks>
/// <para>
/// Compiled code wins on what it covers, but a contract run rarely meets its compiled code cold in the CPU's caches, and
/// its short parts then lose to the interpreter, whose handlers every contract keeps hot. Replays of transactions and
/// blocks, which run only in the trace environments (<see cref="IlEvmSurface.Trace"/>), spread over a thousand and more
/// contracts, and lost with every one compiled; the run-time compiles of their long tail kept the compile thread busy too.
/// Calls and simulations are left alone: there compiled code pays even on contracts of middling rate.
/// </para>
/// <para>
/// How it is measured: each thread takes one trace part in about <see cref="SampleEvery"/> as a sample, at a random gap
/// drawn from the thread's own counter of trace parts (<see cref="IlSurfaceCounts.SampleAt"/>), so the entry's steady path
/// only compares that counter with the next sample's. A sample goes out of line, runs as the rules say, and adds its
/// opcodes, compiled or interpreted, and the part, each times <see cref="SampleEvery"/>, to its code state's counts for the
/// current one-second window and to the thread's totals. The first sample after a window's end sums the threads' totals
/// into the window's and moves <see cref="GateEpoch"/>, which voids every code state's trace verdict.
/// </para>
/// <para>
/// What is hot, by the form of the threshold: with a share 1/n, a code whose opcodes or parts in the current window, or in
/// the one before, are at least 1/n of the last window's trace work; with a rate r, a code that ran at least r parts or r
/// thousand opcodes a second in one of those windows. A part count must also be two samples' worth, about 32 parts a
/// second: one sample cannot tell a code run a few times a second from one run thirty times. Opcodes need no such floor,
/// so a code with long parts stays compiled however rarely it runs. Code a request brings (<see cref="IlEvm.IsRequestCode"/>)
/// is always hot: it is counted by its content across code infos, and a new code info of it would start cold.
/// </para>
/// <para>
/// What it decides: the trace runner keeps its verdict in the code state (<see cref="IlEvm.IlCodeState.TraceGate"/>),
/// apart from the decisions every query runner shares. A cold verdict sends the code's trace parts to the interpreter until
/// the next window; a hot one lets them take the shared decisions, as with the gate off; code with nothing compiled has
/// nothing to gate and is hot. A trace part of code that is not hot asks for no run-time compile either. Calls and
/// simulations neither read nor write any of it: they compile, decide and run exactly as with the gate off. Block
/// processing's runner takes no part.
/// </para>
/// </remarks>
internal static class IlTemperature
{
    /// <summary>One trace part in this many, on average, is a sample, per thread; each sample counts this many times.</summary>
    internal static int SampleEvery = 16;

    /// <summary>The length of a window, in milliseconds.</summary>
    internal static int WindowMs = 1_000;

    /// <summary>A rate threshold of r also counts a code hot at r times this many opcodes a second: a part this long gains even cold.</summary>
    internal const int OpCodesPerPart = 1_000;

    private static readonly object _rolling = new();
    private static long _windowEnd;
    private static int _window;
    private static long _lastTotal;
    private static long _lastParts;
    private static long _sampledAtRoll;
    private static long _partsAtRoll;
    /// <summary>Whether a window has started: the one ending first has nothing before it to share by.</summary>
    private static bool _started;
    private static int _gateEpochs;

    /// <summary>
    /// What the trace verdicts in code states (<see cref="IlEvm.IlCodeState.TraceGate"/>) are taken under: 0 while the gate
    /// is off, so that a code state's 0 lets the trace runner take the shared decisions as with no gate; else a value never
    /// used before, new with each window and each change of the gate, which voids every verdict.
    /// </summary>
    internal static int GateEpoch = IlEvmSettings.TemperatureOn ? ++_gateEpochs : 0;

    /// <summary>Whether the gate is on.</summary>
    internal static bool On => GateEpoch != 0;

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

    /// <summary>Starts the gate over as the settings say: verdicts void, the trace runner's sampling clocks on or off, the windows from scratch.</summary>
    internal static void Restart(bool on)
    {
        lock (_rolling)
        {
            _started = false;
            Volatile.Write(ref _lastTotal, 0);
            Volatile.Write(ref _lastParts, 0);
            Volatile.Write(ref _windowEnd, 0);
            // Past every code state's last window.
            Volatile.Write(ref _window, _window + 2);
            Volatile.Write(ref GateEpoch, on ? ++_gateEpochs : 0);
        }

        IlSurfaceCounts.ResetSampling(on);
        _markCode = null;
        _markState = null;
    }

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

    /// <summary>Ends the window if its time is up: its totals are summed from the threads', and every trace verdict is voided.</summary>
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
    }

    /// <summary>Ends the window now, whatever its time; for tests.</summary>
    internal static void RollNow()
    {
        lock (_rolling)
            RollLocked(Environment.TickCount64);
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
        if (GateEpoch != 0)
            Volatile.Write(ref GateEpoch, ++_gateEpochs);
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

    /// <summary>Whether the code of <paramref name="state"/> is hot in the trace work, by the threshold's form (see the remarks on <see cref="IlTemperature"/>).</summary>
    internal static bool IsHot(CodeInfo codeInfo, IlEvm.IlCodeState state)
    {
        if (IlEvm.IsRequestCode(codeInfo, state))
            return true;
        int window = Volatile.Read(ref _window);
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

        // A part count needs two samples behind it: one sample is the parts of a code run too rarely to tell.
        long floor = 2L * SampleEvery;
        int rate = IlEvmSettings.TemperatureRate;
        if (rate > 0)
            return parts >= Math.Max(floor, rate) || ops >= (long)rate * OpCodesPerPart;
        long totalOps = Volatile.Read(ref _lastTotal), totalParts = Volatile.Read(ref _lastParts);
        int n = Math.Max(1, IlEvmSettings.TemperatureShare);
        return totalParts > 0 && ((ops > 0 && ops >= totalOps / n) || parts >= Math.Max(floor, totalParts / n));
    }

    /// <summary>The threshold as the switch writes it: <c>1/n</c> for a share, <c>r</c> for a rate, or <c>off</c>.</summary>
    internal static string Threshold =>
        IlEvmSettings.TemperatureRate > 0 ? IlEvmSettings.TemperatureRate.ToString(CultureInfo.InvariantCulture)
        : IlEvmSettings.TemperatureShare > 0 ? $"1/{IlEvmSettings.TemperatureShare}" : "off";

    /// <summary>The rule in words, for the startup line.</summary>
    internal static string DescribeRule() =>
        IlEvmSettings.TemperatureRate > 0
            ? $"a trace's contract runs compiled only while it runs {IlEvmSettings.TemperatureRate} parts or {IlEvmSettings.TemperatureRate * OpCodesPerPart} opcodes a second in traces"
            : $"a trace's contract runs compiled only while it ran 1/{IlEvmSettings.TemperatureShare} of the last second's trace opcodes or parts";

    /// <summary>The exit line's part: the gate's threshold and what it decided, or nothing while it is off.</summary>
    internal static string Describe() =>
        !On
            ? "temperature gate off"
            : string.Create(CultureInfo.InvariantCulture,
                $"temperature gate {Threshold} on traces, {WindowMs} ms windows, a sample per {SampleEvery} trace parts: {Window} windows, " +
                $"the last {LastTotal / 1e6:F1}M sampled opcodes in {LastParts} parts; slow-path verdicts hot {IlSurfaceCounts.Sum(IlSurfaceCounts.HotDecisions)}, " +
                $"cold {IlSurfaceCounts.Sum(IlSurfaceCounts.ColdDecisions)}; hot frames whose compile it held back {IlSurfaceCounts.Sum(IlSurfaceCounts.HeldBackCompiles)}");

    /// <summary>Starts the windows over, as at startup, past every code state's last window; for tests.</summary>
    internal static void Reset() => Restart(On);
}
