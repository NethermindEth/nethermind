// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.IlEvm;

/// <summary>The switchback check's clock: a tick every <see cref="TickMs"/> milliseconds, <see cref="WindowTicks"/> ticks to a window.</summary>
/// <remarks>
/// <para>
/// A window is what the checks take turns by: in a window a contract's check names interpreted, every part of the
/// contract its runner admits runs on the interpreter, on every thread, and in the others on compiled code. A window's
/// first tick settles, the caches catching up with the switch, and is timed on neither path.
/// </para>
/// <para>
/// One static int, which every part a check runs reads; a timer moves it on by the time elapsed, so a late tick skips
/// a value rather than stretching the windows. Once a window it also lifts the marks of losses whose transactions are
/// over, and ends the probations whose time has come unless the scheduler does that between blocks
/// (<see cref="IlCheck.SchedulerRestores"/>) and has done it lately: with blocks back to back, or while the node syncs, no
/// window opens, and a probation would otherwise last until one does. The timer starts with the first check that
/// becomes active.
/// </para>
/// </remarks>
internal static class IlCheckClock
{
    /// <summary>The milliseconds a tick stands for.</summary>
    public const int TickMs = 25;

    /// <summary>The ticks of a window, of which the first settles.</summary>
    public const int WindowTicks = 10;

    /// <summary>The ticks since the clock started: the window is <c>Tick / WindowTicks</c>, and it settles where the remainder is 0.</summary>
    public static int Tick;

    /// <summary>For tests: the timer leaves <see cref="Tick"/> and the probations alone, and the test moves them.</summary>
    public static bool ManualTicks { get; set; }

    /// <summary>For tests: called after the timer's work of each new window, so a test of the timer waits for windows rather than sleeping.</summary>
    internal static Action? WindowForTests;

    private static readonly object _starting = new();
    private static Timer? _timer;
    private static long _startedAt, _elapsedTicks;
    private static int _lastWindow = -1;
    private static int _ticking;

    /// <summary>Whether the timer runs.</summary>
    public static bool Running => Volatile.Read(ref _timer) is not null;

