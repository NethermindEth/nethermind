// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>
/// The visit gate: of the frame parts a runner may run compiled, it sends to compiled code those of visits expected to
/// gain from it - long ones, ones warm on their thread, and ones already long on the interpreter - and interprets the rest.
/// </summary>
/// <remarks>
/// A part is one offer of a frame: its entry, or a resume after a nested call. A visit is every part of one code state
/// within one transaction on one thread. Its mode is chosen at its first part, from the lengths learned of earlier visits
/// with the same key and from when this thread last ran the code compiled. The gate only chooses between two exact paths,
/// so correctness never depends on its decisions; <see cref="Chaos"/> randomises them for the differential tests.
/// </remarks>
internal static class IlGate
{
    /// <summary>A decision: run the part compiled.</summary>
    public const int Compile = 0;

    /// <summary>A decision: leave the part to the interpreter, unreported.</summary>
    public const int Interpret = 1;

    /// <summary>A decision: leave the part to the interpreter, which reports its opcodes to the visit.</summary>
    public const int InterpretReport = 2;

    /// <summary>A visit that runs compiled from its next part on.</summary>
    internal const byte ModeCompiled = 1;

    /// <summary>A visit interpreted and reported until it reaches its switch point.</summary>
    internal const byte ModeRenting = 2;

    /// <summary>A visit left to the interpreter.</summary>
    internal const byte ModeSettled = 3;

    /// <summary>A visit of code its runner has nothing compiled for, which a counting runner samples for the live profile.</summary>
    internal const byte ModeSampled = 4;

    internal const int VisitWords = 16;

    /// <summary>
    /// The knobs, in <see cref="IlGateParams.Values"/> order, read from <c>NETHERMIND_ILEVM_GATE_BLOCK_&lt;NAME&gt;</c> and
    /// <c>NETHERMIND_ILEVM_GATE_QUERY_&lt;NAME&gt;</c>, and for the block runner from a list's <c>gate_block_&lt;name&gt;=</c>.
    /// </summary>
    internal static readonly string[] Names = ["VISIT_COLD_OPS", "SWITCH_OPS", "RENT_FACTOR", "WARM_OPS", "TX_GAP_OPS", "MIN_VISITS", "MEASURE_EVERY", "OPS_PER_US"];

    /// <summary>
    /// Block processing's runner before a list's calibration: block code mostly comes from the shared caches, so visits
    /// from 2,048 opcodes are admitted cold, and consecutive transactions on the same contracts are warm.
    /// </summary>
    internal static readonly IlGateParams BlockPrior = new(visitColdOps: 2048, switchOps: 1536, rentFactor: 4, warmOps: 4096, txGapOps: 2048, minVisits: 3, measureEvery: 8, opsPerMicrosecond: 250);

    /// <summary>The read-only queries' runner, which meets its code colder: memory-cold crossovers.</summary>
    internal static readonly IlGateParams QueryPrior = new(visitColdOps: 8192, switchOps: 6144, rentFactor: 4, warmOps: 4096, txGapOps: 4096, minVisits: 3, measureEvery: 8, opsPerMicrosecond: 250);

    private static IlGateParams _block = Overridden("BLOCK", BlockPrior);
    private static IlGateParams _query = Overridden("QUERY", QueryPrior);

    /// <summary>Block processing's runner's thresholds, replaced whole so a visit reads one set; read per visit and per transaction, never per part.</summary>
    public static IlGateParams Block
    {
        get => Volatile.Read(ref _block);
        set => Volatile.Write(ref _block, value);
    }

    /// <summary>The read-only queries' runner's thresholds: the prior under any environment override.</summary>
    public static IlGateParams Query
    {
        get => Volatile.Read(ref _query);
        set => Volatile.Write(ref _query, value);
    }

    /// <summary>For tests: <see cref="Stopwatch"/> ticks added to the wall time the gate reads per transaction, so a test moves it on rather than sleeping.</summary>
    internal static long ClockOffsetForTests;

