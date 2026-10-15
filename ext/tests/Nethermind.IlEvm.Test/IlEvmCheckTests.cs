// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The switchback check: its windows - interpreted ones, compiled ones, the settling tick, the salt and the shares - its
/// samples and pairs, its verdicts and what each does to the run mask and the code, the rolling check of a kept contract,
/// the start-up window, block processing's samples only in its processing passes, and its clock.
/// </summary>
/// <remarks>
/// The visit gate is off, as the fixture leaves it, so every part a runner may run reaches the check; verdicts are
/// driven with synthetic samples, whose ratio is exact, and the windows with the clock set by hand.
/// </remarks>
[NonParallelizable]
public class IlEvmCheckTests : IlEvmAdaptiveFixture
{
    /// <summary>The opcodes and ticks of a synthetic sample: 10 ticks an opcode on the interpreter.</summary>
    private const int SampleOps = 2_048;
    private const long SampleTicks = 20_480;

    private static IlThread Thread => IlThread.Current;

    [SetUp]
    public void Checks()
    {
        IlEvmSettings.SelfCheck = true;
        IlEvmSettings.QueryCheck = true;
        IlEvmSettings.BlockCheck = true;
        IlEvmSettings.SelfCheckDelayMs = 0;
        // Out of the start-up window, unless a test puts the process back in it: it runs from the first frame.
        IlEvmSettings.SawFrame();
        IlEvmSettings.SelfCheckWarmUpMs = 0;
        IlEvmSettings.SelfCheckRecheckMs = 30_000;
        IlEvmSettings.SelfCheckEarlyRecheckMs = 5_000;
        IlCheck.KeepRatio = 0.97;
        IlCheck.RollingLossRatio = 1.02;
        IlCheck.TrialShare = 8;
        IlCheck.KeptShare = 32;
        IlCheck.BlockShare = 32;
        IlCheck.BlockRecheckMs = 600_000;
        IlCheck.MaskedRecheckMs = 600_000;
        IlCheckClock.ManualTicks = true;
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(1));
        IlGate.Chaos = false;
        Thread.ResetForTests();
        Thread.Reseed(4242);
    }

    /// <summary>Counts calldata's first word down to zero and stops, in one part: 10 opcodes a turn and 10 more.</summary>
    private static byte[] CountLoopCode() => Hex("6000355b8015601057600190036003565b5000");

    /// <summary>Calls <see cref="IlEvmAdaptiveFixture.Callee"/> as many times as calldata's first word says, each call ending a part.</summary>
    private static byte[] CallerCode() =>
        [.. Hex("6000355b80156030576000600060006000" + "73"), .. Callee.Bytes, .. Hex("5afa50600190036003565b5000")];

    private static byte[] Hex(string hex) => Nethermind.Core.Extensions.Bytes.FromHexString(hex);

    private static byte[] Word(int value)
    {
        byte[] word = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(word.AsSpan(28), value);
        return word;
    }

    /// <summary>
    /// Pairs at <paramref name="ratio"/> for <paramref name="check"/>: in each window given, a full ring of compiled samples
    /// at that share of the interpreter's time per opcode, then <paramref name="perWindow"/> interpreted buckets.
    /// </summary>
    private static void Feed(IlCheck check, double ratio, int perWindow, params int[] windows)
    {
        foreach (int window in windows)
        {
            for (int i = 0; i < IlCheck.RecentSamples; i++)
                check.AddCompiled((long)Math.Round(SampleTicks * ratio), SampleOps);
            for (int i = 0; i < perWindow; i++)
                check.AddInterpreted(SampleTicks, SampleOps, window);
        }
    }

    /// <summary>Kept early by its seventh pair at 0.8, over three windows, with no pair past it in the rolling ring.</summary>
    private static void Keep(IlCheck check)
    {
        Feed(check, 0.8, 3, 1, 3);
        Feed(check, 0.8, 1, 5);
        Assert.That((check.State, check.ViewForTests().Rolling), Is.EqualTo((IlCheck.Kept, 0)));
    }

    /// <summary>The check's first interpreted window and first compiled one, at the share its state takes.</summary>
    private static (int Interpreted, int Compiled) WindowsOf(IlCheck check)
    {
        int interpreted = -1, compiled = -1;
        for (int window = 1; interpreted < 0 || compiled < 0; window++)
        {
            if (check.IsInterpretedWindow(window))
                interpreted = interpreted < 0 ? window : interpreted;
            else
                compiled = compiled < 0 ? window : compiled;
        }

        return (interpreted, compiled);
    }

    [Test]
    public void Windows()
    {
        byte[] code = Unique(CountLoopCode(), 0x51);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        IlCheck check = contract.QueryCheck!;
        CodeInfo codeInfo = Stamped(code);
        // One part of 2,010 opcodes a frame: each timed part fills a bucket on its own.
        byte[] input = Word(200);
        Outcome expected = Interpret(code, input);
        (int interpreted, int compiled) = WindowsOf(check);

        // A compiled window: every part compiled, one in 8 timed, which fills the ring of compiled samples.
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(compiled));
        for (int i = 0; i < 400; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(400));
        Assert.That(Thread.Counters.SamplesCompiled, Is.InRange(24, 76), "one in 8: 50 expected");
        Assert.That(Thread.Counters.SamplesInterpreted, Is.Zero);
        Assert.That(check.ViewForTests().Recent, Is.EqualTo(IlCheck.RecentSamples));

        // Its settling tick: compiled, and timed on neither path.
        long samples = Thread.Counters.SamplesCompiled;
        IlCheckClock.SetForTests(IlCheckClock.SettleTickOf(compiled));
        for (int i = 0; i < 100; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(500));
        Assert.That(Thread.Counters.SamplesCompiled, Is.EqualTo(samples), "the settling tick records nothing");

        // An interpreted window: every part the runner admits runs on the interpreter, reported to its visit, one in four timed;
        // each timed part's bucket pairs with the ring, at most 8 from the window.
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(interpreted));
        for (int i = 0; i < 400; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(500), "no part ran compiled");
        Assert.That(Thread.Counters.PartsSwitchedBack, Is.EqualTo(400));
        Assert.That(Thread.Counters.PartsCompiled, Is.EqualTo(500), "switched-back parts do not count as compiled");
        Assert.That(Thread.Counters.SamplesInterpreted, Is.InRange(65, 135), "one in 4: 100 expected");
        IlThread.VisitView visit = Thread.VisitOf(StateOf(codeInfo))!.Value;
        Assert.That((visit.Ops, visit.Measured), Is.EqualTo((expected.OpCodes, true)), "an interpreted part still counts toward its visit");
        IlCheck.View view = check.ViewForTests();
        Assert.That((view.Pairs, view.PairWindows, view.State), Is.EqualTo((IlCheck.MaxPairsPerWindow, 1, IlCheck.Trial)), "one window decides nothing");

        // Its settling tick: interpreted all the same, untimed.
        samples = Thread.Counters.SamplesInterpreted;
        IlCheckClock.SetForTests(IlCheckClock.SettleTickOf(interpreted));
        for (int i = 0; i < 100; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(500));
        Assert.That(Thread.Counters.PartsSwitchedBack, Is.EqualTo(500));
        Assert.That(Thread.Counters.SamplesInterpreted, Is.EqualTo(samples), "the settling tick records nothing");
    }

    [Test]
    public void Windows_are_salted_shared_out_and_never_back_to_back()
    {
        IlEvm.IlContract contract = Ship(Unique(CountLoopCode(), 0x52))[0];
        IlCheck query = contract.QueryCheck!, block = contract.BlockCheck!;
        Assert.That(query.Salt, Is.EqualTo((uint)contract.Profile!.Id * 2));
        Assert.That(block.Salt, Is.EqualTo(query.Salt + 1));

        const int Windows = 100_000;
        foreach ((IlCheck check, int share) in new[] { (query, 8), (query, 32), (block, 32) })
        {
            int hits = 0, backToBack = 0;
            bool last = false;
            for (int window = 1; window <= Windows; window++)
            {
                bool hit = check.IsInterpretedWindow(window, share);
                hits += hit ? 1 : 0;
                backToBack += hit && last ? 1 : 0;
                last = hit;
            }

            double expected = 1.0 / share * (1 - 1.0 / share);
            Assert.That(backToBack, Is.Zero, $"share {share}: never two interpreted windows in a row");
            Assert.That(hits / (double)Windows, Is.InRange(expected * 0.9, expected * 1.1), $"share {share}");
        }

        // The two runners' checks of one contract take their windows apart: they share about as many as chance gives.
        int both = Enumerable.Range(1, Windows).Count(w => query.IsInterpretedWindow(w, 32) && block.IsInterpretedWindow(w, 32));
        Assert.That(both, Is.LessThan(200), "about 92 by chance");

        // The share follows the state: the queries' runner's undecided check takes one in 8, block processing's one in 32.
        int queryHits = Enumerable.Range(1, Windows).Count(query.IsInterpretedWindow);
        int blockHits = Enumerable.Range(1, Windows).Count(block.IsInterpretedWindow);
        Assert.That(queryHits, Is.EqualTo(Enumerable.Range(1, Windows).Count(w => query.IsInterpretedWindow(w, 8))));
        Assert.That(blockHits, Is.EqualTo(Enumerable.Range(1, Windows).Count(w => block.IsInterpretedWindow(w, 32))));
        Feed(query, 0.8, 3, 1, 3, 5);
        Assert.That(query.State, Is.EqualTo(IlCheck.Kept));
        Assert.That(Enumerable.Range(1, Windows).Count(query.IsInterpretedWindow), Is.EqualTo(Enumerable.Range(1, Windows).Count(w => query.IsInterpretedWindow(w, 32))),
            "kept: one in 32");
    }

    [Test]
    public void Verdicts()
    {
        byte[] fast = Unique(CountLoopCode(), 0x53), slow = Unique(CountLoopCode(), 0x54), live = Unique(CountLoopCode(), 0x55);
        List<IlEvm.IlContract> shipped = Ship(fast, slow);

        // Compiled code at 0.8 of the interpreter's time on every pair, over three windows: kept, early.
        IlCheck kept = shipped[0].QueryCheck!;
        Feed(kept, 0.8, 3, 1, 3, 5);
        Assert.That(kept.State, Is.EqualTo(IlCheck.Kept));
        Assert.That(kept.Ratio, Is.EqualTo(0.8).Within(1e-9));
        Assert.That(shipped[0].RunMask & IlRuntime.RunQuery, Is.Not.Zero);
        (long measuredPairs, double interpretedNs, double compiledNs) = kept.Measured();
        Assert.That(measuredPairs, Is.EqualTo(9));
        Assert.That(compiledNs / interpretedNs, Is.EqualTo(0.8).Within(1e-9));

        // At 1.3, a probation: the queries' runner's bit is cleared, block processing's are left, and its frames run interpreted.
        IlEvm.IlContract slowContract = shipped[1];
        RunCounter runs = CountRuns(slowContract);
        IlCheck check = slowContract.QueryCheck!;
        int blockBits = slowContract.RunMask & (IlRuntime.RunMain | IlRuntime.RunWorkers);
        long before = Environment.TickCount64;
        Feed(check, 1.3, 3, 1, 3, 5);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation));
        Assert.That(check.Ratio, Is.EqualTo(1.3).Within(1e-9));
        Assert.That(slowContract.RunMask & IlRuntime.RunQuery, Is.Zero);
        Assert.That(slowContract.RunMask & IlRuntime.CheckOffQuery, Is.Not.Zero);
        Assert.That(slowContract.RunMask & (IlRuntime.RunMain | IlRuntime.RunWorkers), Is.EqualTo(blockBits));
        IlCheck.View view = check.ViewForTests();
        Assert.That(view.RecheckAt - before, Is.InRange(30_000, 35_000));
        CodeInfo slowInfo = Stamped(slow);
        byte[] input = Word(100);
        Outcome expected = Interpret(slow, input);
        Assert.That(Run(QueryVm, slowInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.Zero, "on probation the queries' runner leaves it to the interpreter");

        // At the recheck the bit comes back and the check starts over.
        Assert.That(IlCheck.RestoreDue(Environment.TickCount64), Is.Zero, "not yet");
        Assert.That(IlCheck.RestoreDue(view.RecheckAt), Is.EqualTo(1));
        Assert.That(check.State, Is.EqualTo(IlCheck.Trial));
        Assert.That(slowContract.RunMask & (IlRuntime.RunQuery | IlRuntime.CheckOffQuery), Is.EqualTo(IlRuntime.RunQuery));
        Assert.That((check.ViewForTests().Checks, check.ViewForTests().Pairs, check.ViewForTests().Recent), Is.EqualTo((2, 0, 0)));
        (_, int compiledWindow) = WindowsOf(check);
        IlCheckClock.SetForTests(IlCheckClock.SettleTickOf(compiledWindow));
        Assert.That(Run(QueryVm, slowInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1), "compiled again");

        // A second loss outside the start-up window: a shipped contract keeps its code and its bit stays cleared, checked again a long while on.
        before = Environment.TickCount64;
        Feed(check, 1.3, 3, 11, 13, 15);
        Assert.That(check.State, Is.EqualTo(IlCheck.Masked));
        Assert.That(slowContract.RunMask & IlRuntime.RunQuery, Is.Zero);
        Assert.That(slowContract.Runners.All(static r => r is not null), Is.True, "a shipped contract keeps its code");
        Assert.That(slowContract.Profile!.Shipped, Is.SameAs(slowContract));
        Assert.That(check.ViewForTests().RecheckAt - before, Is.InRange(600_000, 605_000));
        Assert.That(Run(QueryVm, slowInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        Assert.That(IlCheck.RestoreDue(check.ViewForTests().RecheckAt), Is.EqualTo(1));
        Assert.That(check.State, Is.EqualTo(IlCheck.Trial), "rechecked");

        // A live contract the queries' runner promoted goes at its second loss: released, and its frames interpreted.
        IlEvm.IlContract promoted = Promote(live, IlOrigin.LiveRpc);
        IlCheck liveCheck = promoted.QueryCheck!;
        CodeInfo liveInfo = Stamped(live);
        Assert.That(Run(QueryVm, liveInfo, input), Is.EqualTo(Interpret(live, input)));
        Assert.That(StateOf(liveInfo).QueryRun, Is.SameAs(promoted));
        Feed(liveCheck, 1.3, 3, 1, 3, 5);
        Assert.That(liveCheck.State, Is.EqualTo(IlCheck.Probation));
        IlCheck.RestoreDue(liveCheck.ViewForTests().RecheckAt);
        int version = IlRuntime.Version;
        Feed(liveCheck, 1.3, 3, 11, 13, 15);
        Assert.That(liveCheck.State, Is.EqualTo(IlCheck.GivenBack));
        Assert.That(promoted.Profile!.Live, Is.Null);
        Assert.That(promoted.Runners.All(static r => r is null), Is.True, "its code released");
        Assert.That(IlRuntime.Version, Is.GreaterThan(version));
        Assert.That(Run(QueryVm, liveInfo, input), Is.EqualTo(Interpret(live, input)));
        Assert.That((IlEvmFrameRunner.CodeSlot(liveInfo) as IlEvm.IlCodeState)?.QueryRun, Is.Null);

        Assert.That(IlCheck.Verdicts(block: false), Is.EqualTo((1L, 2L, 1L, 1L, 3L)), "kept, probation, masked, given back, rechecked");
        Assert.That(IlCheck.Verdicts(block: true), Is.EqualTo((0L, 0L, 0L, 0L, 0L)));
    }

    [Test]
    public void A_live_contract_is_released_only_by_its_own_source()
    {
        // Promoted from block processing, it runs on both runners: the queries' runner losing on it twice masks it for
        // the queries alone, and block processing keeps it.
        byte[] code = Unique(CountLoopCode(), 0x56);
        IlEvm.IlContract promoted = Promote(code, IlOrigin.LiveBlocks);
        IlCheck query = promoted.QueryCheck!, block = promoted.BlockCheck!;
        Feed(query, 1.3, 3, 1, 3, 5);
        IlCheck.RestoreDue(query.ViewForTests().RecheckAt);
        Feed(query, 1.3, 3, 11, 13, 15);
        Assert.That(query.State, Is.EqualTo(IlCheck.Masked));
        Assert.That(promoted.Profile!.Live, Is.SameAs(promoted));
        Assert.That(promoted.RunMask & IlRuntime.RunAll, Is.EqualTo(IlRuntime.RunMain | IlRuntime.RunWorkers));

        // Block processing's runner losing on it twice releases it, through the hook the scheduler routes to a job.
        List<IlEvm.IlContract> released = [];
        IlCheck.ReleaseLive = released.Add;
        Feed(block, 1.3, 3, 1, 3, 5);
        Assert.That(block.State, Is.EqualTo(IlCheck.Probation));
        Assert.That(block.ViewForTests().RecheckAt - Environment.TickCount64, Is.InRange(590_000, 600_000), "block processing's probation is ten minutes");
        IlCheck.RestoreDue(block.ViewForTests().RecheckAt);
        Feed(block, 1.3, 3, 11, 13, 15);
        Assert.That(block.State, Is.EqualTo(IlCheck.GivenBack));
        Assert.That(released, Is.EqualTo(new[] { promoted }));
        Assert.That(promoted.RunMask & (IlRuntime.RunMain | IlRuntime.RunWorkers), Is.Zero);
        Assert.That(IlCheck.Verdicts(block: true).GivenBack, Is.EqualTo(1));
    }

    [Test]
    public void Rolling_loss()
    {
        List<IlEvm.IlContract> shipped = Ship(Unique(CountLoopCode(), 0x57), Unique(CountLoopCode(), 0x58));
        IlCheck check = shipped[0].QueryCheck!;
        Keep(check);

        // Kept, the check goes on: 14 pairs at 1.1 are not a block of them yet, the fifteenth is, and a loss.
        Feed(check, 1.1, 7, 7, 9);
        Assert.That((check.State, check.ViewForTests().Rolling), Is.EqualTo((IlCheck.Kept, 14)));
        Feed(check, 1.1, 1, 11);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation));
        Assert.That(check.RollingRatio, Is.EqualTo(1.1).Within(1e-9));
        Assert.That(shipped[0].RunMask & IlRuntime.RunQuery, Is.Zero);

        // Within the band between keeping and losing, a block is no loss: hysteresis.
        IlCheck steady = shipped[1].QueryCheck!;
        Keep(steady);
        Feed(steady, 1.0, 5, 7, 9, 11);
        Assert.That(steady.State, Is.EqualTo(IlCheck.Kept));
        Assert.That(steady.RollingRatio, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(steady.ViewForTests().Rolling, Is.Zero, "a new block begins");
    }

    [Test]
    public void Warm_up()
    {
        // In the start-up window a live contract's loss, and a shipped one's on block processing's runner, is a probation of
        // the early recheck's length, and none counts toward a give-back.
        IlEvmSettings.SelfCheckWarmUpMs = int.MaxValue;
        IlEvm.IlContract shipped = Ship(Unique(CountLoopCode(), 0x59))[0];
        IlEvm.IlContract live = Promote(Unique(CountLoopCode(), 0x5a), IlOrigin.LiveRpc);
        foreach (IlCheck check in new[] { shipped.BlockCheck!, live.QueryCheck! })
        {
            Assert.That(check.Active, Is.True);
            for (int round = 0; round < 4; round++)
            {
                long before = Environment.TickCount64;
                Feed(check, 1.3, 3, 1, 3, 5);
                IlCheck.View view = check.ViewForTests();
                Assert.That(view.State, Is.EqualTo(IlCheck.Probation), $"round {round}");
                Assert.That(view.RecheckAt - before, Is.InRange(5_000, 6_000), $"round {round}");
                Assert.That(view.LostOnce, Is.False);
                Assert.That(IlCheck.RestoreDue(view.RecheckAt), Is.EqualTo(1));
            }
        }

        // A shipped contract's check on the queries' runner takes no part in the window, so the list's contracts serve the
        // first calls compiled throughout; a loss fed to it all the same starts it over, the runner left on the contract.
        IlCheck exempt = shipped.QueryCheck!;
        Assert.That(exempt.Active, Is.False);
        for (int round = 0; round < 4; round++)
        {
            Feed(exempt, 1.3, 3, 1, 3, 5);
            IlCheck.View view = exempt.ViewForTests();
            Assert.That((view.State, view.Pairs, view.LostOnce, view.RecheckAt), Is.EqualTo((IlCheck.Trial, 0, false, 0L)), $"round {round}");
            Assert.That(shipped.RunMask & IlRuntime.RunQuery, Is.Not.Zero);
        }

        Assert.That(IlCheck.Verdicts(block: false), Is.EqualTo((0L, 4L, 0L, 0L, 4L)), "no permanent verdict, and none at all for the shipped contract");
        Assert.That(IlCheck.Verdicts(block: true), Is.EqualTo((0L, 4L, 0L, 0L, 4L)));
        Assert.That(live.Profile!.Live, Is.SameAs(live));

        // Past it, the first loss is a probation and the second gives back; the shipped contract's query check runs now.
        IlEvmSettings.SelfCheckWarmUpMs = 0;
        Assert.That(exempt.Active, Is.True);
        foreach ((IlCheck check, int verdict) in new[] { (shipped.QueryCheck!, IlCheck.Masked), (live.QueryCheck!, IlCheck.GivenBack) })
        {
            Feed(check, 1.3, 3, 1, 3, 5);
            Assert.That((check.State, check.ViewForTests().LostOnce), Is.EqualTo((IlCheck.Probation, true)));
            IlCheck.RestoreDue(check.ViewForTests().RecheckAt);
            Feed(check, 1.3, 3, 11, 13, 15);
            Assert.That(check.State, Is.EqualTo(verdict));
        }

        Assert.That(live.Profile!.Live, Is.Null);
    }

    [Test]
    public void Pairs_per_window_and_the_full_count()
    {
        List<IlEvm.IlContract> shipped = Ship(Unique(CountLoopCode(), 0x5b), Unique(CountLoopCode(), 0x5c), Unique(CountLoopCode(), 0x5d), Unique(CountLoopCode(), 0x5e));

        // One window gives at most 8 pairs however many buckets fill in it, and an early verdict needs three windows.
        IlCheck check = shipped[0].QueryCheck!;
        Feed(check, 0.8, 20, 1);
        Assert.That((check.ViewForTests().Pairs, check.ViewForTests().PairWindows, check.State), Is.EqualTo((8, 1, IlCheck.Trial)));
        Feed(check, 0.8, 20, 2);
        Assert.That((check.ViewForTests().Pairs, check.ViewForTests().PairWindows, check.State), Is.EqualTo((16, 2, IlCheck.Trial)));
        Feed(check, 0.8, 1, 3);
        Assert.That(check.State, Is.EqualTo(IlCheck.Kept));

        // Closer than the early margins, the verdict waits for all 31 pairs, and their median decides: kept at 0.9 ...
        IlCheck close = shipped[1].QueryCheck!;
        Feed(close, 0.9, 8, 1, 2, 3);
        Feed(close, 0.96, 6, 4);
        Assert.That((close.State, close.ViewForTests().Pairs), Is.EqualTo((IlCheck.Trial, 30)));
        Feed(close, 0.96, 1, 5);
        Assert.That(close.State, Is.EqualTo(IlCheck.Kept));
        Assert.That(close.Ratio, Is.EqualTo(0.9).Within(1e-9));

        // ... and lost at 1.0, short of the keep ratio's guard band.
        IlCheck lost = shipped[2].QueryCheck!;
        Feed(lost, 1.0, 8, 1, 2, 3, 4);
        Assert.That(lost.State, Is.EqualTo(IlCheck.Probation));
        Assert.That((lost.Ratio, lost.ViewForTests().Pairs), Is.EqualTo((1.0, IlCheck.Pairs)));

        // A pair needs a compiled side of two samples or more.
        IlCheck fresh = shipped[3].QueryCheck!;
        fresh.AddCompiled(SampleTicks, SampleOps);
        fresh.AddInterpreted(SampleTicks, SampleOps, 1);
        Assert.That(fresh.ViewForTests().Pairs, Is.Zero);
        fresh.AddCompiled(SampleTicks, SampleOps);
        fresh.AddInterpreted(SampleTicks, SampleOps, 1);
        Assert.That(fresh.ViewForTests().Pairs, Is.EqualTo(1));

        // Samples pool to a bucket of 1,024 opcodes before they count.
        fresh.AddInterpreted(500, 1_000, 1);
        Assert.That((fresh.ViewForTests().Pairs, fresh.ViewForTests().InterpretedOps), Is.EqualTo((1, 1_000L)));
        fresh.AddInterpreted(20, 24, 1);
        Assert.That((fresh.ViewForTests().Pairs, fresh.ViewForTests().InterpretedOps), Is.EqualTo((2, 0L)));
    }

    [Test]
    public void Block_samples_branch_only()
    {
        byte[] code = Unique(CountLoopCode(), 0x5f);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        IlCheck check = contract.BlockCheck!;
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(200);
        Outcome expected = Interpret(code, input);
        (int interpreted, int compiled) = WindowsOf(check);
        Outcome RunBlock() => Run(BlockVm, codeInfo, input);

        // A compiled window on a thread block processing's runner serves outside a branch, as the mempool's session's:
        // compiled, never timed.
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(compiled));
        for (int i = 0; i < 200; i++)
            Assert.That(RunBlock(), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(200));
        Assert.That(Thread.Counters.SamplesCompiled, Is.Zero);

        // In a branch the same thread's parts are timed, as a prewarmer's.
        IlRuntime.InBranch = true;
        for (int i = 0; i < 200; i++)
            Assert.That(RunBlock(), Is.EqualTo(expected));
        IlRuntime.InBranch = false;
        long inBranch = Thread.Counters.SamplesCompiled;
        Assert.That(inBranch, Is.InRange(6, 50), "one in 8: 25 expected");

        // Block processing's own thread in its processing pass is timed by the core's mark alone.
        for (int i = 0; i < 200; i++)
            Assert.That(OnBlockThread(RunBlock), Is.EqualTo(expected));
        Assert.That(Thread.Counters.SamplesCompiled - inBranch, Is.InRange(6, 50));

        // An interpreted window outside a branch: the parts run interpreted, the window holding on every thread, but untimed.
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(interpreted));
        for (int i = 0; i < 100; i++)
            Assert.That(RunBlock(), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(600));
        Assert.That((Thread.Counters.PartsSwitchedBack, Thread.Counters.SamplesInterpreted), Is.EqualTo((100L, 0L)));
        IlRuntime.InBranch = true;
        for (int i = 0; i < 100; i++)
            Assert.That(RunBlock(), Is.EqualTo(expected));
        Assert.That(Thread.Counters.SamplesInterpreted, Is.InRange(5, 50), "one in 4: 25 expected");
        Assert.That(contract.QueryCheck!.ViewForTests().CompiledOps + contract.QueryCheck.ViewForTests().Recent, Is.Zero, "the queries' check saw none of it");
    }

    [Test]
    public void A_loss_mid_visit_leaves_the_visit_unlearned()
    {
        // The callee every caller calls, shipped: a caller's two calls are two parts of the callee's one visit.
        IlEvm.IlContract callee = Ship(CalleeCode())[0];
        IlCheck check = callee.QueryCheck!;
        (int interpreted, _) = WindowsOf(check);
        // Six pairs at 4.0 over three windows: one short of an early loss. The seventh comes from the callee's first
        // part in an interpreted window, timed: its six opcodes complete a bucket filled to 1,020 at the feed's pace. At
        // 4.0 that pair stays past the early margin even if a collection holds the part up for a couple of milliseconds.
        Feed(check, 4.0, 2, interpreted + 100, interpreted + 200, interpreted + 300);
        Assert.That((check.State, check.ViewForTests().Pairs), Is.EqualTo((IlCheck.Trial, 6)));
        // The thread's own pool of the window's samples, as a timed part of it would leave it.
        Thread.PoolInterpretedForTests(check, 10_200, 1_020, interpreted);
        Assert.That(Thread.PooledOps(check, interpreted: true), Is.EqualTo(1_020), "pooled on the thread, not yet the check's");
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(interpreted));

        byte[] caller = Unique(CallerCode(), 0x60);
        byte[] input = Word(2);
        Outcome expected = Interpret(caller, input);
        // A seed whose first draw times the callee's first part: one in four are.
        uint seed = 1;
        while (true)
        {
            Thread.Reseed(seed);
            if ((Thread.Next() & (IlCheck.TimeInterpretedEvery - 1)) == 0)
                break;
            seed++;
        }

        Thread.Reseed(seed);
        Assert.That(Run(QueryVm, Stamped(caller), input), Is.EqualTo(expected));
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation), "the first part's sample lost");
        Assert.That(Thread.Counters.SamplesInterpreted, Is.EqualTo(1));
        // The second part met the runner taken off: the visit saw one part of two, and is neither learned from nor counted.
        IlThread.VisitView visit = Thread.VisitOf(callee.Profile!)!.Value;
        Assert.That((visit.Parts, visit.Ops, visit.Measured), Is.EqualTo((1, 6, false)));
        Thread.FoldAll();
        Assert.That(callee.Profile!.QueryVisits, Is.Null, "nothing learned");

        // Past the lift, parts the runner declines are no longer looked at.
        Assert.That(callee.RunMask & IlRuntime.CheckOffQuery, Is.Not.Zero);
        IlCheck.RestoreDue(Environment.TickCount64 + IlCheck.UnderWayMs);
        Assert.That(callee.RunMask & (IlRuntime.CheckOffQuery | IlRuntime.RunQuery), Is.Zero);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation), "the probation goes on");
        Assert.That(IlCheck.Awaiting, Is.EqualTo(1));
    }

    [Test]
    public void A_handed_back_part_is_timed_through_the_interpreters_rest()
    {
        byte[] code = Unique(CallerCode(), 0x61), other = Unique(CountLoopCode(), 0x62);
        IlEvm.IlContract contract = Ship(code)[0];
        IlCheck check = contract.QueryCheck!;
        CodeInfo codeInfo = Stamped(code), otherInfo = Stamped(other);
        Run(QueryVm, codeInfo, Word(1));
        IlEvm.IlCodeState state = StateOf(codeInfo);
        IlThread thread = Thread;
        long samples = thread.Counters.SamplesCompiled;

        void HandBack(int events)
        {
            QueryVm.OpCodeCount = 10;
            Assert.That(thread.Offer(QueryVm, state, codeInfo, pc: 40, block: false, counting: false), Is.EqualTo(IlGate.Compile));
            QueryVm.OpCodeCount = 610;
            thread.AfterCompiled(QueryVm, 10, codeInfo, handedBack: true);
            thread.ArmTimed(check, window: 7, interpreted: false, Stopwatch.GetTimestamp(), compiledOps: 600, events: events);
        }

        // Compiled code's 600 opcodes and the interpreter's 500 after them: one sample of the whole part.
        HandBack(contract.ChunkEvents);
        thread.FinishReport(codeInfo, 500);
        Assert.That((check.ViewForTests().Recent, thread.Counters.SamplesCompiled - samples), Is.EqualTo((1, 1L)));
        Assert.That(thread.VisitOf(state)!.Value.Ops, Is.EqualTo(1_100));

        // A part during which a chunk compiled waited on the JIT: dropped. So is one whose report another code's takes, or
        // that the next part's offer finds still armed.
        HandBack(contract.ChunkEvents - 1);
        thread.FinishReport(codeInfo, 500);
        HandBack(contract.ChunkEvents);
        thread.FinishReport(otherInfo, 500);
        thread.FinishReport(codeInfo, 500);
        HandBack(contract.ChunkEvents);
        thread.DetectTx(QueryVm, pc: 41, block: false);
        thread.FinishReport(codeInfo, 500);
        // Nor does a later part's report, armed with no sample of its own, finish one its report was dropped with.
        HandBack(contract.ChunkEvents);
        thread.DetectTx(QueryVm, pc: 41, block: false);
        thread.SwitchedBack(codeInfo);
        thread.FinishReport(codeInfo, 500);
        Assert.That((check.ViewForTests().Recent, check.ViewForTests().CompiledOps, thread.Counters.SamplesCompiled - samples), Is.EqualTo((1, 0L, 1L)));
    }

    [Test]
    public void Parts_that_meet_a_cold_chunk_are_not_timed()
    {
        // Laid out one instruction a chunk with only the entry chunk compiled: every part hands back at a chunk waiting
        // for the JIT, and says nothing of compiled code.
        IlEvmSettings.ChunkInstructions = 1;
        IlEvmSettings.ChunkThreshold = int.MaxValue;
        IlEvmSettings.EagerChunks = false;
        IlEvmSettings.CompileSynchronously = false;
        byte[] code = Unique(CountLoopCode(), 0x63);
        IlEvm.IlContract? contract = IlEvm.IlCompiler.Compile(new CodeInfo(code), IlEvm.IlGetFork(Bodies(Spec)), out string? reason);
        Assert.That(contract, Is.Not.Null, reason);
        Assert.That(contract!.CompileChunk(contract.EntryChunk[0]), Is.Not.Null);
        IlRuntime.PublishShipped([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), contract)]);
        IlCheck check = contract.QueryCheck!;
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(50);
        Outcome expected = Interpret(code, input);
        (_, int compiled) = WindowsOf(check);
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(compiled));
        for (int i = 0; i < 200; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(IlEvm.IlContract.HitColdChunk, Is.True);
        Assert.That(Thread.Counters.SamplesCompiled, Is.Zero);
        Assert.That((check.ViewForTests().CompiledOps, check.ViewForTests().Recent), Is.EqualTo((0L, 0)));
    }

    [Test]
    public void The_check_runs_from_its_delay_after_the_attach()
    {
        IlEvmSettings.SelfCheckDelayMs = 60_000;
        byte[] code = Unique(CountLoopCode(), 0x64);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(10);
        (int interpreted, _) = WindowsOf(contract.QueryCheck!);
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(interpreted));
        for (int i = 0; i < 10; i++)
            Run(QueryVm, codeInfo, input);
        Assert.That((runs.Runs, Thread.Counters.PartsSwitchedBack), Is.EqualTo((10, 0L)), "not yet: compiled, even in an interpreted window");
        Assert.That(contract.AttachedAt, Is.Not.Zero);

        contract.AttachedAt -= 61_000;
        for (int i = 0; i < 10; i++)
            Run(QueryVm, codeInfo, input);
        Assert.That((runs.Runs, Thread.Counters.PartsSwitchedBack), Is.EqualTo((10, 10L)));
    }

    [Test]
    public void The_gate_admits_and_the_check_switches_back()
    {
        // With the gate on, a visit learned long runs compiled from its first part, but not in an interpreted window.
        IlEvmSettings.Gate = true;
        IlGate.Query = IlGate.QueryPrior;
        byte[] code = Unique(CountLoopCode(), 0x65);
        IlEvm.IlContract contract = Ship(code)[0];
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(2_000);
        IlVisitBox box = contract.Profile!.VisitsFor(block: false);
        for (int i = 0; i < 3; i++)
            IlVisitStats.Learn(ref box.Stats, IlGate.EntryKey(input), 20_010);
        (int interpreted, int compiled) = WindowsOf(contract.QueryCheck!);
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(interpreted));
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(Interpret(code, input)));
        Assert.That((Thread.Counters.VisitsLong, Thread.Counters.PartsSwitchedBack, runs.Runs), Is.EqualTo((1L, 1L, 0)));
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(compiled));
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(Interpret(code, input)));
        Assert.That((Thread.Counters.VisitsLong, runs.Runs), Is.EqualTo((2L, 1)));
    }

    [Test]
    public void No_check_when_its_runner_has_it_off()
    {
        IlEvmSettings.BlockCheck = false;
        IlEvm.IlContract contract = Ship(Unique(CountLoopCode(), 0x66))[0];
        Assert.That(contract.BlockCheck, Is.Null);
        Assert.That(contract.QueryCheck, Is.Not.Null);

        IlEvmSettings.SelfCheck = false;
        Assert.That(Ship(Unique(CountLoopCode(), 0x67))[0].QueryCheck, Is.Null, "the self-check off turns every check off");
    }

    [Test]
    public void The_clock_ticks_and_ends_probations_once_a_window()
    {
        IlEvmSettings.SelfCheckRecheckMs = 0;
        IlCheck check = Ship(Unique(CountLoopCode(), 0x68))[0].QueryCheck!;
        Feed(check, 1.3, 3, 1, 3, 5);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation));

        IlCheckClock.ManualTicks = false;
        IlCheckClock.EnsureStarted();
        Assert.That(IlCheckClock.Running, Is.True);
        int start = IlCheckClock.Tick;
        Stopwatch waited = Stopwatch.StartNew();
        while ((IlCheckClock.Tick < start + 2 * IlCheckClock.WindowTicks || check.State != IlCheck.Trial) && waited.ElapsedMilliseconds < 10_000)
            System.Threading.Thread.Sleep(10);
        Assert.That(IlCheckClock.Tick, Is.GreaterThanOrEqualTo(start + 2 * IlCheckClock.WindowTicks), "a tick every 25 ms");
        Assert.That(check.State, Is.EqualTo(IlCheck.Trial), "the timer ended the probation");

        // Under the scheduler the timer leaves the probations to its maintenance while that runs between blocks ...
        IlCheck.SchedulerRestores = true;
        IlCheck.MaskedRecheckMs = 0;
        System.Threading.Volatile.Write(ref IlCheck.LastSchedulerRestore, Environment.TickCount64 + 60_000);
        Feed(check, 1.3, 3, 11, 13, 15);
        Assert.That(check.State, Is.EqualTo(IlCheck.Masked));
        System.Threading.Thread.Sleep(600);
        Assert.That(check.State, Is.EqualTo(IlCheck.Masked), "due, but left to the scheduler");

        // ... and ends them itself once the maintenance has not run for a while, as with blocks back to back.
        System.Threading.Volatile.Write(ref IlCheck.LastSchedulerRestore, Environment.TickCount64 - IlCheck.RestoreGraceMs);
        waited.Restart();
        while (check.State != IlCheck.Trial && waited.ElapsedMilliseconds < 10_000)
            System.Threading.Thread.Sleep(10);
        Assert.That(check.State, Is.EqualTo(IlCheck.Trial), "the timer ended the wait the scheduler did not");
        IlCheckClock.ManualTicks = true;
    }

    [Test]
    public void Block_checks_time_compiled_parts_at_the_kept_rate_once_their_ring_is_full()
    {
        // Block processing's pairs wait on interpreted windows that meet a block, a few an hour at the chain's tip: one in
        // 8 of its compiled parts is timed while the ring of compiled samples fills, one in 32 once it is full.
        byte[] code = Unique(CountLoopCode(), 0x6b);
        IlEvm.IlContract contract = Ship(code)[0];
        IlCheck check = contract.BlockCheck!;
        CodeInfo codeInfo = Stamped(code);
        // One part of 2,010 opcodes a frame: each timed part fills a bucket on its own.
        byte[] input = Word(200);
        Outcome expected = Interpret(code, input);
        int[] compiled = [.. Enumerable.Range(1, 400).Where(w => !check.IsInterpretedWindow(w)).Take(2)];
        IlRuntime.InBranch = true;
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(compiled[0]));
        for (int i = 0; i < 256; i++)
            Assert.That(Run(BlockVm, codeInfo, input), Is.EqualTo(expected));
        long filling = Thread.Counters.SamplesCompiled;
        Assert.That(filling, Is.InRange(16, 52), "one in 8: 32 expected of 256");
        Assert.That(check.ViewForTests().Recent, Is.EqualTo(IlCheck.RecentSamples), "the ring full");

        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(compiled[1]));
        for (int i = 0; i < 640; i++)
            Assert.That(Run(BlockVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(check.State, Is.EqualTo(IlCheck.Trial), "still undecided");
        Assert.That(Thread.Counters.SamplesCompiled - filling, Is.InRange(8, 36), "one in 32: 20 expected of 640, where one in 8 would time 80");

        // The queries' runner's undecided check, whose interpreted windows come every few seconds, times one in 8 throughout.
        IlRuntime.InBranch = false;
        IlCheck query = contract.QueryCheck!;
        Feed(query, 0.8, 0, 1);
        int queryWindow = Enumerable.Range(1, 400).First(w => !query.IsInterpretedWindow(w));
        IlCheckClock.SetForTests(IlCheckClock.TimedTickOf(queryWindow));
        long before = Thread.Counters.SamplesCompiled;
        for (int i = 0; i < 320; i++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(Thread.Counters.SamplesCompiled - before, Is.InRange(20, 64), "one in 8: 40 expected");
    }

    [Test]
    public void The_clock_lifts_the_marks_of_losses_whenever_and_ends_probations_the_scheduler_leaves()
    {
        // A loss under the scheduler, whose maintenance ended probations a moment ago: the timer's window lifts the mark
        // of a visit under way once its second is over, which runs nothing compiled, and leaves the probation alone.
        IlEvmSettings.SelfCheckRecheckMs = 0;
        IlCheck.SchedulerRestores = true;
        IlEvm.IlContract contract = Ship(Unique(CountLoopCode(), 0x6c))[0];
        IlCheck check = contract.QueryCheck!;
        Feed(check, 1.3, 3, 1, 3, 5);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation));
        long now = Environment.TickCount64;
        System.Threading.Volatile.Write(ref IlCheck.LastSchedulerRestore, now);
        Assert.That(IlCheckClock.OnWindow(now + IlCheck.UnderWayMs), Is.Zero);
        Assert.That(contract.RunMask & (IlRuntime.CheckOffQuery | IlRuntime.RunQuery), Is.Zero, "the mark lifted, the runner's bit still off");
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation), "the probation left to the scheduler, which ran lately");

        // With no maintenance since for longer than the grace, as with blocks back to back or a node syncing: the timer
        // ends it, as a window's maintenance would have.
        Assert.That(IlCheckClock.OnWindow(now + IlCheck.RestoreGraceMs - 1), Is.Zero);
        Assert.That(IlCheckClock.OnWindow(now + IlCheck.RestoreGraceMs), Is.EqualTo(1));
        Assert.That((check.State, contract.RunMask & IlRuntime.RunQuery), Is.EqualTo((IlCheck.Trial, (int)IlRuntime.RunQuery)));
    }

    [Test]
    public void The_exit_line_gives_each_runners_verdicts()
    {
        List<IlEvm.IlContract> shipped = Ship(Unique(CountLoopCode(), 0x69), Unique(CountLoopCode(), 0x6a));
        Feed(shipped[0].QueryCheck!, 0.8, 3, 1, 3, 5);
        Feed(shipped[1].BlockCheck!, 1.3, 3, 1, 3, 5);
        Assert.That(IlCheck.Describe(), Is.EqualTo(
            "check query on: 1 kept, 0 probation, 0 masked, 0 given back, 0 rechecked; block on: 0 kept, 1 probation, 0 masked, 0 given back, 0 rechecked; 1 awaiting a recheck"));
    }

    /// <summary>
    /// The check's cost per compiled part, end to end: a caller whose 200 calls make 201 parts of it a transaction, every
    /// part admitted (the gate off), its contract taking in turn no check, an undecided one in a compiled window (one part
    /// in 8 timed), a kept one (one in 32), and the undecided one in a window's settling tick (none timed).
    /// </summary>
    /// <remarks>One contract throughout, its check swapped per arm, so that only the check differs between the arms.</remarks>
    [Test, Explicit("Timing")]
    public void Hot_path_cost()
    {
        // Performance cores only, at high priority, as the gate's timing takes them (ILEVM_AFFINITY, a hex mask).
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
            long.TryParse(Environment.GetEnvironmentVariable("ILEVM_AFFINITY"), System.Globalization.NumberStyles.HexNumber, null, out long affinity) && affinity > 0)
        {
            using Process self = Process.GetCurrentProcess();
            self.ProcessorAffinity = (nint)affinity;
            self.PriorityClass = ProcessPriorityClass.High;
        }

        const int Calls = 200, Transactions = 200, Rounds = 61;
        byte[] input = Word(Calls);
        byte[] code = Unique(CallerCode(), 0x70);
        IlEvm.IlContract contract = Ship(code)[0];
        IlCheck undecided = contract.QueryCheck!, kept = new(contract, block: false);
        Keep(kept);
        CodeInfo codeInfo = Stamped(code);
        int undecidedTick = IlCheckClock.TimedTickOf(WindowsOf(undecided).Compiled), keptTick = IlCheckClock.TimedTickOf(WindowsOf(kept).Compiled);
        int settleTick = IlCheckClock.SettleTickOf(WindowsOf(undecided).Compiled);

        double Time(IlCheck? check, int tick)
        {
            contract.QueryCheck = check;
            IlCheckClock.SetForTests(tick);
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Transactions; i++)
                Run(QueryVm, codeInfo, input);
            return (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / Transactions;
        }

        (string Name, IlCheck? Check, int Tick)[] arms =
        [
            ("no check", null, undecidedTick),
            ("undecided", undecided, undecidedTick),
            ("kept", kept, keptTick),
            ("settling", undecided, settleTick),
        ];

        for (int pass = 0; pass < 3; pass++)
        {
            foreach ((string _, IlCheck? check, int tick) in arms)
                Time(check, tick);
        }

        // Each round times every arm, in an order of its own; an arm's cost is the median over rounds of its difference
        // from the unchecked arm's time in the same round.
        double[][] times = [.. arms.Select(_ => new double[Rounds])];
        Random order = new(17);
        int[] turns = [.. Enumerable.Range(0, arms.Length)];
        for (int round = 0; round < Rounds; round++)
        {
            order.Shuffle(turns);
            foreach (int a in turns)
                times[a][round] = Time(arms[a].Check, arms[a].Tick);
        }

        contract.QueryCheck = undecided;
        Assert.That((undecided.State, kept.State), Is.EqualTo((IlCheck.Trial, IlCheck.Kept)), "no window interpreted, no verdict");
        static double Median(IEnumerable<double> values)
        {
            double[] sorted = [.. values.Order()];
            return sorted[sorted.Length / 2];
        }

        string results = string.Join(", ", arms.Select((a, i) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{a.Name} {Median(times[i]) / 1000:F2} us ({Median(times[i].Select((t, r) => t - times[0][r])) / (Calls + 1):+0.00;-0.00} ns a part)")));
        TestContext.Out.WriteLine($"check, end to end, {Calls + 1} compiled parts a transaction, medians of {Rounds} rounds: {results}");
    }
}
