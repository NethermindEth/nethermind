// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The visit gate: unknown visits rent, long ones run compiled from their first part, short ones settle on the
/// interpreter, renting ones switch once; and what decides it - the learned visit lengths per runner and per list entry,
/// the work clock and its transactions, a hand-back's report, the block threads' mask, the sampling of uncompiled code
/// for live profiling, the calibration, and chaos.
/// </summary>
[NonParallelizable]
public class IlEvmGateTests : IlEvmAdaptiveFixture
{
    /// <summary>The test thread's gate, which runs every part here.</summary>
    private static IlThread Gate => IlThread.Current;

    [SetUp]
    public void FreshGate()
    {
        // The priors, whatever the environment overrides, and the same draws every run.
        IlEvmSettings.Gate = true;
        IlGate.Chaos = false;
        IlGate.Block = IlGate.BlockPrior;
        IlGate.Query = IlGate.QueryPrior;
        IlThread.Current.ResetForTests();
        IlThread.Current.Reseed(12345);
    }

    /// <summary>
    /// Counts calldata's first word down to zero and stops: 10 opcodes a turn, 10 more around them, in one part.
    /// </summary>
    /// <remarks>
    /// 00 PUSH1 0, CALLDATALOAD; 03 JUMPDEST, DUP1, ISZERO, PUSH1 10, JUMPI, PUSH1 1, SWAP1, SUB, PUSH1 3, JUMP;
    /// 10 JUMPDEST, POP, STOP.
    /// </remarks>
    private static byte[] CountLoopCode() => Hex("6000355b8015601057600190036003565b5000");

    /// <summary>
    /// Calls <see cref="IlEvmAdaptiveFixture.Callee"/> as many times as calldata's first word says, each call ending a
    /// part: its own 18 opcodes a call and 10 more, in one part more than calls; the callee runs 6 more a call.
    /// </summary>
    /// <remarks>
    /// 00 PUSH1 0, CALLDATALOAD; 03 JUMPDEST, DUP1, ISZERO, PUSH1 30, JUMPI, PUSH1 0 (x4), PUSH20 callee, GAS,
    /// STATICCALL; 28 POP, PUSH1 1, SWAP1, SUB, PUSH1 3, JUMP; 30 JUMPDEST, POP, STOP. A first part of 14 opcodes,
    /// then parts of 18, the last of 14.
    /// </remarks>
    private static byte[] CallerCode() =>
        [.. Hex("6000355b80156030576000600060006000" + "73"), .. Callee.Bytes, .. Hex("5afa50600190036003565b5000")];

    private static byte[] Hex(string hex) => Nethermind.Core.Extensions.Bytes.FromHexString(hex);

    /// <summary>A 32-byte calldata word of <paramref name="value"/>: the loops' count, with the selector and length every count shares.</summary>
    private static byte[] Word(int value)
    {
        byte[] word = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(word.AsSpan(28), value);
        return word;
    }

    [Test]
    public void Unknown_visit_rents()
    {
        byte[] code = Unique(CallerCode(), 0x21);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(20);
        Outcome expected = Interpret(code, input);
        for (int i = 0; i < 3; i++)
        {
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
            Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(expected));
        }