    /// <summary>For correctness runs, <c>NETHERMIND_ILEVM_GATE_CHAOS=1</c>: each visit takes a random mode, switch point and measurement.</summary>
    public static bool Chaos { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_GATE_CHAOS") == "1";

    /// <summary>One transaction in this many, a power of two, is sampled on the queries' runner when it counts; <c>NETHERMIND_ILEVM_QUERY_SAMPLE_EVERY</c>.</summary>
    /// <remarks>Drawn per transaction, not per visit, so a transaction not drawn costs its parts no more than on a runner that does not count.</remarks>
    public static int QuerySampleEvery
    {
        get => 1 << _querySampleLog2;
        set => _querySampleLog2 = SampleLog2(value);
    }

    /// <summary>As <see cref="QuerySampleEvery"/>, for block processing's runner under <see cref="IlEvmPrefer.Blocks"/>; <c>NETHERMIND_ILEVM_BLOCK_SAMPLE_EVERY</c>.</summary>
    public static int BlockSampleEvery
    {
        get => 1 << _blockSampleLog2;
        set => _blockSampleLog2 = SampleLog2(value);
    }

    private static int _querySampleLog2 = SampleLog2(int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_QUERY_SAMPLE_EVERY"), out int query) ? query : 8);
    private static int _blockSampleLog2 = SampleLog2(int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_BLOCK_SAMPLE_EVERY"), out int block) ? block : 16);

    private static int SampleLog2(int every) => BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(every, 1, 1 << 16)));

    internal static int SampleLog2For(bool block) => block ? _blockSampleLog2 : _querySampleLog2;

    /// <summary>Once a key has <see cref="LearnAlwaysBelow"/> visits learned, one visit in this many is learned, so threads running one contract rarely write its line.</summary>
    public const int LearnEvery = 8;

    public const int LearnAlwaysBelow = 16;

    /// <summary>A frame entry's visit key: its selector and calldata length's log2 class, so array arguments of other sizes learn apart; odd, so never an empty word's 0.</summary>
    internal static uint EntryKey(ReadOnlySpan<byte> input)
    {
        uint selector = input.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(input) : 0xFFFF_FF00u | (uint)input.Length;
        return (selector * 0x9E3779B1u ^ (uint)BitOperations.Log2((uint)input.Length | 1) * 0x85EBCA6Bu) | 1u;
    }

    internal static uint ResumeKey(nint pc) => ((uint)pc * 0x9E3779B1u) | 0x8000_0001u;

    /// <summary>The log2 of <paramref name="ops"/> in 1/256ths, its fraction read linearly from the 8 bits below the leading one, as the thresholds need no more.</summary>
    internal static int Log2Q8(long ops)
    {
        if (ops <= 1)
            return 0;
        ulong value = (ulong)ops;
        int whole = BitOperations.Log2(value);
        int fraction = (int)(whole >= 8 ? (value >> (whole - 8)) & 0xFF : (value << (8 - whole)) & 0xFF);
        return (whole << 8) | fraction;
    }

    /// <summary>The opcodes whose <see cref="Log2Q8"/> is <paramref name="q8"/>; exact for counts with no bits past its 8 fraction bits.</summary>
    internal static long Exp2Q8(int q8)
    {
        if (q8 <= 0)
            return 1;
        int whole = Math.Min(q8 >> 8, 40);
        ulong mantissa = 256u + (uint)(q8 & 0xFF);
        return whole >= 8 ? (long)(mantissa << (whole - 8)) : (long)(mantissa >> (8 - whole));
    }

    /// <summary>Sets block processing's thresholds from a list's <c>gate_block_*=</c> calibration lines, over the prior and under any environment override.</summary>
    public static void Calibrate(IReadOnlyList<string> calibration)
    {
        Dictionary<string, string> tokens = IlEvmPrecompileList.CalibrationValues(calibration);
        int[] values = BlockPrior.Values();
        for (int i = 0; i < Names.Length; i++)
        {
            if (tokens.TryGetValue("gate_block_" + Names[i].ToLowerInvariant(), out string? text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 0 && value <= int.MaxValue)
                values[i] = (int)Math.Round(value);
        }

        Block = Overridden("BLOCK", new IlGateParams(values));
    }

    private static IlGateParams Overridden(string runner, IlGateParams prior)
    {
        int[] values = prior.Values();
        for (int i = 0; i < Names.Length; i++)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable($"NETHERMIND_ILEVM_GATE_{runner}_{Names[i]}"), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                values[i] = value;
        }

        return new IlGateParams(values);
    }

    private static readonly object _registry = new();
    private static readonly List<IlThread> _threads = [];
    private static IlGateCounters _retired;

    /// <summary>Keeps a thread's counters for the totals; threads that ended are let go, their counts kept.</summary>
    internal static void Register(IlThread thread)
    {
        lock (_registry)
        {
            Prune();
            _threads.Add(thread);
        }
    }

    private static void Prune()
    {
        for (int i = _threads.Count - 1; i >= 0; i--)
        {
            if (_threads[i].Owner.IsAlive)
                continue;
            _retired.Add(in _threads[i].Counters);
            _threads.RemoveAt(i);
        }
    }

    /// <summary>The counters of every thread that offered the gate a part, summed.</summary>
    public static IlGateCounters Totals()
    {
        lock (_registry)
        {
            Prune();
            IlGateCounters sum = _retired;
            foreach (IlThread thread in _threads)
                sum.Add(in thread.Counters);
            return sum;
        }
    }

    /// <summary>The gate for the exit line.</summary>
    public static string Describe()
    {
        IlGateCounters c = Totals();
        IlGateParams block = Block, query = Query;
        string extra = (c.VisitsUngated > 0 ? $", {c.VisitsUngated} ungated" : "") + (c.VisitsChaos > 0 ? $", {c.VisitsChaos} chaos" : "");
        return string.Create(CultureInfo.InvariantCulture,
            $"gate {(IlEvmSettings.Gate ? "on" : "off")} (block {block.VisitColdOps}/{block.SwitchOps}, query {query.VisitColdOps}/{query.SwitchOps} ops cold/switch): " +
            $"visits {c.VisitsLong} long, {c.VisitsWarm} warm, {c.VisitsSeeded} seeded, {c.VisitsRenting} renting ({c.VisitsSwitched} switched), {c.VisitsSettled} settled ({c.VisitsMeasured} measured), {c.VisitsSampled} sampled{extra}; " +
            $"parts {c.PartsCompiled} compiled, {c.PartsInterpreted} interpreted, {c.PartsSwitchedBack} switched back by a check's window; " +
            $"check samples {c.SamplesCompiled} compiled, {c.SamplesInterpreted} interpreted");
    }
}

/// <summary>A runner's gate thresholds, fixed once made: the knobs, and the log2 forms the visits compare with.</summary>
internal sealed class IlGateParams
{
    public IlGateParams(int visitColdOps, int switchOps, int rentFactor, int warmOps, int txGapOps, int minVisits, int measureEvery, int opsPerMicrosecond)
    {
        VisitColdOps = Math.Max(1, visitColdOps);
        SwitchOps = Math.Max(1, switchOps);
        RentFactor = Math.Max(1, rentFactor);
        WarmOps = Math.Max(0, warmOps);
        TxGapOps = Math.Max(0, txGapOps);
        MinVisits = Math.Clamp(minVisits, 0, 255);
        // A power of two, so choosing a measured visit is one draw and a mask.
        MeasureEvery = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(measureEvery, 1, 1 << 20));
        OpsPerMicrosecond = Math.Max(0, opsPerMicrosecond);
        VisitColdLog = IlGate.Log2Q8(VisitColdOps);
        SwitchLog = IlGate.Log2Q8(SwitchOps);
        RentLog = IlGate.Log2Q8(RentFactor);
        MeasureMask = (uint)MeasureEvery - 1;
        MeasureLog2 = (byte)BitOperations.Log2((uint)MeasureEvery);
        OpsPerTick = OpsPerMicrosecond * 1e6 / Stopwatch.Frequency;
    }