    /// <summary>Starts the timer, once, unless the ticks are manual.</summary>
    public static void EnsureStarted()
    {
        if (ManualTicks || Volatile.Read(ref _timer) is not null)
            return;
        lock (_starting)
        {
            if (_timer is not null)
                return;
            _startedAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref _timer, new Timer(static _ => OnTick(), null, TickMs, TickMs));
        }
    }

    private static void OnTick()
    {
        // A slow callback overlapping the next one is skipped by it.
        if (ManualTicks || Interlocked.Exchange(ref _ticking, 1) == 1)
            return;
        try
        {
            // On by the ticks elapsed since the last callback, from wherever the clock stands: a test may have set it.
            long elapsed = Stopwatch.GetElapsedTime(_startedAt).Ticks / (TimeSpan.TicksPerMillisecond * TickMs);
            long passed = elapsed - _elapsedTicks;
            if (passed <= 0)
                return;
            _elapsedTicks = elapsed;
            int tick = Interlocked.Add(ref Tick, (int)Math.Min(passed, int.MaxValue / 2));
            int window = tick / WindowTicks;
            if (window == _lastWindow)
                return;
            _lastWindow = window;
            OnWindow(Environment.TickCount64);
            WindowForTests?.Invoke();
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>
    /// The timer's work once a window, at <paramref name="now"/> in <see cref="Environment.TickCount64"/> milliseconds:
    /// the marks of losses whose transactions are over are lifted, which changes nothing that runs; the probations and
    /// waits whose time has come end here unless the scheduler ends them, as it does at its maintenance between blocks,
    /// and has within <see cref="IlCheck.RestoreGraceMs"/>.
    /// </summary>
    /// <returns>How many checks started over.</returns>
    internal static int OnWindow(long now) => IlCheck.RestoreDue(now, rechecks: !IlCheck.SchedulerRestores || now - Volatile.Read(ref IlCheck.LastSchedulerRestore) >= IlCheck.RestoreGraceMs);

    /// <summary>Sets the clock to <paramref name="tick"/>, for tests with <see cref="ManualTicks"/>.</summary>
    internal static void SetForTests(int tick) => Volatile.Write(ref Tick, tick);

    /// <summary>The first tick of <paramref name="window"/>, which settles; for tests.</summary>
    internal static int SettleTickOf(int window) => window * WindowTicks;

    /// <summary>A tick of <paramref name="window"/> past its settling, for tests.</summary>
    internal static int TimedTickOf(int window) => window * WindowTicks + WindowTicks / 2;
}

/// <summary>
/// The switchback check of one compiled contract on one runner: times the parts the runner admits against the
/// interpreter's on the same code, and takes the runner off the contract where compiled code does not win.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter's side comes from switchback windows of <see cref="IlCheckClock"/>: windows in which every part the
/// runner admits runs on the interpreter, on every thread, so the interpreter is timed on the code it has run for a while
/// rather than between compiled runs that evict its handlers; timed that way, the Legacy rules' per-frame check kept
/// light contracts compiled that lost against a clean interpreter. One window in <see cref="TrialShare"/> is interpreted
/// until a verdict and one in <see cref="KeptShare"/> once kept; block processing's runner, whose time matters most, one
/// in <see cref="BlockShare"/> throughout. Never two in a row, and salted per check, so contracts take their turns apart.
/// Parts are timed one in <see cref="TimeInterpretedEvery"/> in an interpreted window, and one in
/// <see cref="TimeCompiledTrialEvery"/> or <see cref="TimeCompiledKeptEvery"/> in the others.
/// </para>
/// <para>
/// Samples pool into buckets of at least <see cref="BucketOps"/> opcodes, so short parts measure past the timer's
/// resolution. A full compiled bucket joins a ring of the latest <see cref="RecentSamples"/>; a full interpreted bucket
/// makes a pair, the ring's median time per opcode over its own, at most <see cref="MaxPairsPerWindow"/> from one window,
/// during which the ring stands still. The verdict comes at <see cref="Pairs"/> pairs, or early at <see cref="EarlyPairs"/>
/// over <see cref="EarlyWindows"/> windows that all agree by a clear margin: kept when the pairs' median is at most
/// <see cref="KeepRatio"/>. Kept, the contract is still timed, at the kept rates: each <see cref="RollingPairs"/> pairs
/// whose median passes <see cref="RollingLossRatio"/> are a loss.
/// </para>
/// <para>
/// A loss clears the runner's bits of the contract's <see cref="IlEvm.IlContract.RunMask"/>, so the runner leaves the
/// contract to the interpreter at once, on every code state. The first loss, and any in the start-up window
/// (<see cref="IlEvmSettings.SelfCheckWarmUpMs"/>), is a probation: the bits come back when its time is up, and the check
/// starts over. A second loss outside the start-up window gives the contract back: one that live profiling promoted for
/// this runner's source is released (<see cref="ReleaseLive"/>); any other keeps its code, its bits cleared, and is checked
/// again every <see cref="MaskedRecheckMs"/>. The read-only queries' runner's check of a shipped contract does not run in
/// the start-up window at all, and a loss there would only start it over: the list's contracts serve a node's first calls
/// compiled, and are checked once the window is over.
/// </para>
/// <para>
/// No part writes the check on its own: a thread pools its timed samples per check until they fill a bucket
/// (<see cref="IlThread"/>), then adds them to the check's shared bucket, which takes the check's lock once it fills.
/// An untimed compiled part reads the clock, a word of the check and a draw of its thread, and runs on the runner's own
/// path. Block processing's check times compiled parts at the kept rate once its ring of compiled samples is full.
/// </para>
/// </remarks>
internal sealed class IlCheck
{
    /// <summary>A check timing toward its verdict.</summary>
    public const int Trial = 0;

    /// <summary>Compiled code won: the runner runs the contract, and the check goes on timing it.</summary>
    public const int Kept = 1;

    /// <summary>Compiled code lost: the runner leaves the contract to the interpreter until the recheck.</summary>
    public const int Probation = 2;

    /// <summary>Lost twice: a contract that keeps its code, left to the interpreter until a recheck a long while on.</summary>
    public const int Masked = 3;

    /// <summary>Lost twice: a live contract, released; the check is over.</summary>
    public const int GivenBack = 4;

    /// <summary>The fewest opcodes a sample pools.</summary>
    public const int BucketOps = 1_024;

    /// <summary>The compiled samples a pair's compiled side is the median of.</summary>
    public const int RecentSamples = 8;

    /// <summary>The pairs a verdict waits for, unless they agree early.</summary>
    public const int Pairs = 31;

    /// <summary>The pairs, and the windows they come from, after which a verdict every pair agrees on by a clear margin is taken early.</summary>
    public const int EarlyPairs = 7;

    public const int EarlyWindows = 3;

    /// <summary>The margins of an early verdict: every pair at most this for compiled code, or at least that against it.</summary>
    public const double EarlyCompiled = 0.85;

    public const double EarlyInterpreted = 1.18;

    /// <summary>The pairs one window may give: its pairs share one compiled side, the ring standing still while it runs.</summary>
    public const int MaxPairsPerWindow = 8;

    /// <summary>The pairs a kept contract's rolling check takes a median of.</summary>
    public const int RollingPairs = 15;

    /// <summary>Of the parts admitted in an interpreted window, one in this many is timed.</summary>
    public const uint TimeInterpretedEvery = 4;

    /// <summary>Of the compiled parts, one in this many is timed until the verdict, and one in the other once kept.</summary>
    public const uint TimeCompiledTrialEvery = 8;

    public const uint TimeCompiledKeptEvery = 32;

    /// <summary>
    /// The bits of a window word: a timing state, compiled parts timed at the kept rate, and interpreted; the window and
    /// one above them. The kept rate is a kept check's, and block processing's once its ring of compiled samples is full:
    /// its pairs wait on interpreted windows that meet a block, a few an hour at the chain's tip, and a fuller ring buys none.
    /// </summary>
    internal const long WordInterpreted = 1, WordSparse = 2, WordTiming = 4;

    private const int WordShift = 3;

    /// <summary>Compiled code must run the contract's opcodes in at most this share of the interpreter's time to be kept: a guard band for the bias the windows leave.</summary>
    public static double KeepRatio { get; set; } = Knob("KEEP_RATIO", 0.97);

    /// <summary>A kept contract loses when its rolling pairs' median passes this.</summary>
    public static double RollingLossRatio { get; set; } = Knob("ROLLING_LOSS_RATIO", 1.02);

    /// <summary>One window in this many is interpreted while the read-only queries' runner's check is undecided; a power of two.</summary>
    public static int TrialShare { get; set; } = (int)Knob("TRIAL_SHARE", 8);

    /// <summary>One window in this many is interpreted once it is kept.</summary>
    public static int KeptShare { get; set; } = (int)Knob("KEPT_SHARE", 32);

    /// <summary>One window in this many is interpreted for block processing's runner, decided or not.</summary>
    public static int BlockShare { get; set; } = (int)Knob("BLOCK_SHARE", 32);

    /// <summary>How long block processing's runner leaves a contract on probation; the queries' runner takes <see cref="IlEvmSettings.SelfCheckRecheckMs"/>.</summary>
    public static int BlockRecheckMs { get; set; } = (int)Knob("BLOCK_RECHECK_MS", 600_000);

    /// <summary>How long a contract that lost twice and keeps its code is left to the interpreter before it is checked again.</summary>
    public static int MaskedRecheckMs { get; set; } = (int)Knob("MASKED_RECHECK_MS", 600_000);

    /// <summary>How long a contract a check takes its runner off still has its parts looked at for a visit under way, in milliseconds.</summary>
    /// <remarks>
    /// A visit starts only on a part its runner may run, so once the transactions under way at the loss are over, no
    /// visit of the contract can still be counting; past this the mark that sends its parts to be looked at is lifted.
    /// </remarks>
    public const int UnderWayMs = 1_000;

    /// <summary>
    /// Releases a live contract a check gave back: by default at once, on the thread whose sample decided; the scheduler
    /// routes it to a job between blocks (<see cref="ReleaseJob"/>), which also marks the profile demoted.
    /// </summary>
    public static Action<IlEvm.IlContract> ReleaseLive { get; set; } = ReleaseNow;

    /// <summary>Whether the scheduler ends the probations whose time has come, at its windows' maintenance; until it is attached the clock's timer does, once a window.</summary>
    public static bool SchedulerRestores { get; set; }

    /// <summary>When the scheduler's maintenance last ended the probations due, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    public static long LastSchedulerRestore;

    /// <summary>How long the clock's timer leaves due probations to the scheduler's maintenance before it ends them itself, as when no window opens between blocks back to back.</summary>
    public const int RestoreGraceMs = 2_000;

    /// <summary>The contract checked.</summary>
    public readonly IlEvm.IlContract Contract;

    /// <summary>Whether this is block processing's runner's check, else the read-only queries' runner's.</summary>
    public readonly bool Block;

    /// <summary>What the windows of this check are hashed with: the profile's number, twice, and the runner, so the two runners' checks of one contract take different windows.</summary>
    public readonly uint Salt;

    /// <summary>The window last decided, in a word every part reads: <c>(window + 1) &lt;&lt; 3</c>, and the timing, kept and interpreted bits.</summary>
    /// <remarks>Written once a window by whichever part first meets it, and cleared on every change of state, so the next part decides again.</remarks>
    private long _window;

    /// <summary>Whether the check runs, once <see cref="IlEvmSettings.SelfCheckDelayMs"/> have passed since the contract was attached.</summary>
    private bool _active;

    private volatile int _state;
    private readonly Bucket _interpreted = new(), _compiled = new();
    private readonly double[] _recent = new double[RecentSamples];
    private int _recentCount, _recentNext;
    private readonly double[] _ratios = new double[Pairs], _pairCompiled = new double[Pairs], _pairInterpreted = new double[Pairs];
    private int _pairs, _pairWindows, _windowPairs;
    private int _lastPairWindow = int.MinValue;
    private readonly double[] _rolling = new double[RollingPairs], _rollingCompiled = new double[RollingPairs], _rollingInterpreted = new double[RollingPairs];
    private int _rollingCount;

    /// <summary>Whether the contract lost once outside the start-up window, so that the next such loss gives it back.</summary>
    private bool _lostOnce;

    /// <summary>When the runner's bits come back, in <see cref="Environment.TickCount64"/> milliseconds; 0 for no recheck.</summary>
    private long _recheckAt;

    /// <summary>When the mark that has the contract's parts looked at for a visit under way is lifted; 0 once it is.</summary>
    private long _underWayUntil;

    /// <summary>The runner's bits a loss cleared, which the recheck sets again.</summary>
    private byte _maskedBits;

    /// <summary>Whether the check is on the list of those awaiting a recheck or a lift.</summary>
    private bool _listed;
    private int _checks = 1;
    private long _totalPairs;

    /// <summary>The pairs' median at the latest verdict: compiled time per opcode over the interpreter's.</summary>
    public double Ratio { get; private set; }

    /// <summary>The latest rolling median of a kept contract.</summary>
    public double RollingRatio { get; private set; }

    /// <summary>The medians of the latest verdict's or rolling block's pairs, interpreted and compiled, in <see cref="Stopwatch"/> ticks per opcode; 0 before any.</summary>
    private double _interpretedPerOp, _compiledPerOp;

    private sealed class Bucket
    {
        public long Ticks;
        public long Ops;
    }

    private static readonly object _registry = new();
    private static readonly List<IlCheck> _awaiting = [];
    private static int _awaitingCount;
    private static readonly long[] _kept = new long[2], _probations = new long[2], _masked = new long[2], _givenBack = new long[2], _rechecks = new long[2];

    /// <summary>A check of <paramref name="contract"/> for one runner.</summary>
    public IlCheck(IlEvm.IlContract contract, bool block)
    {
        Contract = contract;
        Block = block;
        int id = contract.Profile?.Id ?? RuntimeHelpers.GetHashCode(contract);
        Salt = (uint)id * 2 + (block ? 1u : 0u);
    }

    /// <summary>Gives a contract the checks of the runners whose check is on: at its publishing, shipped or promoted.</summary>
    public static void Arm(IlEvm.IlContract contract)
    {
        if (!IlEvmSettings.SelfCheck)
            return;
        if (IlEvmSettings.QueryCheck)
            contract.QueryCheck ??= new IlCheck(contract, block: false);
        if (IlEvmSettings.BlockCheck)
            contract.BlockCheck ??= new IlCheck(contract, block: true);
    }

    /// <summary>
    /// Whether the check takes the runner's admitted parts: from <see cref="IlEvmSettings.SelfCheckDelayMs"/> after the
    /// contract was attached, so the helpers compiled code calls have tiered up first; for a shipped contract on the
    /// read-only queries' runner, not before the start-up window (<see cref="IlEvmSettings.SelfCheckWarmUpMs"/>) is over,
    /// so a node's first calls run compiled throughout, with no switchback window and no probation.
    /// </summary>
    public bool Active
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _active || Activate();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Activate()
    {
        long now = Environment.TickCount64;
        long attached = Volatile.Read(ref Contract.AttachedAt);
        // A part of a body set no resolution attached: its first part starts the clock.
        if (attached == 0)
        {
            long seen = Interlocked.CompareExchange(ref Contract.AttachedAt, now, 0);
            attached = seen == 0 ? now : seen;
        }
        if (now - attached < IlEvmSettings.SelfCheckDelayMs)
            return false;
        // The list's contracts serve the queries from the first call; a check in the start-up window would time start-up,
        // and take a share of its calls to the interpreter for it.
        if (StartUpExempt && IlEvmSettings.InWarmUpAt(now))
            return false;
        IlCheckClock.EnsureStarted();
        _active = true;
        return true;
    }

    /// <summary>The check's state: <see cref="Trial"/>, <see cref="Kept"/>, <see cref="Probation"/>, <see cref="Masked"/> or <see cref="GivenBack"/>.</summary>
    public int State => _state;

    /// <summary>Whether the check leaves its contract alone in the start-up window: the read-only queries' runner's check of a shipped contract.</summary>
    private bool StartUpExempt => !Block && Contract.Origin == IlOrigin.Shipped;

    /// <summary>
    /// Whether the check takes an admitted part offered at <paramref name="tick"/>: every part of an interpreted window,
    /// and one compiled part in <see cref="TimeCompiledTrialEvery"/> or <see cref="TimeCompiledKeptEvery"/>, drawn to be
    /// timed, never in a window's settling tick. The rest run compiled untimed, on the runner's own path.
    /// </summary>
    /// <param name="word">The window's decision, <see cref="WindowWord"/>, for the part the check takes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TakesPart(IlThread thread, int tick, out long word)
    {
        word = WindowWord(tick / IlCheckClock.WindowTicks);
        // An interpreted window is a timing state's; chaos takes every part while the check times.
        if ((word & WordInterpreted) != 0 || IlGate.Chaos)
            return (word & WordTiming) != 0;
        return (word & WordTiming) != 0 && tick % IlCheckClock.WindowTicks != 0 &&
            (thread.Next() & ((word & WordSparse) != 0 ? TimeCompiledKeptEvery - 1 : TimeCompiledTrialEvery - 1)) == 0;
    }

    /// <summary>
    /// The decision for <paramref name="window"/>, as a word: <see cref="WordTiming"/> while the check times,
    /// <see cref="WordSparse"/> where compiled parts are timed at the kept rate, <see cref="WordInterpreted"/> for a
    /// switchback window.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long WindowWord(int window)
    {
        long word = _window;
        return (int)(word >> WordShift) == window + 1 ? word : Decide(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long Decide(int window)
    {
        int state = _state;
        bool timing = state is Trial or Kept;
        // Racy: a ring that fills during the window is read full in the next.
        bool sparse = state == Kept || (Block && Volatile.Read(ref _recentCount) >= RecentSamples);
        long word = ((long)(window + 1) << WordShift) | (timing ? WordTiming : 0) | (sparse ? WordSparse : 0) |
            (timing && IsInterpretedWindow(window, ShareOf(state)) ? WordInterpreted : 0);
        _window = word;
        return word;
    }

    /// <summary>Whether <paramref name="window"/> is one of this check's switchback windows at one in <paramref name="share"/>: hit by the hash, and the window before not.</summary>
    public bool IsInterpretedWindow(int window, int share) => Hit(window, share) && !Hit(window - 1, share);

    private bool Hit(int window, int share) => (Mix(window, Salt) & (uint)(Pow2(share) - 1)) == 0;

    /// <summary>Whether <paramref name="window"/> is a switchback window at the share the check's state takes now; for tests and diagnostics.</summary>
    public bool IsInterpretedWindow(int window)
    {
        int state = _state;
        return state is Trial or Kept && IsInterpretedWindow(window, ShareOf(state));
    }

    /// <summary>One window in this many is interpreted in <paramref name="state"/>: block processing's runner's share, else the queries' undecided or kept one.</summary>
    private int ShareOf(int state) => Block ? BlockShare : state == Kept ? KeptShare : TrialShare;

    /// <summary>A murmur-style mix of a window and a salt.</summary>
    internal static uint Mix(int window, uint salt)
    {
        uint h = (uint)window * 0x9E3779B1u ^ salt * 0x85EBCA6Bu;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    private static int Pow2(int share) => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(share, 1, 1 << 30));

    /// <summary>A compiled part, or one handed back with the interpreter's rest of it, ran <paramref name="opCodes"/> in <paramref name="ticks"/>: pooled, and a full bucket joins the ring of recent compiled samples.</summary>
    public void AddCompiled(long ticks, int opCodes)
    {
        if (opCodes <= 0)
            return;
        Interlocked.Add(ref _compiled.Ticks, ticks);
        if (Interlocked.Add(ref _compiled.Ops, opCodes) < BucketOps)
            return;
        lock (this)
        {
            long ops = Interlocked.Exchange(ref _compiled.Ops, 0);
            long pooled = Interlocked.Exchange(ref _compiled.Ticks, 0);
            // A bucket emptied by another thread a moment ago holds too little to time.
            if (ops < BucketOps / 2 || _state is not (Trial or Kept))
                return;
            _recent[_recentNext] = Math.Max(pooled, 1) / (double)ops;
            _recentNext = (_recentNext + 1) % RecentSamples;
            _recentCount = Math.Min(_recentCount + 1, RecentSamples);
        }
    }

    /// <summary>A part of a switchback window ran <paramref name="opCodes"/> on the interpreter in <paramref name="ticks"/>: pooled, and a full bucket makes a pair with the recent compiled samples.</summary>
    /// <param name="window">The window the part ran in, by which the pairs are counted.</param>
    public void AddInterpreted(long ticks, int opCodes, int window)
    {
        if (opCodes <= 0)
            return;
        Interlocked.Add(ref _interpreted.Ticks, ticks);
        if (Interlocked.Add(ref _interpreted.Ops, opCodes) < BucketOps)
            return;
        bool release;
        lock (this)
        {
            long ops = Interlocked.Exchange(ref _interpreted.Ops, 0);
            long pooled = Interlocked.Exchange(ref _interpreted.Ticks, 0);
            // A pair needs a compiled side of more than one sample, which the compiled windows give.
            if (ops < BucketOps / 2 || _state is not (Trial or Kept) || _recentCount < 2)
                return;
            if (window != _lastPairWindow)
            {
                _lastPairWindow = window;
                _pairWindows++;
                _windowPairs = 0;
            }

            if (_windowPairs >= MaxPairsPerWindow)
                return;
            _windowPairs++;
            release = Pair(Median(_recent, _recentCount), Math.Max(pooled, 1) / (double)ops);
        }

        // Outside the check's lock: releasing the code takes the contract's own.
        if (release)
            ReleaseLive(Contract);
    }

    /// <summary>Takes a pair of a compiled and an interpreted time per opcode, under the lock; true when it gave a live contract back, to release.</summary>
    private bool Pair(double compiled, double interpreted)
    {
        double ratio = compiled / interpreted;
        _totalPairs++;
        if (_state == Trial)
        {
            _ratios[_pairs] = ratio;
            _pairCompiled[_pairs] = compiled;
            _pairInterpreted[_pairs] = interpreted;
            int pairs = ++_pairs;
            ReadOnlySpan<double> ratios = _ratios.AsSpan(0, pairs);
            // Every pair clearly one way, over a few windows, decides early; anything closer runs to the full count.
            bool early = pairs >= EarlyPairs && _pairWindows >= EarlyWindows &&
                (ratios.IndexOfAnyExceptInRange(0, EarlyCompiled) < 0 || ratios.IndexOfAnyExceptInRange(EarlyInterpreted, double.MaxValue) < 0);
            if (!early && pairs < Pairs)
                return false;
            Ratio = Median(_ratios, pairs);
            _compiledPerOp = Median(_pairCompiled, pairs);
            _interpretedPerOp = Median(_pairInterpreted, pairs);
            if (Ratio <= KeepRatio)
            {
                _state = Kept;
                _rollingCount = 0;
                _window = 0;
                Interlocked.Increment(ref _kept[Runner]);
                return false;
            }

            return Lose();
        }

        // Kept: each block of rolling pairs is judged on its own.
        _rolling[_rollingCount] = ratio;
        _rollingCompiled[_rollingCount] = compiled;
        _rollingInterpreted[_rollingCount] = interpreted;
        if (++_rollingCount < RollingPairs)
            return false;
        _rollingCount = 0;
        RollingRatio = Median(_rolling, RollingPairs);
        _compiledPerOp = Median(_rollingCompiled, RollingPairs);
        _interpretedPerOp = Median(_rollingInterpreted, RollingPairs);
        return RollingRatio > RollingLossRatio && Lose();
    }

    /// <summary>Compiled code lost, under the lock: a probation, or a second loss's give-back; true when a live contract is to be released.</summary>
    private bool Lose()
    {
        long now = Environment.TickCount64;
        // While the process starts up a loss mostly measures start-up: re-checked soon, and it counts toward nothing; a
        // shipped contract stays on the queries' runner, its check from the start.
        bool warmUp = IlEvmSettings.InWarmUp;
        if (warmUp && StartUpExempt)
        {
            StartOver();
            return false;
        }

        if (warmUp || !_lostOnce)
        {
            if (!warmUp)
                _lostOnce = true;
            int recheck = warmUp ? IlEvmSettings.SelfCheckEarlyRecheckMs : Block ? BlockRecheckMs : IlEvmSettings.SelfCheckRecheckMs;
            TakeOff(Probation, now + Math.Max(0, recheck), now);
            Interlocked.Increment(ref _probations[Runner]);
            return false;
        }

        // A live contract goes when the runner of the source that promoted it finds it slower; one the other runner
        // finds slower, or a shipped one, keeps its code for the runner it still serves, and for a later recheck.
        if (Contract.Origin == (Block ? IlOrigin.LiveBlocks : IlOrigin.LiveRpc))
        {
            TakeOff(GivenBack, 0, now);
            Interlocked.Increment(ref _givenBack[Runner]);
            return true;
        }

        TakeOff(Masked, now + Math.Max(0, MaskedRecheckMs), now);
        Interlocked.Increment(ref _masked[Runner]);
        return false;
    }

    /// <summary>Clears the runner's bits of the run mask, marked as a check's doing, and awaits the recheck at <paramref name="recheckAt"/>, if any.</summary>
    private void TakeOff(int state, long recheckAt, long now)
    {
        byte before = UpdateMask(ref Contract.RunMask, clear: RunBits, set: OffBit);
        _maskedBits |= (byte)(before & RunBits);
        _state = state;
        _window = 0;
        _recheckAt = recheckAt;
        _underWayUntil = now + UnderWayMs;
        Await(this);
    }

    /// <summary>Ends a probation, or a masked contract's long wait: the bits back, and the check from the start.</summary>
    internal void Recheck()
    {
        lock (this)
        {
            if (_state is not (Probation or Masked))
                return;
            // The compiled samples are from before the loss; the parts since ran interpreted. Timing before the bits are
            // back, so the first parts that may run find the check at its start.
            StartOver();
            _recheckAt = 0;
            _underWayUntil = 0;
            UpdateMask(ref Contract.RunMask, clear: OffBit, set: _maskedBits);
            _maskedBits = 0;
        }

        Interlocked.Increment(ref _rechecks[Runner]);
    }

    /// <summary>Starts the check over, under its lock: its samples and pairs dropped, and a trial from the next window.</summary>
    private void StartOver()
    {
        Interlocked.Exchange(ref _interpreted.Ops, 0);
        Interlocked.Exchange(ref _interpreted.Ticks, 0);
        Interlocked.Exchange(ref _compiled.Ops, 0);
        Interlocked.Exchange(ref _compiled.Ticks, 0);
        _recentCount = _recentNext = 0;
        _pairs = _pairWindows = _windowPairs = _rollingCount = 0;
        _lastPairWindow = int.MinValue;
        _checks++;
        _state = Trial;
        _window = 0;
    }

    /// <summary>Lifts the mark that has the contract's parts looked at for a visit under way, leaving the runner's bits cleared.</summary>
    private void LiftUnderWay()
    {
        lock (this)
        {
            if (_underWayUntil == 0 || _state is Trial or Kept)
                return;
            _underWayUntil = 0;
            UpdateMask(ref Contract.RunMask, clear: OffBit, set: 0);
        }
    }

    /// <summary>
    /// Ends the probations and waits whose time has come at <paramref name="now"/>, in <see cref="Environment.TickCount64"/>
    /// milliseconds, and lifts the marks of losses whose transactions are over: the scheduler's maintenance between blocks,
    /// or the clock's timer once a window (<see cref="IlCheckClock.OnWindow"/>).
    /// </summary>
    /// <param name="rechecks">Whether the probations and waits due end here; false lifts the marks only.</param>
    /// <returns>How many checks started over.</returns>
    public static int RestoreDue(long now, bool rechecks = true)
    {
        if (Volatile.Read(ref _awaitingCount) == 0)
            return 0;
        List<IlCheck>? lift = null, due = null;
        lock (_registry)
        {
            for (int i = _awaiting.Count - 1; i >= 0; i--)
            {
                IlCheck check = _awaiting[i];
                long underWay = Volatile.Read(ref check._underWayUntil), recheckAt = Volatile.Read(ref check._recheckAt);
                bool lifting = underWay != 0 && underWay <= now, rechecking = rechecks && recheckAt != 0 && recheckAt <= now;
                if (lifting)
                    (lift ??= []).Add(check);
                if (rechecking)
                    (due ??= []).Add(check);
                // Kept on the list while it still waits for either.
                if ((underWay != 0 && !lifting) || (recheckAt != 0 && !rechecking))
                    continue;
                check._listed = false;
                _awaiting[i] = _awaiting[^1];
                _awaiting.RemoveAt(_awaiting.Count - 1);
            }

            Volatile.Write(ref _awaitingCount, _awaiting.Count);
        }

        // Outside the registry's lock: each takes its check's.
        foreach (IlCheck check in lift ?? [])
            check.LiftUnderWay();
        foreach (IlCheck check in due ?? [])
            check.Recheck();
        return due?.Count ?? 0;
    }

    private static void Await(IlCheck check)
    {
        lock (_registry)
        {
            if (check._listed)
                return;
            check._listed = true;
            _awaiting.Add(check);
            Volatile.Write(ref _awaitingCount, _awaiting.Count);
        }
    }

    /// <summary>The default release of a live contract given back: its profile lets go of it, its code goes, live profiling's budget gets its bytes back, and every state resolves again.</summary>
    /// <remarks>The scheduler's release job ends in it too, once it has demoted the profile, as does the scorer's demotion.</remarks>
    internal static void ReleaseNow(IlEvm.IlContract contract)
    {
        if (contract.Profile is { } profile && ReferenceEquals(profile.Live, contract))
            profile.Live = null;
        contract.Release();
        IlLive.Released(contract);
        IlRuntime.Shrank();
    }

    private int Runner => Block ? 1 : 0;

    /// <summary>The run-mask bits this check's runner runs the contract by.</summary>
    private byte RunBits => Block ? (byte)(IlRuntime.RunMain | IlRuntime.RunWorkers) : IlRuntime.RunQuery;

    /// <summary>The run-mask bit that marks the runner's bits as cleared by this check.</summary>
    private byte OffBit => Block ? IlRuntime.CheckOffBlock : IlRuntime.CheckOffQuery;

    /// <summary>Clears and sets bits of a run mask that the other runner's check may write at once; the mask before.</summary>
    private static byte UpdateMask(ref byte mask, byte clear, byte set)
    {
        byte seen = Volatile.Read(ref mask);
        while (true)
        {
            byte next = (byte)((seen & ~clear) | set);
            byte found = Interlocked.CompareExchange(ref mask, next, seen);
            if (found == seen)
                return seen;
            seen = found;
        }
    }

    private static double Median(double[] values, int count)
    {
        Span<double> sorted = stackalloc double[count];
        values.AsSpan(0, count).CopyTo(sorted);
        sorted.Sort();
        return (count & 1) == 1 ? sorted[count / 2] : (sorted[count / 2 - 1] + sorted[count / 2]) / 2;
    }

    /// <summary>What the check measured for live profiling's model: the pairs over its life, and the latest medians in nanoseconds per opcode, interpreted and compiled; 0 before a verdict.</summary>
    public (long Pairs, double InterpretedNsPerOp, double CompiledNsPerOp) Measured()
    {
        lock (this)
        {
            double scale = 1e9 / Stopwatch.Frequency;
            return (_totalPairs, _interpretedPerOp * scale, _compiledPerOp * scale);
        }
    }

    /// <summary>The verdicts so far in the process for one runner: kept, probations, contracts masked, contracts given back, and rechecks.</summary>
    public static (long Kept, long Probation, long Masked, long GivenBack, long Rechecked) Verdicts(bool block)
    {
        int runner = block ? 1 : 0;
        return (Interlocked.Read(ref _kept[runner]), Interlocked.Read(ref _probations[runner]), Interlocked.Read(ref _masked[runner]),
            Interlocked.Read(ref _givenBack[runner]), Interlocked.Read(ref _rechecks[runner]));
    }

    /// <summary>The checks awaiting a recheck or a lift now.</summary>
    public static int Awaiting => Volatile.Read(ref _awaitingCount);

    /// <summary>The checks for the exit line: each runner's verdicts, and how many checks await a recheck.</summary>
    public static string Describe()
    {
        string Runner(bool block)
        {
            (long kept, long probation, long masked, long givenBack, long rechecked) = Verdicts(block);
            bool on = IlEvmSettings.SelfCheck && (block ? IlEvmSettings.BlockCheck : IlEvmSettings.QueryCheck);
            return $"{(block ? "block" : "query")} {(on ? "on" : "off")}: {kept} kept, {probation} probation, {masked} masked, {givenBack} given back, {rechecked} rechecked";
        }

        return string.Create(CultureInfo.InvariantCulture, $"check {Runner(block: false)}; {Runner(block: true)}; {Awaiting} awaiting a recheck");
    }

    /// <summary>A check as tests read it.</summary>
    internal readonly record struct View(int State, int Pairs, int PairWindows, int Recent, long InterpretedOps, long CompiledOps, int Rolling,
        double Ratio, double RollingRatio, long RecheckAt, bool LostOnce, int Checks, byte MaskedBits, long TotalPairs);

    /// <summary>The check's state and samples, for tests.</summary>
    internal View ViewForTests()
    {
        lock (this)
            return new View(_state, _pairs, _pairWindows, _recentCount, Interlocked.Read(ref _interpreted.Ops), Interlocked.Read(ref _compiled.Ops),
                _rollingCount, Ratio, RollingRatio, _recheckAt, _lostOnce, _checks, _maskedBits, _totalPairs);
    }

    /// <summary>Forgets every check awaiting a recheck and the verdicts, and puts the hooks back, for tests.</summary>
    internal static void ResetForTests()
    {
        lock (_registry)
        {
            foreach (IlCheck check in _awaiting)
                check._listed = false;
            _awaiting.Clear();
            Volatile.Write(ref _awaitingCount, 0);
            for (int runner = 0; runner < 2; runner++)
                _kept[runner] = _probations[runner] = _masked[runner] = _givenBack[runner] = _rechecks[runner] = 0;
        }

        ReleaseLive = ReleaseNow;
        SchedulerRestores = false;
        Volatile.Write(ref LastSchedulerRestore, 0);
    }

    /// <summary>A knob from <c>NETHERMIND_ILEVM_CHECK_&lt;NAME&gt;</c>, else its default.</summary>
    private static double Knob(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_CHECK_" + name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : fallback;
}