        // Each runner's first three visits know nothing yet: every part runs on the interpreter, reported, as master does.
        Assert.That(runs.Runs, Is.Zero);
        Assert.That((Gate.Counters.PartsCompiled, Gate.Counters.VisitsRenting, Gate.Counters.PartsInterpreted), Is.EqualTo((0L, 6L, 6L * 21)));
        IlThread.VisitView visit = Gate.VisitOf(StateOf(codeInfo))!.Value;
        Assert.That((visit.Mode, visit.Parts, visit.Ops, visit.Measured), Is.EqualTo((IlGate.ModeRenting, 21, 18 * 20 + 10, true)), "every part's opcodes reported");
    }

    [Test]
    public void Long_visit_compiled_from_first_part()
    {
        byte[] code = Unique(CountLoopCode(), 0x22);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(2_000);
        Outcome expected = Interpret(code, input);
        Assert.That(expected.OpCodes, Is.EqualTo(20_010));
        for (int visit = 0; visit < IlGate.Query.MinVisits; visit++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That((runs.Runs, Gate.Counters.VisitsLong), Is.EqualTo((0, 0L)), "a one-part visit rents to its end until enough are learned");

        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(Gate.Counters.VisitsLong, Is.EqualTo(1));
        Assert.That(runs.Runs, Is.EqualTo(1), "the visit's first part ran compiled");
        Assert.That(Gate.VisitOf(StateOf(codeInfo))!.Value.Mode, Is.EqualTo(IlGate.ModeCompiled));
    }

    [Test]
    public void Short_visit_settles()
    {
        byte[] code = Unique(ArithmeticCode(), 0x23);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, ArithmeticInput);
        for (int visit = 0; visit < IlGate.Query.MinVisits; visit++)
            Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));

        for (int visit = 0; visit < 256; visit++)
            Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));
        Assert.That((runs.Runs, Gate.Counters.PartsCompiled), Is.EqualTo((0, 0L)));
        Assert.That(Gate.Counters.VisitsSettled, Is.EqualTo(256));
        // One settled visit in 8 is measured: 32 expected, the bounds about four standard deviations out.
        Assert.That(Gate.Counters.VisitsMeasured, Is.InRange(12, 52));
    }

    [Test]
    public void Renting_switches_once()
    {
        // No warmth: each visit rents afresh, though the last one ran compiled.
        IlGate.Query = IlGate.Query.With(switchOps: 64, visitColdOps: 1 << 30, warmOps: 0);
        byte[] code = Unique(CallerCode(), 0x24);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(20);
        Outcome expected = Interpret(code, input);
        for (int visit = 1; visit <= 3; visit++)
        {
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
            Assert.That(Gate.Counters.VisitsSwitched, Is.EqualTo(visit), "once per visit");
            // Parts of 14, 18, 18 and 18 opcodes run interpreted, to 68; the fifth and the 16 after it, compiled.
            Assert.That(runs.Runs, Is.EqualTo(17 * visit));
        }

        IlThread.VisitView view = Gate.VisitOf(StateOf(codeInfo))!.Value;
        Assert.That((view.Mode, view.Parts, view.Ops), Is.EqualTo((IlGate.ModeCompiled, 21, 370)), "compiled and reported opcodes together: the whole visit");
        Assert.That(Gate.Counters.PartsInterpreted, Is.EqualTo(3 * 4));
    }

    [Test]
    public void Outlier_switch_point()
    {
        IlGate.Query = IlGate.Query.With(switchOps: 512, visitColdOps: 1 << 30);
        byte[] code = Unique(CallerCode(), 0x25);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        // 83 and 277 calls: visits of 1,504 and 4,996 opcodes, both under one key.
        byte[] shorter = Word(83), longer = Word(277);
        uint key = IlGate.EntryKey(shorter);
        Assert.That(IlGate.EntryKey(longer), Is.EqualTo(key));
        // Three visits of 1,000 learned: the switch point is max(512, 2 x 1,000).
        IlVisitBox box = contract.Profile!.VisitsFor(block: false);
        for (int i = 0; i < 3; i++)
            IlVisitStats.Learn(ref box.Stats, key, 1_000);

        Assert.That(Run(QueryVm, codeInfo, shorter), Is.EqualTo(Interpret(code, shorter)));
        IlThread.VisitView visit = Gate.VisitOf(StateOf(codeInfo))!.Value;
        Assert.That((visit.Mode, visit.SwitchAt, visit.Ops), Is.EqualTo((IlGate.ModeRenting, 2_000, 1_504)), "short of twice its norm: never switched");
        Assert.That((runs.Runs, Gate.Counters.VisitsSwitched), Is.EqualTo((0, 0L)));

        Assert.That(Run(QueryVm, codeInfo, longer), Is.EqualTo(Interpret(code, longer)));
        Assert.That(Gate.Counters.VisitsSwitched, Is.EqualTo(1), "past it: switched");
        Assert.That(runs.Runs, Is.GreaterThan(0));
    }

    [Test]
    public void Warm_across_transactions()
    {
        // A gap of 16 opcodes per transaction and none for wall time: the clock moves only by what the gate sees.
        IlGate.Query = IlGate.Query.With(switchOps: 64, visitColdOps: 1 << 30, warmOps: 200, txGapOps: 16, opsPerMicrosecond: 0);
        byte[] code = Unique(CallerCode(), 0x26), other = Unique(ArithmeticCode(), 0x27);
        List<IlEvm.IlContract> shipped = Ship(code, other);
        RunCounter runs = CountRuns(shipped[0]);
        CodeInfo codeInfo = Stamped(code), otherInfo = Stamped(other);
        byte[] input = Word(20);
        Outcome expected = Interpret(code, input);

        // The first visit rents and switches, so its last parts run compiled.
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(Gate.Counters.VisitsSwitched, Is.EqualTo(1));

        // The next transaction starts 16 opcodes on: within 200, warm, compiled from its first part.
        int before = runs.Runs;
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(Gate.Counters.VisitsWarm, Is.EqualTo(1));
        Assert.That(runs.Runs - before, Is.EqualTo(21), "every part compiled");

        // Twenty transactions of other code put 336 opcodes of clock in between: cold again, so it rents.
        Outcome otherExpected = Interpret(other, ArithmeticInput);
        for (int i = 0; i < 20; i++)
            Assert.That(Run(QueryVm, otherInfo, ArithmeticInput), Is.EqualTo(otherExpected));
        long renting = Gate.Counters.VisitsRenting;
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(Gate.Counters.VisitsWarm, Is.EqualTo(1));
        Assert.That(Gate.Counters.VisitsRenting, Is.EqualTo(renting + 1));
    }

    [Test]
    public void Transaction_detection()
    {
        IlGate.Query = IlGate.Query.With(txGapOps: 100, opsPerMicrosecond: 0);
        IlGate.Block = IlGate.Block.With(txGapOps: 50, opsPerMicrosecond: 0);
        byte[] code = Unique(ArithmeticCode(), 0x28);
        Ship(code);
        CodeInfo codeInfo = Stamped(code);
        IlThread gate = Gate;

        // A top-level frame's entry starts a transaction, and the clock moves on by at least the gap.
        int tx = gate.Transaction;
        long clock = gate.Clock;
        Run(QueryVm, codeInfo, ArithmeticInput);
        Assert.That(gate.Transaction, Is.EqualTo(tx + 1));
        Assert.That(gate.Clock - clock, Is.GreaterThanOrEqualTo(100));

        // Parts that are no top-level entry: the same machine counting on is the same transaction.
        tx = gate.Transaction;
        QueryVm.OpCodeCount = 500;
        gate.DetectTx(QueryVm, pc: 7, block: false);
        QueryVm.OpCodeCount = 600;
        gate.DetectTx(QueryVm, pc: 9, block: false);
        Assert.That(gate.Transaction, Is.EqualTo(tx));

        // A count that went back starts one.
        clock = gate.Clock;
        QueryVm.OpCodeCount = 100;
        gate.DetectTx(QueryVm, pc: 7, block: false);
        Assert.That(gate.Transaction, Is.EqualTo(tx + 1));
        Assert.That(gate.Clock - clock, Is.GreaterThanOrEqualTo(100));

        // So does another machine, by its runner's gap.
        clock = gate.Clock;
        BlockVm.OpCodeCount = 100;
        gate.DetectTx(BlockVm, pc: 7, block: true);
        Assert.That(gate.Transaction, Is.EqualTo(tx + 2));
        Assert.That(gate.Clock - clock, Is.GreaterThanOrEqualTo(50));

        // Wall time between transactions counts at the interpreter's pace: 10 ms at 250 opcodes a microsecond.
        IlGate.Query = IlGate.Query.With(txGapOps: 0, opsPerMicrosecond: 250);
        Run(QueryVm, codeInfo, ArithmeticInput);
        clock = gate.Clock;
        Stopwatch slept = Stopwatch.StartNew();
        while (slept.ElapsedMilliseconds < 10)
            System.Threading.Thread.Sleep(1);
        Run(QueryVm, codeInfo, ArithmeticInput);
        Assert.That(gate.Clock - clock, Is.GreaterThanOrEqualTo(2_500_000));
    }

    [Test]
    public void Hand_back_counts_whole_part()
    {
        // Laid out one instruction a chunk with only the entry chunk compiled: the frame hands back where it leaves it.
        IlEvmSettings.ChunkInstructions = 1;
        IlEvmSettings.ChunkThreshold = int.MaxValue;
        IlEvmSettings.EagerChunks = false;
        IlEvmSettings.CompileSynchronously = false;
        IlEvmSettings.Gate = false;
        byte[] code = Unique(CountLoopCode(), 0x29);
        IlEvm.IlContract? contract = IlEvm.IlCompiler.Compile(new CodeInfo(code), IlEvm.IlGetFork(Bodies(Spec)), out string? reason);
        Assert.That(contract, Is.Not.Null, reason);
        Assert.That(contract!.ChunkCount, Is.GreaterThan(1));
        Assert.That(contract.CompileChunk(contract.EntryChunk[0]), Is.Not.Null);
        IlRuntime.PublishShipped([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), contract)]);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(50);
        Outcome expected = Interpret(code, input);

        IlEvm.IlContract.HitColdChunk = false;
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(IlEvm.IlContract.HitColdChunk, Is.True, "the part handed back at a chunk not compiled");
        IlThread.VisitView visit = Gate.VisitOf(StateOf(codeInfo))!.Value;
        Assert.That((visit.Ops, visit.Parts), Is.EqualTo((expected.OpCodes, 1)), "compiled code's opcodes and the interpreter's rest: the whole part");
    }

    [Test]
    public void Visit_stats()
    {
        IlVisitBox box = new();
        uint key = IlGate.EntryKey(ArithmeticInput);
        for (int i = 0; i < 7; i++)
            IlVisitStats.Learn(ref box.Stats, key, 1_000);
        IlVisitStats.Learn(ref box.Stats, key, 1_000_000);
        (int count, int log) = IlVisitStats.Get(ref box.Stats, key);
        Assert.That(count, Is.EqualTo(8));
        Assert.That(IlGate.Exp2Q8(log), Is.InRange(1_000, 6_000), "a geometric mean: one outlier does not drag it");

        // Past 8 visits the mean moves an eighth of the way, so it follows a shift a mean over all visits would not: after
        // 200 visits of 1,000 opcodes, 16 of 100,000 take it most of the way, to about 57,000.
        IlVisitBox shifting = new();
        for (int i = 0; i < 200; i++)
            IlVisitStats.Learn(ref shifting.Stats, key, 1_000);
        for (int i = 0; i < 16; i++)
            IlVisitStats.Learn(ref shifting.Stats, key, 100_000);
        (int shiftedCount, int shifted) = IlVisitStats.Get(ref shifting.Stats, key);
        Assert.That(shiftedCount, Is.EqualTo(216));
        Assert.That(IlGate.Exp2Q8(shifted), Is.InRange(30_000, 100_000));

        // Twenty keys in sixteen words: no error, and the latest is always there.
        IlVisitBox crowded = new();
        for (int k = 0; k < 20; k++)
        {
            uint each = 0x1000_0001u + 2u * (uint)k;
            IlVisitStats.Learn(ref crowded.Stats, each, 100 + k);
            Assert.That(IlVisitStats.Get(ref crowded.Stats, each), Is.EqualTo((1, IlGate.Log2Q8(100 + k))));
        }

        Assert.That(IlVisitStats.Get(ref crowded.Stats, 0x7777_7777u), Is.EqualTo((0, 0)), "a key never learned");
        Assert.That(IlGate.Exp2Q8(IlGate.Log2Q8(1_536)), Is.EqualTo(1_536));
        Assert.That(IlGate.Exp2Q8(IlGate.Log2Q8(6_144)), Is.EqualTo(6_144));
    }

    [Test]
    public void Per_runner_learning()
    {
        byte[] code = Unique(ArithmeticCode(), 0x2a);
        IlEvm.IlContract contract = Ship(code)[0];
        IlCodeProfile profile = contract.Profile!;
        CodeInfo codeInfo = Stamped(code);
        uint key = IlGate.EntryKey(ArithmeticInput);
        for (int i = 0; i < 2; i++)
            OnBlockThread(() => Run(BlockVm, codeInfo, ArithmeticInput));
        Gate.FoldAll();
        Assert.That(profile.QueryVisits, Is.Null, "block visits teach block processing's runner alone");
        IlVisitBox block = profile.BlockVisits!;
        Assert.That(IlVisitStats.Get(ref block.Stats, key).Count, Is.EqualTo(2));
        ulong[] blockWords = Words(block);

        for (int i = 0; i < 3; i++)
            Run(QueryVm, codeInfo, ArithmeticInput);
        Gate.FoldAll();
        IlVisitBox query = profile.QueryVisits!;
        Assert.That(IlVisitStats.Get(ref query.Stats, key).Count, Is.EqualTo(3));
        Assert.That(Words(block), Is.EqualTo(blockWords), "and the reverse");

        ulong[] queryWords = Words(query);
        OnBlockThread(() => Run(BlockVm, codeInfo, ArithmeticInput));
        Gate.FoldAll();
        Assert.That(Words(query), Is.EqualTo(queryWords));
        Assert.That(IlVisitStats.Get(ref block.Stats, key).Count, Is.EqualTo(3));
    }

    private static ulong[] Words(IlVisitBox box)
    {
        ulong[] words = new ulong[IlGate.VisitWords];
        for (int i = 0; i < words.Length; i++)
            words[i] = box.Stats[i];
        return words;
    }

    [Test]
    public void Block_thread_mask()
    {
        // BlockThreads=Main: only the thread the core marks for a processing pass offers the gate a part.
        IlEvmSettings.Gate = false;
        IlRuntime.BlockThreadsMask = IlRuntime.RunMain;
        byte[] code = Unique(ArithmeticCode(), 0x2b);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, ArithmeticInput);
        Assert.That(Run(BlockVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.Zero);
        Assert.That(Gate.VisitOf(StateOf(codeInfo)), Is.Null, "a worker's part is declined before the gate");
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, ArithmeticInput)), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        IlThread.VisitView visit = Gate.VisitOf(StateOf(codeInfo))!.Value;
        Assert.That((visit.Block, visit.Main), Is.EqualTo((true, true)), "the block thread's kind, from the core's flag");
        Assert.That(ProcessingThread.IsBlockProcessingThread, Is.False);

        // Both kinds: a worker's part runs compiled too, and its visit is the workers' kind; the queries' visits are the main kind.
        IlRuntime.BlockThreadsMask = IlRuntime.RunMain | IlRuntime.RunWorkers;
        byte[] both = Unique(ArithmeticCode(), 0x2c);
        RunCounter bothRuns = CountRuns(Ship(both)[0]);
        CodeInfo bothInfo = Stamped(both);
        Outcome bothExpected = Interpret(both, ArithmeticInput);
        Assert.That(Run(BlockVm, bothInfo, ArithmeticInput), Is.EqualTo(bothExpected));
        Assert.That(bothRuns.Runs, Is.EqualTo(1));
        Assert.That(Gate.VisitOf(StateOf(bothInfo))!.Value.Main, Is.False);
        Assert.That(Run(QueryVm, bothInfo, ArithmeticInput), Is.EqualTo(bothExpected));
        Assert.That(Gate.VisitOf(StateOf(bothInfo))!.Value.Main, Is.True);
    }

    [Test]
    public void Minv_overrides_block_threshold()
    {
        byte[] code = Unique(CountLoopCode(), 0x2d), plain = Unique(CountLoopCode(), 0x2e);
        IlEvm.IlContract contract = Ship(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code), MinVisitLog2: 9), code);
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(59);
        Outcome expected = Interpret(code, input);
        Assert.That(expected.OpCodes, Is.EqualTo(600));

        // The read-only queries keep their own threshold, 8,192 opcodes: visits of 600 settle.
        for (int i = 0; i < 4; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(Gate.VisitOf(StateOf(codeInfo))!.Value.Mode, Is.EqualTo(IlGate.ModeSettled));
        Assert.That(runs.Runs, Is.Zero);

        // Block processing: minv=9 admits visits from 512 opcodes once three are learned.
        for (int i = 0; i < 4; i++)
            Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(expected));
        Assert.That((Gate.Counters.VisitsLong, runs.Runs), Is.EqualTo((1L, 1)));

        // Without it, the block runner's prior of 2,048 does not.
        RunCounter plainRuns = CountRuns(Ship(plain)[0]);
        CodeInfo plainInfo = Stamped(plain);
        for (int i = 0; i < 5; i++)
            Assert.That(OnBlockThread(() => Run(BlockVm, plainInfo, input)), Is.EqualTo(Interpret(plain, input)));
        Assert.That((Gate.Counters.VisitsLong, plainRuns.Runs), Is.EqualTo((1L, 0)));
    }

    [Test]
    public void Gate_off_runs_every_part_compiled_and_keeps_the_bookkeeping()
    {
        IlEvmSettings.Gate = false;
        byte[] code = Unique(CallerCode(), 0x2f);
        RunCounter runs = CountRuns(Ship(code)[0]);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(20);
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(Interpret(code, input)));
        Assert.That(runs.Runs, Is.EqualTo(21));
        Assert.That((Gate.Counters.VisitsUngated, Gate.Counters.PartsCompiled), Is.EqualTo((1L, 21L)));
        IlThread.VisitView visit = Gate.VisitOf(StateOf(codeInfo))!.Value;
        Assert.That((visit.Mode, visit.Ops), Is.EqualTo((IlGate.ModeCompiled, 370)), "learned and counted as a gated visit is");
    }

    [Test]
    public void A_counting_runner_samples_uncompiled_code_and_learns_its_visits()
    {
        // Nothing shipped: with counting on, one transaction in 8 is sampled, and each visit of it measured and reported,
        // part by part.
        IlRuntime.QueryCounting = true;
        IlGate.QuerySampleEvery = 8;
        byte[] code = Unique(CallerCode(), 0x31);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(4);
        Outcome expected = Interpret(code, input);
        for (int i = 0; i < 128; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        Assert.That(state.QueryRun, Is.Null, "counted, not compiled");
        Gate.FoldAll();
        (int learned, int log) = IlVisitStats.Get(ref state.Profile!.QueryVisits!.Stats, IlGate.EntryKey(input));
        Assert.That(learned, Is.InRange(4, 32), "one transaction in 8 sampled: 16 expected");
        // The callee's code is sampled too, as is anything a sampled transaction meets; the others' visits are none.
        Assert.That(Gate.Counters.VisitsSampled, Is.EqualTo(2 * learned));
        Assert.That(IlGate.Exp2Q8(log), Is.EqualTo(18 * 4 + 10), "every part of a measured visit reported");
        Assert.That(Gate.Counters.PartsCompiled + Gate.Counters.PartsInterpreted, Is.Zero, "sampled parts are none of the gate's decisions");

        // Not counting, uncompiled code never reaches the gate.
        IlRuntime.QueryCounting = false;
        byte[] other = Unique(CallerCode(), 0x32);
        CodeInfo otherInfo = Stamped(other);
        long sampled = Gate.Counters.VisitsSampled;
        Assert.That(Run(QueryVm, otherInfo, input), Is.EqualTo(Interpret(other, input)));
        Assert.That(Gate.Counters.VisitsSampled, Is.EqualTo(sampled));
        Assert.That(IlEvmFrameRunner.CodeSlot(otherInfo), Is.SameAs(IlRuntime.QuietAll));
    }

    [Test]
    public void A_report_the_next_part_finds_armed_is_dropped()
    {
        // A renting visit's part asks for its report; the interpreter's run of it throwing, the report never comes.
        byte[] code = Unique(CallerCode(), 0x33);
        Ship(code);
        CodeInfo codeInfo = Stamped(code), otherCode = Stamped(Unique(ArithmeticCode(), 0x34));
        Run(QueryVm, codeInfo, Word(1));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        IlThread gate = Gate;
        QueryVm.OpCodeCount = 10;
        Assert.That(gate.Offer(QueryVm, state, codeInfo, pc: 40, block: false, counting: false), Is.EqualTo(IlGate.InterpretReport));
        int ops = gate.VisitOf(state)!.Value.Ops;
        // Another code's report goes nowhere and disarms it; so does the next part's offer.
        gate.FinishReport(otherCode, 1_000);
        gate.FinishReport(codeInfo, 1_000);
        Assert.That(gate.VisitOf(state)!.Value.Ops, Is.EqualTo(ops));
        QueryVm.OpCodeCount = 20;
        Assert.That(gate.Offer(QueryVm, state, codeInfo, pc: 40, block: false, counting: false), Is.EqualTo(IlGate.InterpretReport));
        gate.DetectTx(QueryVm, pc: 40, block: false);
        gate.FinishReport(codeInfo, 1_000);
        Assert.That(gate.VisitOf(state)!.Value.Ops, Is.EqualTo(ops));
        // The one that does come counts.
        Assert.That(gate.Offer(QueryVm, state, codeInfo, pc: 40, block: false, counting: false), Is.EqualTo(IlGate.InterpretReport));
        gate.FinishReport(codeInfo, 1_000);
        Assert.That(gate.VisitOf(state)!.Value.Ops, Is.EqualTo(ops + 1_000));
    }

    [Test]
    public void Calibration_sets_the_block_thresholds_under_the_environment()
    {
        IlGate.Calibrate(["fit r2=0.61 gate_block_visit_cold_ops=4096 gate_block_switch_ops=3072.4", "a=0.25/0.6/1.1"]);
        Assert.That((IlGate.Block.VisitColdOps, IlGate.Block.SwitchOps, IlGate.Block.WarmOps), Is.EqualTo((4096, 3072, IlGate.BlockPrior.WarmOps)));
        Assert.That(IlGate.Query.VisitColdOps, Is.EqualTo(IlGate.QueryPrior.VisitColdOps), "the queries' runner keeps its own");

        string variable = "NETHERMIND_ILEVM_GATE_BLOCK_SWITCH_OPS";
        string? saved = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "999");
        try
        {
            IlGate.Calibrate(["gate_block_switch_ops=3072"]);
            Assert.That(IlGate.Block.SwitchOps, Is.EqualTo(999), "an environment override wins");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }

        IlGate.Calibrate([]);
        Assert.That(IlGate.Block.Values(), Is.EqualTo(IlGate.BlockPrior.Values()));
    }

    [Test]
    public void Chaos_visits_match_the_interpreter()
    {
        IlGate.Chaos = true;
        byte[] code = Unique(CallerCode(), 0x35), loop = Unique(CountLoopCode(), 0x36);
        Ship(code, loop);
        CodeInfo codeInfo = Stamped(code), loopInfo = Stamped(loop);
        Random random = new(7);
        for (int i = 0; i < 200; i++)
        {
            byte[] input = Word(random.Next(0, 40));
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(Interpret(code, input)));
            Assert.That(OnBlockThread(() => Run(BlockVm, loopInfo, input)), Is.EqualTo(Interpret(loop, input)));
        }

        Assert.That(Gate.Counters.VisitsChaos, Is.EqualTo(400));
        Assert.That(Gate.Counters.PartsCompiled, Is.GreaterThan(0));
        Assert.That(Gate.Counters.PartsInterpreted, Is.GreaterThan(0));
    }

    [Test]
    public void A_transaction_whose_top_level_frame_is_declined_from_its_mark_is_a_transaction_of_its_own()
    {
        // Only the callee is shipped: the caller's frames, the transactions' top-level ones, are declined from their mark,
        // which the gate never sees; each transaction's callee visit is a visit of its own all the same.
        IlEvm.IlContract callee = Ship(CalleeCode())[0];
        CodeInfo caller = Stamped(Unique(CallerCode(), 0x37));
        Run(QueryVm, caller, Word(1));
        Assert.That(IlEvmFrameRunner.CodeSlot(caller), Is.InstanceOf<IlQuiet>(), "the caller is declined from its mark");
        int tx = Gate.Transaction;
        Run(QueryVm, caller, Word(3));
        Assert.That(Gate.Transaction, Is.EqualTo(tx + 1), "the second transaction is one");
        IlThread.VisitView visit = Gate.VisitOf(callee.Profile!)!.Value;
        Assert.That((visit.Current, visit.Parts, visit.Ops), Is.EqualTo((true, 3, 3 * 6)), "its visit has its own three parts, and their opcodes");
    }

    [Test]
    public void A_transaction_whose_callee_starts_later_than_the_last_part_ended_is_one_of_its_own()
    {
        // A caller nothing ships counts its calldata word down before it calls the shipped callee once: the second
        // transaction's callee part starts at an opcode count past where the first transaction's ended, so only the
        // declined top-level entry tells the gate a transaction began.
        IlEvm.IlContract callee = Ship(CalleeCode())[0];
        CodeInfo caller = Stamped(Unique([.. Hex("6000355b8015601057600190036003565b50" + "6000600060006000" + "73"), .. Callee.Bytes, .. Hex("5afa5000")], 0x39));
        Run(QueryVm, caller, Word(0));
        Assert.That(IlEvmFrameRunner.CodeSlot(caller), Is.InstanceOf<IlQuiet>(), "the caller is declined from its mark");
        int tx = Gate.Transaction;
        Run(QueryVm, caller, Word(200));
        Assert.That(Gate.Transaction, Is.EqualTo(tx + 1), "the second transaction is one");
        IlThread.VisitView visit = Gate.VisitOf(callee.Profile!)!.Value;
        Assert.That((visit.Current, visit.Parts, visit.Ops), Is.EqualTo((true, 1, 6)), "its visit is its own one part");
    }

    [Test]
    public void Short_block_visits_of_consecutive_transactions_never_add_up_to_compiled()
    {
        // The block prior switches a renting visit at 1,536 opcodes: a transaction's visit of the shipped loop, 1,010
        // opcodes, never reaches it, however many transactions call it through a router nothing ships; and what is learned
        // of it is its own length, so it never runs compiled from its first part either.
        IlEvm.IlContract loop = Ship(LoopCalleeCode())[0];
        RunCounter runs = CountRuns(loop);
        byte[] router = RouterCode(0x38);
        CodeInfo routerInfo = Stamped(router);
        byte[] input = Word(100);
        Outcome expected = Interpret(router, input, vm: BlockVm);
        for (int tx = 0; tx < 20; tx++)
        {
            Assert.That(OnBlockThread(() => Run(BlockVm, routerInfo, input)), Is.EqualTo(expected));
            // Measured while unknown, then one in 8, near the switch point; settled unmeasured otherwise.
            IlThread.VisitView visit = Gate.VisitOf(loop.Profile!)!.Value;
            Assert.That((visit.Parts, visit.Ops), Is.EqualTo((1, visit.Measured ? 1_010 : 0)), $"transaction {tx}: a visit of its own");
        }

        Assert.That((runs.Runs, Gate.Counters.VisitsSwitched, Gate.Counters.VisitsLong), Is.EqualTo((0, 0L, 0L)));
        Gate.FoldAll();
        (int learned, int log) = IlVisitStats.Get(ref loop.Profile!.BlockVisits!.Stats, IlGate.EntryKey(input));
        Assert.That((learned, IlGate.Exp2Q8(log)), Is.EqualTo((Math.Max(3, learned), 1_010)), "each learned at its own length");
    }

    [Test]
    public void A_part_both_unmeasured_and_sampled_starts_one_transaction()
    {
        // A contract its check took the queries' runner off, on a runner that counts: a transaction's top-level entry of it
        // is looked at for a visit under way and sampled, one transaction either way.
        IlRuntime.QueryCounting = true;
        byte[] code = Unique(ArithmeticCode(), 0x39);
        IlEvm.IlContract contract = Ship(code)[0];
        contract.RunMask = (byte)((contract.RunMask & ~IlRuntime.RunQuery) | IlRuntime.CheckOffQuery);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, ArithmeticInput);
        for (int i = 0; i < 3; i++)
        {
            int tx = Gate.Transaction;
            Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));
            Assert.That(Gate.Transaction, Is.EqualTo(tx + 1), $"transaction {i}");
        }

        Assert.That(Gate.Counters.VisitsSampled, Is.EqualTo(3), "sampled, every transaction drawn");
    }

    [Test]
    public void Block_visits_near_the_switch_point_rent_one_in_eight()
    {
        // Learned at 1,000 opcodes: within four times of the block prior's switch point of 1,536, short of its cold
        // length of 2,048. One visit in 8 rents, standing for the others, which settle unreported; a typical one never
        // reaches twice its mean, so none runs compiled.
        byte[] code = Unique(CountLoopCode(), 0x3a);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(99);
        Outcome expected = Interpret(code, input);
        Assert.That(expected.OpCodes, Is.EqualTo(1_000));
        IlVisitBox box = contract.Profile!.VisitsFor(block: true);
        for (int i = 0; i < 3; i++)
            IlVisitStats.Learn(ref box.Stats, IlGate.EntryKey(input), 1_000);
        for (int i = 0; i < 256; i++)
            Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.Zero);
        Assert.That(Gate.Counters.VisitsRenting, Is.InRange(12, 52), "one in 8: 32 expected");
        Assert.That((Gate.Counters.VisitsRenting + Gate.Counters.VisitsSettled, Gate.Counters.VisitsMeasured), Is.EqualTo((256L, 0L)), "the others settled, unmeasured");

        // The queries' runner, whose visits of a key learned near its own switch point rent, rents every one: it meets
        // its code colder, and fewer of its keys are near.
        IlVisitBox query = contract.Profile.VisitsFor(block: false);
        byte[] longer = Word(199);
        for (int i = 0; i < 3; i++)
            IlVisitStats.Learn(ref query.Stats, IlGate.EntryKey(longer), 2_000);
        long renting = Gate.Counters.VisitsRenting;
        for (int i = 0; i < 64; i++)
            Assert.That(Run(QueryVm, codeInfo, longer), Is.EqualTo(Interpret(code, longer)));
        Assert.That(Gate.Counters.VisitsRenting - renting, Is.EqualTo(64));
    }

    [Test]
    public void A_counting_runner_samples_transactions_and_the_others_decline_from_the_marks()
    {
        // Block processing counting for live profiling, one transaction in 16 sampled: 256 transactions, each of code of
        // its own that nothing ships. Those sampled give their code a state and a profile, every visit counted; the others
        // leave theirs marked, never offering the gate a part.
        IlRuntime.BlocksCounting = true;
        IlGate.BlockSampleEvery = 16;
        CodeInfo[] codes = [.. Enumerable.Range(0, 256).Select(i => Stamped([.. Unique(ArithmeticCode(), 0x3b), (byte)i, (byte)(i >> 8)]))];
        foreach (CodeInfo codeInfo in codes)
            OnBlockThread(() => Run(BlockVm, codeInfo, ArithmeticInput));
        int states = codes.Count(static c => IlEvmFrameRunner.CodeSlot(c) is IlEvm.IlCodeState);
        Assert.That(states, Is.InRange(4, 32), "one in 16: 16 expected");
        Assert.That(codes.Count(static c => IlEvmFrameRunner.CodeSlot(c) is IlQuiet), Is.EqualTo(256 - states));
        Assert.That(Gate.Counters.VisitsSampled, Is.EqualTo(states), "a visit for each sampled transaction");
        Gate.FoldAll();
        // Each counted visit stands for the 16 transactions its draw stood for.
        IlCodeProfile profile = StateOf(codes.First(static c => IlEvmFrameRunner.CodeSlot(c) is IlEvm.IlCodeState)).Profile!;
        Assert.That(profile.Counters!.Where(static (_, i) => i < IlLive.PartsIndex && i % 2 == 0).Sum(), Is.EqualTo(16));
        Assert.That(profile.Counters![IlLive.MeasuredIndex], Is.EqualTo(1));
    }

    [Test]
    public void A_key_well_learned_moves_on_one_visit_in_eight()
    {
        // Compiled visits of a key with 16 learned: one in 8 is learned from, so threads running the contract seldom
        // write its line; the mean still follows them.
        IlGate.Query = IlGate.Query.With(visitColdOps: 1_024);
        byte[] code = Unique(CountLoopCode(), 0x3c);
        IlEvm.IlContract contract = Ship(code)[0];
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(200);
        uint key = IlGate.EntryKey(input);
        IlVisitBox box = contract.Profile!.VisitsFor(block: false);
        for (int i = 0; i < IlGate.LearnAlwaysBelow; i++)
            IlVisitStats.Learn(ref box.Stats, key, 3_000);
        for (int i = 0; i < 400; i++)
            Run(QueryVm, codeInfo, input);
        Gate.FoldAll();
        (int learned, int log) = IlVisitStats.Get(ref box.Stats, key);
        Assert.That(Gate.Counters.VisitsLong, Is.EqualTo(400));
        Assert.That(learned - IlGate.LearnAlwaysBelow, Is.InRange(25, 80), "one in 8: 50 expected");
        Assert.That(IlGate.Exp2Q8(log), Is.InRange(2_010, 2_200), "moved most of the way to the visits' 2,010 opcodes");
    }

    [Test]
    public void A_thread_pools_its_timed_samples_until_they_fill_a_bucket()
    {
        IlEvmSettings.SelfCheck = true;
        IlEvmSettings.QueryCheck = true;
        List<IlEvm.IlContract> shipped = Ship(Unique(CountLoopCode(), 0x3d), Unique(CountLoopCode(), 0x3e));
        IlCheck check = shipped[0].QueryCheck!, other = shipped[1].QueryCheck!;
        // Ten compiled parts of 100 opcodes: pooled on the thread, the check's shared bucket untouched.
        for (int i = 0; i < 10; i++)
            Gate.AddCompiledSample(check, 1_000, 100);
        Assert.That((Gate.PooledOps(check, interpreted: false), check.ViewForTests().CompiledOps, check.ViewForTests().Recent), Is.EqualTo((1_000, 0L, 0)));
        // The eleventh fills a bucket: the check takes it whole, as one sample.
        Gate.AddCompiledSample(check, 1_000, 100);
        Assert.That((Gate.PooledOps(check, interpreted: false), check.ViewForTests().Recent), Is.EqualTo((0, 1)));
        // Another check's samples pool apart; an interpreted window's too, and a part of another window hands the pool of
        // the one before to its check first.
        Gate.AddCompiledSample(other, 1_000, 100);
        Gate.PoolInterpretedForTests(check, 1_000, 100, window: 7);
        Gate.PoolInterpretedForTests(check, 1_000, 100, window: 8);
        Assert.That((Gate.PooledOps(other, interpreted: false), Gate.PooledOps(check, interpreted: false), Gate.PooledOps(check, interpreted: true)), Is.EqualTo((100, 0, 100)));
        Assert.That(check.ViewForTests().InterpretedOps, Is.EqualTo(100), "window 7's pool went to the check");
        Assert.That(Gate.Counters.SamplesCompiled, Is.EqualTo(12));
    }

    /// <summary>
    /// The gate's warm cost per part, measured two ways. In a loop on the gate alone: a part it declines (a settled
    /// visit) and a part it admits (a compiled visit, with the bookkeeping after the run), over the loop's own cost. And
    /// end to end: a caller whose 200 calls make 201 parts of it a transaction, on the bare hook (nothing shipped, the
    /// slot's mark), declined by the gate, reported part by part, sampled for live profiling, and with no runner at all.
    /// </summary>
    [Test, Explicit("Timing")]
    public void Hot_path_cost()
    {
        // Performance cores only, at high priority, as the shape timings take them (ILEVM_AFFINITY, a hex mask): a hybrid
        // laptop part otherwise moves the thread between core types and clocks.
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
            long.TryParse(Environment.GetEnvironmentVariable("ILEVM_AFFINITY"), NumberStyles.HexNumber, null, out long affinity) && affinity > 0)
        {
            using Process self = Process.GetCurrentProcess();
            self.ProcessorAffinity = (nint)affinity;
            self.PriorityClass = ProcessPriorityClass.High;
        }

        TestContext.Out.WriteLine(DirectCost(partsPerVisit: 64));
        TestContext.Out.WriteLine(DirectCost(partsPerVisit: 1));
        TestContext.Out.WriteLine(EndToEndCost());
    }

    private string DirectCost(int partsPerVisit)
    {
        const int Parts = 8_000_000, Rounds = 9, Step = 20_000;
        // No warmth, so the declined visits settle whatever the admitted ones ran; nothing measured.
        IlGate.Query = IlGate.Query.With(visitColdOps: 1_024, switchOps: 1 << 20, measureEvery: 1 << 20, warmOps: 0, opsPerMicrosecond: 0);
        byte[] code = Unique(CallerCode(), 0x40);
        IlEvm.IlContract contract = Ship(code)[0];
        CodeInfo codeInfo = Stamped(code);
        Run(QueryVm, codeInfo, Word(1));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        // Resumes at 40 learned short, so their visits settle; resumes at 41 learned long, so they run compiled, and the
        // admitted parts' 10,000 opcodes each keep them long.
        IlVisitBox box = contract.Profile!.VisitsFor(block: false);
        for (int i = 0; i < 3; i++)
        {
            IlVisitStats.Learn(ref box.Stats, IlGate.ResumeKey(40), 20);
            IlVisitStats.Learn(ref box.Stats, IlGate.ResumeKey(41), 20_000);
        }

        // Each visit on the other machine, so each starts a transaction, as each of a call's contracts does.
        EthereumVirtualMachine one = QueryVm, other = PragueQueryVm;
        IlThread gate = Gate;
        long sink = 0;

        long Bare()
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0, ops = 0; i < Parts; i++)
            {
                EthereumVirtualMachine vm = (i / partsPerVisit & 1) == 0 ? one : other;
                ops = i % partsPerVisit == 0 ? 1 : ops + Step;
                vm.OpCodeCount = ops;
                sink += ops;
            }

            return Stopwatch.GetTimestamp() - start;
        }

        long Declined()
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0, ops = 0; i < Parts; i++)
            {
                EthereumVirtualMachine vm = (i / partsPerVisit & 1) == 0 ? one : other;
                ops = i % partsPerVisit == 0 ? 1 : ops + Step;
                vm.OpCodeCount = ops;
                sink += gate.Offer(vm, state, codeInfo, 40, block: false, counting: false);
            }

            return Stopwatch.GetTimestamp() - start;
        }

        long Admitted()
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0, ops = 0; i < Parts; i++)
            {
                EthereumVirtualMachine vm = (i / partsPerVisit & 1) == 0 ? one : other;
                ops = i % partsPerVisit == 0 ? 1 : ops + Step;
                vm.OpCodeCount = ops;
                sink += gate.Offer(vm, state, codeInfo, 41, block: false, counting: false);
                // The compiled run, as far as the gate sees it: the count moves on.
                vm.OpCodeCount = ops + Step / 2;
                gate.AfterCompiled(vm, ops, codeInfo, handedBack: false);
            }

            return Stopwatch.GetTimestamp() - start;
        }

        long bare = long.MaxValue, declined = long.MaxValue, admitted = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            bare = Math.Min(bare, Bare());
            declined = Math.Min(declined, Declined());
            admitted = Math.Min(admitted, Admitted());
        }

        IlGateCounters counters = gate.Counters;
        double Ns(long ticks) => ticks * 1e9 / Stopwatch.Frequency / Parts;
        return string.Create(CultureInfo.InvariantCulture,
            $"gate alone, {partsPerVisit} parts a visit: loop {Ns(bare):F2} ns, declined +{Ns(declined) - Ns(bare):F2} ns, admitted +{Ns(admitted) - Ns(bare):F2} ns a part " +
            $"(settled {counters.VisitsSettled}, measured {counters.VisitsMeasured}, long {counters.VisitsLong}; sink {sink % 10})");
    }

    private string EndToEndCost()
    {
        const int Calls = 200, Transactions = 100, Rounds = 31;
        byte[] input = Word(Calls);
        int callerOps = 18 * Calls + 10;
        // One code each: the bare hook's, never shipped; settled; renting, every part reported; sampled while counting.
        byte[] quiet = Unique(CallerCode(), 0x41), settled = Unique(CallerCode(), 0x42), renting = Unique(CallerCode(), 0x43), sampled = Unique(CallerCode(), 0x44);
        List<IlEvm.IlContract> shipped = Ship(settled, renting);
        CodeInfo quietInfo = Stamped(quiet), settledInfo = Stamped(settled), rentingInfo = Stamped(renting), sampledInfo = Stamped(sampled);
        foreach (IlEvm.IlContract contract in shipped)
        {
            IlVisitBox box = contract.Profile!.VisitsFor(block: false);
            for (int i = 0; i < 3; i++)
                IlVisitStats.Learn(ref box.Stats, IlGate.EntryKey(input), callerOps);
        }

        // Settled: a switch point no visit comes near, nothing measured. Renting: the prior, under which a visit of
        // 3,610 opcodes rents to twice that and is reported throughout.
        IlGateParams settle = IlGate.QueryPrior.With(switchOps: 1 << 20, measureEvery: 1 << 20), rent = IlGate.QueryPrior;
        Outcome expected = Interpret(quiet, input);

        double Time(Action arm)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Transactions; i++)
                arm();
            return (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / Transactions;
        }

        (string Name, Action Arm)[] arms =
        [
            ("no runner", () => Run(PlainVm, quietInfo, input)),
            ("bare hook", () => Run(QueryVm, quietInfo, input)),
            ("settled", () =>
            {
                IlGate.Query = settle;
                Run(QueryVm, settledInfo, input);
            }),
            ("renting", () =>
            {
                IlGate.Query = rent;
                Run(QueryVm, rentingInfo, input);
            }),
            ("sampled", () =>
            {
                IlRuntime.QueryCounting = true;
                Run(QueryVm, sampledInfo, input);
                IlRuntime.QueryCounting = false;
            }),
        ];

        // A warm-up of every arm first; the sampled one leaves the callee's slot a state, as it then is for every arm.
        for (int pass = 0; pass < 3; pass++)
        {
            foreach ((string _, Action arm) in arms)
                Time(arm);
        }

        // Each round times every arm in turn; an arm's cost is the median over rounds of its difference from the bare
        // hook's time in the same round, which drift between rounds does not move.
        double[][] times = [.. arms.Select(_ => new double[Rounds])];
        for (int round = 0; round < Rounds; round++)
        {
            for (int a = 0; a < arms.Length; a++)
                times[a][round] = Time(arms[a].Arm);
        }

        IlGate.Query = settle;
        Assert.That(Run(QueryVm, settledInfo, input), Is.EqualTo(expected), "the arms run the same work");
        static double Median(IEnumerable<double> values)
        {
            double[] sorted = [.. values.Order()];
            return sorted[sorted.Length / 2];
        }

        // Per caller part; the sampled arm samples the callee's parts too, so per part it is about half.
        string results = string.Join(", ", arms.Select((a, i) => string.Create(CultureInfo.InvariantCulture,
            $"{a.Name} {Median(times[i]) / 1000:F2} us ({Median(times[i].Select((t, r) => t - times[1][r])) / (Calls + 1):+0.00;-0.00} ns a caller part over the bare hook)")));
        return $"end to end, {Calls + 1} caller parts a transaction, medians of {Rounds} rounds: {results}";
    }
}