    internal IlGateParams(int[] values) : this(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7])
    {
    }

    /// <summary>The geometric mean of a key's learned visits, in opcodes, from which its visits run compiled from their first part.</summary>
    public readonly int VisitColdOps;

    /// <summary>The opcodes after which a renting visit switches to compiled code.</summary>
    public readonly int SwitchOps;

    /// <summary>How far below the switch point a key's mean may lie and its visits still rent: a visit that long may reach twice its mean.</summary>
    public readonly int RentFactor;

    /// <summary>The work-clock opcodes after a compiled run within which the code is taken to be still in this core's caches.</summary>
    public readonly int WarmOps;

    /// <summary>The least a new transaction moves the work clock on: a request's handling, or a block's work between its transactions.</summary>
    public readonly int TxGapOps;

    /// <summary>The visits a key must have learned before its length decides.</summary>
    public readonly int MinVisits;

    /// <summary>One settled visit in this many is reported and learned from, a power of two.</summary>
    public readonly int MeasureEvery;

    /// <summary>The work-clock opcodes a microsecond of wall time between transactions counts for: the interpreter's pace.</summary>
    public readonly int OpsPerMicrosecond;

    public readonly int VisitColdLog, SwitchLog, RentLog;

    /// <summary><see cref="MeasureEvery"/> less one, and its log2: a measured visit's weight in the live counts.</summary>
    public readonly uint MeasureMask;

    public readonly byte MeasureLog2;

    /// <summary><see cref="OpsPerMicrosecond"/> per <see cref="Stopwatch"/> tick.</summary>
    public readonly double OpsPerTick;

    internal int[] Values() => [VisitColdOps, SwitchOps, RentFactor, WarmOps, TxGapOps, MinVisits, MeasureEvery, OpsPerMicrosecond];

    public IlGateParams With(int? visitColdOps = null, int? switchOps = null, int? rentFactor = null, int? warmOps = null, int? txGapOps = null,
        int? minVisits = null, int? measureEvery = null, int? opsPerMicrosecond = null) =>
        new(visitColdOps ?? VisitColdOps, switchOps ?? SwitchOps, rentFactor ?? RentFactor, warmOps ?? WarmOps, txGapOps ?? TxGapOps,
            minVisits ?? MinVisits, measureEvery ?? MeasureEvery, opsPerMicrosecond ?? OpsPerMicrosecond);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"cold {VisitColdOps}, switch {SwitchOps}, rent x{RentFactor}, warm {WarmOps}, gap {TxGapOps}, min visits {MinVisits}, measure 1/{MeasureEvery}, {OpsPerMicrosecond} ops/us");
}

/// <summary>A thread's gate counters: visits by the mode they started in, and parts by the path the gate sent them down.</summary>
internal struct IlGateCounters
{
    /// <summary>Visits compiled from their first part for their learned length, and for this thread's recent compiled run.</summary>
    public long VisitsLong, VisitsWarm;

    /// <summary>Visits compiled because the list vouches for their contract on the queries' runner (<see cref="IlCodeProfile.QuerySeeded"/>).</summary>
    public long VisitsSeeded;

    public long VisitsRenting, VisitsSwitched;

    public long VisitsSettled, VisitsMeasured;

    public long VisitsSampled;

    public long VisitsUngated, VisitsChaos;

    public long PartsCompiled, PartsInterpreted;

    /// <summary>Admitted parts that a switchback check's interpreted window sent to the interpreter.</summary>
    public long PartsSwitchedBack;

    /// <summary>Parts a switchback check timed: compiled (with the interpreter's rest of a handed-back part) and interpreted.</summary>
    public long SamplesCompiled, SamplesInterpreted;

    public void Add(in IlGateCounters other)
    {
        VisitsLong += other.VisitsLong;
        VisitsWarm += other.VisitsWarm;
        VisitsSeeded += other.VisitsSeeded;
        VisitsRenting += other.VisitsRenting;
        VisitsSwitched += other.VisitsSwitched;
        VisitsSettled += other.VisitsSettled;
        VisitsMeasured += other.VisitsMeasured;
        VisitsSampled += other.VisitsSampled;
        VisitsUngated += other.VisitsUngated;
        VisitsChaos += other.VisitsChaos;
        PartsCompiled += other.PartsCompiled;
        PartsInterpreted += other.PartsInterpreted;
        PartsSwitchedBack += other.PartsSwitchedBack;
        SamplesCompiled += other.SamplesCompiled;
        SamplesInterpreted += other.SamplesInterpreted;
    }
}

/// <summary>
/// A code's learned visit lengths for one runner: 8 sets of 2 ways, one word per key holding the key (bits 0-31), its
/// visits learned (32-39) and their <see cref="IlGate.Log2Q8"/> mean (40-55), a geometric mean one outlier does not drag.
/// </summary>
/// <remarks>Each update is one 64-bit store, so a reader never sees a mix of words; two threads learning at once lose one update, which only slows learning.</remarks>
[InlineArray(IlGate.VisitWords)]
internal struct IlVisitStats
{
    private ulong _word;

    /// <summary>What was learned of <paramref name="key"/>: how many visits, and the log2 mean of their opcodes; (0, 0) for nothing.</summary>
    public static (int Count, int LogMean) Get(ref IlVisitStats stats, uint key)
    {
        int way = Way(key);
        ulong first = stats[way];
        if ((uint)first == key)
            return (Count(first), LogMean(first));
        ulong second = stats[way + 1];
        return (uint)second == key ? (Count(second), LogMean(second)) : (0, 0);
    }

    /// <summary>Learns a visit of <paramref name="ops"/> opcodes: a plain mean over the first 8, then one that moves an eighth of the way.</summary>
    public static void Learn(ref IlVisitStats stats, uint key, long ops)
    {
        int way = Way(key);
        ulong first = stats[way], second = stats[way + 1];
        int at;
        ulong word;
        if ((uint)first == key)
        {
            at = way;
            word = first;
        }
        else if ((uint)second == key)
        {
            at = way + 1;
            word = second;
        }
        else
        {
            at = Count(first) <= Count(second) ? way : way + 1;
            word = 0;
        }

        int count = Count(word);
        int mean = LogMean(word);
        int x = IlGate.Log2Q8(ops);
        mean = count == 0 ? x : count < 8 ? (mean * count + x) / (count + 1) : mean + (x - mean) / 8;
        count = Math.Min(count + 1, 255);
        ulong learned = key | ((ulong)count << 32) | ((ulong)(uint)mean << 40);
        // Unchanged words are not written, so threads on a busy contract stop writing its line once its count and mean settle.
        if (learned != word)
            stats[at] = learned;
    }

    private static int Way(uint key) => (int)((key * 0x9E3779B1u) >> 29) << 1;

    private static int Count(ulong word) => (int)(word >> 32) & 0xFF;

    private static int LogMean(ulong word) => (int)(word >> 40) & 0xFFFF;
}

/// <summary>A runner's learned visit lengths for one code, apart from the profile's other fields, made when the runner first learns.</summary>
internal sealed class IlVisitBox
{
    public IlVisitStats Stats;
}

/// <summary>The visit gate's side of a code profile: the visit lengths each runner learned.</summary>
internal sealed partial class IlCodeProfile
{
    /// <summary>What block processing's runner learned of the code's visits; null until it folds its first.</summary>
    /// <remarks>Each runner learns apart: block code and calls meet their code differently warm, and live profiling reads only its own source's counts.</remarks>
    public IlVisitBox? BlockVisits;

    public IlVisitBox? QueryVisits;

    public IlVisitBox VisitsFor(bool block)
    {
        ref IlVisitBox? field = ref block ? ref BlockVisits : ref QueryVisits;
        IlVisitBox? box = Volatile.Read(ref field);
        if (box is not null)
            return box;
        IlVisitBox created = new();
        return Interlocked.CompareExchange(ref field, created, null) ?? created;
    }

    public IlVisitBox? PeekVisits(bool block) => block ? BlockVisits : QueryVisits;
}

/// <summary>A thread's side of the visit gate: its visits, its work clock, the part it waits to hear from, and its counters.</summary>
/// <remarks>
/// The only thread-static the gate reads on a part's path. The visit table, 256 sets of 2 ways (about 24 KB), is keyed by
/// code state, which the code infos' slots hold anyway, and never holds a code info: a released contract's code goes with
/// its runners whatever holds the state. A part costs no timestamp and no write outside this object unless a switchback
/// check times it (<see cref="IlCheck"/>). A visit's end is learned from when its way is next taken.
/// </remarks>
internal sealed partial class IlThread
{
    [ThreadStatic] private static IlThread? _current;

    private const int Sets = 256;

    /// <summary>The check sides a thread pools timed samples for at once, one per way, a power of two.</summary>
    private const int PoolWays = 8;

    private const int PoolLog2 = 3;

    private static int _stateHashes;

    /// <summary>A transaction's top-level frame enters, whatever then runs it: a counting runner draws whether the transaction is sampled.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TopLevelEntry(bool block)
    {
        bool counting = block ? IlRuntime.BlocksCounting : IlRuntime.QueryCounting;
        IlThread? thread = _current;
        if (thread is null)
        {
            // A thread that never met the gate has no transaction to end; one of a counting runner gets its gate here.
            if (!counting)
                return;
            thread = Create();
        }

        // Ends the transaction before: the next part sees a new machine and starts another.
        thread._vm = null;
        int log2 = IlGate.SampleLog2For(block);
        thread._sampleLog2 = (byte)log2;
        thread._sampleTx = counting && (thread.Next() & ((1u << log2) - 1)) == 0;
    }

    /// <summary>Whether the transaction under way on this thread was drawn for live profiling.</summary>
    public static bool SamplingTransaction
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current is { } thread && thread._sampleTx;
    }

    public static IlThread Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current ?? Create();
    }

    public static IlThread? CurrentOrNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IlThread Create()
    {
        IlThread thread = new();
        IlGate.Register(thread);
        return _current = thread;
    }

    /// <summary>The next code state's <see cref="IlEvm.IlCodeState.GateHash"/>: consecutive numbers, which the sets' multiplicative hash spreads.</summary>
    internal static uint NextStateHash() => (uint)Interlocked.Increment(ref _stateHashes);

    /// <summary>One code state's latest visit on this thread.</summary>
    private struct Visit
    {
        /// <summary>The state; null for a way never taken.</summary>
        public IlEvm.IlCodeState? Key;
        /// <summary>The work clock when this thread last ran the state compiled, kept across its visits; 0 for never.</summary>
        public long LastCompiled;
        /// <summary>The transaction of the visit; 0 for none.</summary>
        public int Tx;
        /// <summary>The opcodes the visit's parts ran so far, compiled and reported.</summary>
        public int Ops;
        public int Parts;
        public int SwitchAt;
        public uint EntryKey;
        public byte Mode;
        /// <summary>The log2 of the visits a measured one stands for in the live counts.</summary>
        public byte WeightLog2;
        /// <summary>Whether every part is counted, so the visit's length is known and is learned.</summary>
        public bool Measured;
        /// <summary>Whether a measured visit's length is learned (<see cref="IlGate.LearnEvery"/>).</summary>
        public bool Learns;
        /// <summary>Whether the runner counted the visit for live profiling.</summary>
        public bool Counting;
        public bool Block;
        /// <summary>The live counts' kind: block processing's own thread, or any thread of the read-only queries' runner.</summary>
        public bool Main;
    }

    private readonly Visit[] _v = new Visit[Sets * 2];

    /// <summary>The thread that made this gate, so the totals can let go of it once it ends.</summary>
    public readonly Thread Owner = Thread.CurrentThread;

    /// <summary>The thread's counters, which <see cref="IlGate.Totals"/> sums; plain increments, read racily.</summary>
    public IlGateCounters Counters;

    /// <summary>The machine of the transaction under way, and its opcode count at the last part.</summary>
    private object? _vm;
    private int _ops;

    private int _tx = 1;

    /// <summary>The visit of the latest part the gate admitted, for <see cref="AfterCompiled"/>.</summary>
    private int _cur;

    /// <summary>The work clock at the transaction's start; when, and at what clock, the last transaction started.</summary>
    private long _base, _txTicks, _txClock;

    private uint _random = (uint)Environment.CurrentManagedThreadId * 2654435761u | 1;

    /// <summary>The code whose part the interpreter runs reported, and the visit it reports to.</summary>
    private CodeInfo? _reportCode;
    private int _reportVisit, _reportTx;

    /// <summary>The check whose timed sample the armed report finishes, or null, with what <see cref="ArmTimed"/> gave.</summary>
    private IlCheck? _timed;
    private long _timedStart;
    private int _timedWindow, _timedCompiledOps, _timedEvents;
    private bool _timedInterpreted;

    /// <summary>Whether the transaction under way is sampled for live profiling, and the log2 of the transactions it stands for.</summary>
    private bool _sampleTx;
    private byte _sampleLog2;

    /// <summary>A check's timed samples this thread pooled and has not handed it yet: one side of it, and for the interpreted side one window.</summary>
    private struct Pooled
    {
        public IlCheck? Check;
        public long Ticks;
        public int Ops;
        public int Window;
        public bool Interpreted;
    }

    private readonly Pooled[] _pooled = new Pooled[PoolWays];

    /// <summary>Decides how a part of a contract its runner may run on this thread runs, as an <see cref="IlGate"/> decision.</summary>
    /// <remarks>A compiled part must be followed by <see cref="AfterCompiled"/>; a reported one arms the report <see cref="FinishReport"/> completes.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Offer(VirtualMachine<EthereumGasPolicy> vm, IlEvm.IlCodeState state, CodeInfo code, nint pc, bool block, bool counting)
    {
        DetectTx(vm, pc, block);
        int i = SlotOf(state);
        ref Visit v = ref At(i);
        _cur = i;
        if (v.Tx != _tx)
            StartVisit(ref v, vm, state, pc, block, counting);
        v.Parts++;
        switch (v.Mode)
        {
            case IlGate.ModeCompiled:
                Counters.PartsCompiled++;
                return IlGate.Compile;
            case IlGate.ModeRenting:
                if (v.Ops >= v.SwitchAt)
                {
                    // Once only: from here the visit runs compiled to its end.
                    v.Mode = IlGate.ModeCompiled;
                    Counters.VisitsSwitched++;
                    Counters.PartsCompiled++;
                    return IlGate.Compile;
                }

                ArmReport(code, i);
                Counters.PartsInterpreted++;
                return IlGate.InterpretReport;
            default:
                // Settled, or a sampled visit whose code became runnable since it started: interpreted to its end.
                Counters.PartsInterpreted++;
                if (!v.Measured)
                    return IlGate.Interpret;
                ArmReport(code, i);
                return IlGate.InterpretReport;
        }
    }

    /// <summary>Counts a part of code its runner has nothing compiled for, in a sampled transaction; true when the interpreter should report it.</summary>
    public bool Sample(VirtualMachine<EthereumGasPolicy> vm, IlEvm.IlCodeState state, CodeInfo code, OpcodeBodySet bodies, nint pc, bool block)
    {
        DetectTx(vm, pc, block);
        int i = SlotOf(state);
        ref Visit v = ref At(i);
        if (v.Tx != _tx)
            StartSample(ref v, vm, state, bodies, pc, block);
        v.Parts++;
        if (!v.Measured)
            return false;
        ArmReport(code, i);
        return true;
    }

    /// <summary>A compiled part ran: its visit counts its opcodes, the thread its run; a part handed back mid-way waits for the report of the rest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AfterCompiled(VirtualMachine<EthereumGasPolicy> vm, int before, CodeInfo code, bool handedBack)
    {
        ref Visit v = ref At(_cur);
        int ops = vm.OpCodeCount;
        v.Ops += ops - before;
        _ops = ops;
        v.LastCompiled = _base + ops;
        if (handedBack)
            ArmReport(code, _cur);
    }

    /// <summary>An admitted part a switchback check's interpreted window sends to the interpreter: reported, so its visit still counts it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SwitchedBack(CodeInfo code)
    {
        ArmReport(code, _cur);
        Counters.PartsCompiled--;
        Counters.PartsSwitchedBack++;
    }

    /// <summary>Has the armed report finish a sample for <paramref name="check"/>: an interpreted window's part, or a part compiled code handed back.</summary>
    public void ArmTimed(IlCheck check, int window, bool interpreted, long start, int compiledOps, int events)
    {
        _timed = check;
        _timedWindow = window;
        _timedInterpreted = interpreted;
        _timedStart = start;
        _timedCompiledOps = compiledOps;
        _timedEvents = events;
    }

    public void AddCompiledSample(IlCheck check, long ticks, int opCodes)
    {
        Counters.SamplesCompiled++;
        Pool(check, ticks, opCodes, interpreted: false, window: 0);
    }

    /// <summary>Pools a timed sample and hands it to <paramref name="check"/> a bucket at a time, so threads timing one contract seldom write its buckets.</summary>
    private void Pool(IlCheck check, long ticks, int opCodes, bool interpreted, int window)
    {
        if (opCodes <= 0)
            return;
        // A multiplicative hash of the check's salt and side, so consecutive salts spread.
        ref Pooled pool = ref _pooled[(int)(((check.Salt * 2 + (interpreted ? 1u : 0u)) * 0x9E3779B1u) >> (32 - PoolLog2))];
        if (!ReferenceEquals(pool.Check, check) || pool.Interpreted != interpreted || pool.Window != window)
        {
            Flush(ref pool);
            pool.Check = check;
            pool.Interpreted = interpreted;
            pool.Window = window;
        }

        pool.Ticks += ticks;
        pool.Ops += opCodes;
        if (pool.Ops >= IlCheck.BucketOps)
            Flush(ref pool);
    }

    private static void Flush(ref Pooled pool)
    {
        if (pool.Check is { } check && pool.Ops > 0)
        {
            if (pool.Interpreted)
                check.AddInterpreted(pool.Ticks, pool.Ops, pool.Window);
            else
                check.AddCompiled(pool.Ticks, pool.Ops);
        }

        pool = default;
    }

    internal int PooledOps(IlCheck check, bool interpreted)
    {
        foreach (Pooled pool in _pooled)
        {
            if (ReferenceEquals(pool.Check, check) && pool.Interpreted == interpreted)
                return pool.Ops;
        }

        return 0;
    }

    internal void PoolInterpretedForTests(IlCheck check, long ticks, int opCodes, int window) => Pool(check, ticks, opCodes, interpreted: true, window);

    /// <summary>A check just took this runner off the part's contract: a visit of it under way here misses parts now, so it is neither learned from nor counted.</summary>
    /// <remarks>A visit starts only on a part its runner may run, so a sampled one, or one of an earlier transaction, is whole.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unmeasure(VirtualMachine<EthereumGasPolicy> vm, IlEvm.IlCodeState state, nint pc, bool block)
    {
        DetectTx(vm, pc, block);
        int way = (int)((state.GateHash * 0x9E3779B1u) >> 24) << 1;
        for (int i = way; i <= way + 1; i++)
        {
            ref Visit v = ref At(i);
            if (ReferenceEquals(v.Key, state) && v.Tx == _tx && v.Mode != IlGate.ModeSampled)
                v.Measured = false;
        }
    }

    /// <summary>The interpreter ran <paramref name="opCodes"/> opcodes of a part it was asked to report: they count toward the visit that asked.</summary>
    /// <remarks>Called first when the machine reports; a report for other code than the armed one, as after a part whose interpreter run threw, is dropped.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FinishReport(CodeInfo code, int opCodes)
    {
        if (!ReferenceEquals(_reportCode, code))
        {
            _reportCode = null;
            _timed = null;
            return;
        }

        _reportCode = null;
        // The clock and the next part's count go on from where the interpreter stopped.
        _ops += opCodes;
        ref Visit v = ref At(_reportVisit);
        if (v.Tx == _reportTx)
            v.Ops += opCodes;
        if (_timed is not null)
            FinishTimed(opCodes);
    }

    /// <summary>The interpreter's run of a timed part ended: an interpreted window's sample, or a handed-back part's whole, unless it met a chunk's JIT.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FinishTimed(int opCodes)
    {
        long ticks = Stopwatch.GetTimestamp() - _timedStart;
        IlCheck check = _timed!;
        _timed = null;
        if (_timedInterpreted)
        {
            Counters.SamplesInterpreted++;
            Pool(check, ticks, opCodes, interpreted: true, _timedWindow);
        }
        else if (Volatile.Read(ref check.Contract.ChunkEvents) == _timedEvents)
        {
            Counters.SamplesCompiled++;
            Pool(check, ticks, _timedCompiledOps + opCodes, interpreted: false, window: 0);
        }
    }

    /// <summary>A part arrives: a new transaction starts when the opcode count went back, the machine changed, or a top-level entry was reported.</summary>
    /// <remarks>Drops any report still armed: the machine reports a part before it offers the next, so one armed now is of a part whose interpreter run threw.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DetectTx(VirtualMachine<EthereumGasPolicy> vm, nint pc, bool block)
    {
        _reportCode = null;
        int ops = vm.OpCodeCount;
        if (ops < _ops || !ReferenceEquals(vm, _vm))
            NewTransaction(vm, block);
        _ops = ops;
    }

    /// <summary>
    /// Moves the work clock on by the last transaction's opcodes and the larger of the fixed gap and the wall time since the
    /// last one started, at the interpreter's pace: idle time and request handling cool the caches too. The gate's only timestamp.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NewTransaction(VirtualMachine<EthereumGasPolicy> vm, bool block)
    {
        IlGateParams p = block ? IlGate.Block : IlGate.Query;
        long now = Stopwatch.GetTimestamp() + IlGate.ClockOffsetForTests;
        long byOps = _base + _ops + p.TxGapOps;
        long byTime = _txClock + (long)((now - _txTicks) * p.OpsPerTick);
        _base = _txTicks == 0 ? byOps : Math.Max(byOps, byTime);
        _txTicks = now;
        _txClock = _base;
        _vm = vm;
        _ops = 0;
        // 0 marks a way that holds no visit.
        if (++_tx == 0)
            _tx = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SlotOf(IlEvm.IlCodeState state)
    {
        int way = (int)((state.GateHash * 0x9E3779B1u) >> 24) << 1;
        ref Visit first = ref At(way);
        if (ReferenceEquals(first.Key, state))
            return way;
        if (ReferenceEquals(Unsafe.Add(ref first, 1).Key, state))
            return way + 1;
        return Install(state, way);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Install(IlEvm.IlCodeState state, int way)
    {
        ref Visit first = ref At(way);
        ref Visit second = ref At(way + 1);
        // A way never taken, else the one visited longer ago; its last visit is learned from before it goes.
        int victim = first.Key is null ? way
            : second.Key is null ? way + 1
            : (uint)(_tx - first.Tx) >= (uint)(_tx - second.Tx) ? way : way + 1;
        ref Visit v = ref At(victim);
        Fold(ref v);
        v = default;
        v.Key = state;
        return victim;
    }

    /// <summary>Chooses the mode of a visit of compiled code at its first part, after learning from the state's last visit.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartVisit(ref Visit v, VirtualMachine<EthereumGasPolicy> vm, IlEvm.IlCodeState state, nint pc, bool block, bool counting)
    {
        Fold(ref v);
        long lastCompiled = v.LastCompiled;
        v.Tx = _tx;
        v.Ops = 0;
        v.Parts = 0;
        v.SwitchAt = 0;
        v.WeightLog2 = counting ? _sampleLog2 : (byte)0;
        v.Measured = true;
        v.Learns = true;
        v.Counting = counting;
        v.Block = block;
        v.Main = !block || ProcessingThread.IsBlockProcessingThread;
        v.EntryKey = pc == 0 ? IlGate.EntryKey(vm.VmState.Env.InputData.Span) : IlGate.ResumeKey(pc);

        // The gate-off arm keeps the bookkeeping, so it differs from the gated one only in what runs compiled.
        if (!IlEvmSettings.Gate)
        {
            v.Mode = IlGate.ModeCompiled;
            Counters.VisitsUngated++;
            return;
        }

        if (IlGate.Chaos)
        {
            StartChaos(ref v);
            return;
        }

        IlGateParams p = block ? IlGate.Block : IlGate.Query;
        int coldLog = p.VisitColdLog, switchLog = p.SwitchLog;
        long switchOps = p.SwitchOps;
        IlCodeProfile? profile = state.Profile;
        // A list entry's own block threshold (minv=); the switch point stays three quarters of it, as in the prior.
        if (block && profile is not null && profile.BlockMinVisitLog2 > 0)
        {
            int minv = Math.Clamp((int)profile.BlockMinVisitLog2, 2, 30);
            coldLog = minv << 8;
            switchOps = 3L << (minv - 2);
            switchLog = IlGate.Log2Q8(switchOps);
        }

        // The list vouches for the contract on the queries' runner: qminv=0 runs every visit compiled and unmeasured, so
        // first calls run as later ones; else unlearned keys run compiled rather than renting, learned ones meet the entry's threshold.
        bool seeded = false;
        if (!block && profile is { QuerySeeded: true })
        {
            if (profile.QueryMinVisitLog2 == 0)
            {
                v.Mode = IlGate.ModeCompiled;
                v.Measured = false;
                Counters.VisitsSeeded++;
                return;
            }

            int qminv = Math.Clamp((int)profile.QueryMinVisitLog2, 2, 30);
            coldLog = qminv << 8;
            switchOps = 3L << (qminv - 2);
            switchLog = IlGate.Log2Q8(switchOps);
            seeded = true;
        }

        (int count, int log) = profile?.PeekVisits(block) is { } box ? IlVisitStats.Get(ref box.Stats, v.EntryKey) : (0, 0);
        bool learns = count < IlGate.LearnAlwaysBelow || (Next() & (IlGate.LearnEvery - 1)) == 0;
        long now = _base + _ops;
        if (count >= p.MinVisits && log >= coldLog)
        {
            v.Mode = IlGate.ModeCompiled;
            v.Learns = learns;
            Counters.VisitsLong++;
        }
        else if (lastCompiled != 0 && now - lastCompiled <= p.WarmOps)
        {
            v.Mode = IlGate.ModeCompiled;
            v.Learns = learns;
            Counters.VisitsWarm++;
        }
        else if (count < p.MinVisits && seeded)
        {
            v.Mode = IlGate.ModeCompiled;
            Counters.VisitsSeeded++;
        }
        else if (count < p.MinVisits)
        {
            v.Mode = IlGate.ModeRenting;
            v.SwitchAt = (int)Math.Min(switchOps, int.MaxValue);
            Counters.VisitsRenting++;
        }
        else if (log + p.RentLog >= switchLog)
        {
            // Visits near the switch point rent too, but switch only past twice their mean: an outlier switches, a typical
            // visit never pays a cold entry at its end. Block processing's, which are many, rent one in MeasureEvery for the
            // rest, which settle unreported, so a key whose typical visit never switches does not report every part.
            if (block && (Next() & p.MeasureMask) != 0)
            {
                v.Mode = IlGate.ModeSettled;
                v.Measured = false;
                Counters.VisitsSettled++;
                return;
            }

            v.Mode = IlGate.ModeRenting;
            v.SwitchAt = (int)Math.Min(Math.Max(switchOps, 2 * IlGate.Exp2Q8(log)), int.MaxValue);
            if (block)
                v.WeightLog2 += p.MeasureLog2;
            else
                v.Learns = learns;
            Counters.VisitsRenting++;
        }
        else
        {
            v.Mode = IlGate.ModeSettled;
            v.Measured = (Next() & p.MeasureMask) == 0;
            v.WeightLog2 += p.MeasureLog2;
            Counters.VisitsSettled++;
            if (v.Measured)
                Counters.VisitsMeasured++;
        }
    }

    /// <summary>A random mode, a switch point spread over powers of two below 8,192, and a random measurement, for the differential tests.</summary>
    private void StartChaos(ref Visit v)
    {
        v.Mode = (Next() % 3) switch
        {
            0 => IlGate.ModeCompiled,
            1 => IlGate.ModeRenting,
            _ => IlGate.ModeSettled,
        };
        v.SwitchAt = (int)(Next() & ((1u << (int)(Next() % 14)) - 1));
        v.Measured = (Next() & 1) == 0;
        Counters.VisitsChaos++;
    }

    /// <summary>Starts a measured visit of uncompiled code in a sampled transaction, after learning from the state's last visit.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartSample(ref Visit v, VirtualMachine<EthereumGasPolicy> vm, IlEvm.IlCodeState state, OpcodeBodySet bodies, nint pc, bool block)
    {
        Fold(ref v);
        v.Tx = _tx;
        v.Ops = 0;
        v.Parts = 0;
        v.SwitchAt = 0;
        v.Mode = IlGate.ModeSampled;
        v.Counting = true;
        v.Block = block;
        v.Main = !block || ProcessingThread.IsBlockProcessingThread;
        v.Measured = true;
        v.Learns = true;
        v.WeightLog2 = _sampleLog2;
        v.EntryKey = pc == 0 ? IlGate.EntryKey(vm.VmState.Env.InputData.Span) : IlGate.ResumeKey(pc);
        // A live compile is for the code's latest body set, which a fork changes without resolving any state again.
        if (state.Profile is { } profile && !ReferenceEquals(profile.LastBodies, bodies))
            profile.LastBodies = bodies;
        Counters.VisitsSampled++;
    }

    /// <summary>Learns a measured visit's length for its runner when the visit learns, and adds a counted one to the code's live profile.</summary>
    private void Fold(ref Visit v)
    {
        // A visit of no opcodes says nothing, as one whose only part threw.
        if (v.Tx == 0 || !v.Measured || v.Ops <= 0 || v.Key?.Profile is not { } profile)
            return;
        if (v.Learns)
            IlVisitStats.Learn(ref profile.VisitsFor(v.Block).Stats, v.EntryKey, v.Ops);
        if (v.Counting)
            CountLive(profile, v.Ops, v.Parts, v.Main, v.WeightLog2, v.Block);
    }

    /// <summary>Adds a counted visit to the code's live profile, standing for 2^<paramref name="weightLog2"/> visits; live profiling implements it (<see cref="IlLive.Count"/>).</summary>
    partial void CountLive(IlCodeProfile profile, int ops, int parts, bool main, int weightLog2, bool block);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ArmReport(CodeInfo code, int visit)
    {
        _reportCode = code;
        _reportVisit = visit;
        _reportTx = _tx;
        // A sample is armed right after its report and finished only by it: one a dropped report left is cleared here, off the unreported parts' path.
        _timed = null;
    }

    /// <summary>The thread's xorshift draw: measured visits, chaos, and the switchback checks' samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Next()
    {
        uint r = _random;
        r ^= r << 13;
        r ^= r >> 17;
        r ^= r << 5;
        return _random = r;
    }

    /// <summary>The way <paramref name="i"/>, which the sets keep within the table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Visit At(int i) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_v), i);

    internal readonly record struct VisitView(bool Current, byte Mode, int Ops, int Parts, int SwitchAt, bool Measured, bool Counting, bool Block, bool Main, uint EntryKey, long LastCompiled);

    /// <summary>The latest visit of <paramref name="state"/> this thread keeps, current or not, or null; for tests.</summary>
    internal VisitView? VisitOf(IlEvm.IlCodeState state)
    {
        foreach (Visit v in _v)
        {
            if (ReferenceEquals(v.Key, state))
                return new VisitView(v.Tx == _tx, v.Mode, v.Ops, v.Parts, v.SwitchAt, v.Measured, v.Counting, v.Block, v.Main, v.EntryKey, v.LastCompiled);
        }

        return null;
    }

    internal VisitView? VisitOf(IlCodeProfile profile)
    {
        foreach (Visit v in _v)
        {
            if (v.Key is { } state && ReferenceEquals(state.Profile, profile))
                return new VisitView(v.Tx == _tx, v.Mode, v.Ops, v.Parts, v.SwitchAt, v.Measured, v.Counting, v.Block, v.Main, v.EntryKey, v.LastCompiled);
        }

        return null;
    }

    /// <summary>The transactions this thread saw, and its work clock now; for tests.</summary>
    internal int Transaction => _tx;

    internal long Clock => _base + _ops;

    /// <summary>Learns from every visit kept, as if each way were taken now, and ends them; for tests.</summary>
    internal void FoldAll()
    {
        for (int i = 0; i < _v.Length; i++)
        {
            Fold(ref _v[i]);
            _v[i].Tx = 0;
        }
    }

    /// <summary>Starts the thread's draws from <paramref name="seed"/>, so a chaos run repeats; for tests.</summary>
    internal void Reseed(uint seed) => _random = seed * 2654435761u | 1;

    internal void ResetForTests()
    {
        Array.Clear(_v);
        Array.Clear(_pooled);
        Counters = default;
        _vm = null;
        _ops = 0;
        _tx = 1;
        _cur = 0;
        _base = _txTicks = _txClock = 0;
        _reportCode = null;
        _timed = null;
        _sampleTx = false;
        _sampleLog2 = 0;
    }

    /// <summary>Sets whether the transaction under way is sampled, standing for 2^<paramref name="log2"/>; for tests that drive the gate without a top-level entry.</summary>
    internal void SampleTransactionForTests(bool sampled, int log2 = 0)
    {
        _sampleTx = sampled;
        _sampleLog2 = (byte)log2;
    }
}
