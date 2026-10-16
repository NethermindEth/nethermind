// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

public partial class IlEvmDifferentialTests
{
    /// <summary>
    /// The random programs, each shipped as the startup compile ships a list entry and run under the Adaptive rules:
    /// found by content, as code from a state override is, they run compiled and match the interpreter, hand-backs,
    /// faults and cancellation included. With <paramref name="ahead"/> 1 each also has an ahead copy, as the RPC list's
    /// entries do, half its parts on either code; with 2 the ahead layout alone is shipped, every part on the ahead twins.
    /// </summary>
    [TestCase(1, 300, false)]
    [TestCase(5, 300, true)]
    [TestCase(14, 200, false, 6, true)]
    [TestCase(2, 300, false, 1_400, false, 1)]
    [TestCase(6, 300, true, 1_400, true, 1)]
    [TestCase(9, 200, false, 6, true, 1)]
    [TestCase(3, 300, false, 6, false, 2)]
    [TestCase(7, 300, true, 1_400, true, 2)]
    public void Random_programs_shipped_under_the_adaptive_rules_match_the_interpreter(int seed, int programs, bool memoryHeavy, int chunkInstructions = 1_400, bool cancelable = false, int ahead = 0)
    {
        Random random = new(seed);
        int savedChunk = IlEvmSettings.ChunkInstructions, savedOwnMask = IlWarmUp.AheadOwnMask;
        IlMode savedMode = IlEvmSettings.Mode;
        bool savedCounting = IlRuntime.QueryCounting, savedGate = IlEvmSettings.Gate;
        IlWarmUp.AheadOwnMask = 1;
        IlEvmSettings.ChunkInstructions = chunkInstructions;
        IlRuntime.QueryCounting = false;
        // Every part compiled: the gate would leave these short programs' first visits to the interpreter.
        IlEvmSettings.Gate = false;
        _cancelable = cancelable;
        OpcodeBodySet bodies = IlEvm.BodySetFor(_spec)!;
        try
        {
            for (int p = 0; p < programs; p++)
            {
                byte[] code = Generate(random, random.Next(4, 160), memoryHeavy);
                IlEvm.IlContract? contract = ahead == 2 ? IlEvm.PrecompileAhead(code, bodies, out string? reason) : IlEvm.Precompile(code, bodies, out reason);
                Assert.That(contract, Is.Not.Null, $"program {p} seed {seed}: {reason}");
                if (ahead == 1)
                {
                    contract!.Ahead = IlEvm.PrecompileAhead(code, bodies, out reason);
                    Assert.That(contract.Ahead, Is.Not.Null, $"program {p} seed {seed}, its ahead copy: {reason}");
                }

                IlRuntime.PublishShipped([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), contract!)]);
                CodeInfo codeInfo = new(code);
                byte[] input = new byte[random.Next(0, 70)];
                random.NextBytes(input);
                foreach (ulong gas in GasLimits(random))
                {
                    _cancelAfter = int.MaxValue;
                    Outcome expected = Run(codeInfo, input, gas, compiledCode: false);
                    IlEvmSettings.Mode = IlMode.Adaptive;
                    Outcome actual = Run(codeInfo, input, gas, compiledCode: true);
                    if (_cancelable && expected.Polls > 2)
                    {
                        // Both machines reach this many polls, whichever ran a part of the frame.
                        _cancelAfter = random.Next(1, expected.Polls - 1);
                        IlEvmSettings.Mode = savedMode;
                        Outcome e = Run(codeInfo, input, gas, compiledCode: false);
                        IlEvmSettings.Mode = IlMode.Adaptive;
                        Outcome a = Run(codeInfo, input, gas, compiledCode: true);
                        if (!Same(e, a))
                            Assert.Fail($"program {p} seed {seed} gas {gas} (cancelled after {_cancelAfter} polls)\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {e}\ncompiled    {a}");
                    }

                    IlEvmSettings.Mode = savedMode;
                    if (!Same(expected, actual))
                        Assert.Fail($"program {p} seed {seed} gas {gas}\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {expected}\ncompiled    {actual}");
                }

                Assert.That((IlEvmFrameRunner.CodeSlot(codeInfo) as IlEvm.IlCodeState)?.QueryRun, Is.SameAs(contract), $"program {p}: found by content");
            }
        }
        finally
        {
            IlEvmSettings.ChunkInstructions = savedChunk;
            IlEvmSettings.Mode = savedMode;
            IlRuntime.QueryCounting = savedCounting;
            IlEvmSettings.Gate = savedGate;
            IlWarmUp.AheadOwnMask = savedOwnMask;
            _cancelable = false;
            _cancelAfter = int.MaxValue;
            IlEvm.ClearPrecompiled();
        }
    }

    /// <summary>
    /// The random programs of <see cref="Random_programs_match_the_interpreter"/>, seed for seed, shipped and run under
    /// the Adaptive rules with the visit gate in chaos mode: every visit takes a random mode, switch point and
    /// measurement, so a frame's parts move between compiled code and the interpreter at random, and the callee the
    /// programs call is gated too. Chunks compile as frames first reach them, as in the Legacy run, but for a third of
    /// the programs of the seeds that never cancel, which keep a random half of their chunks cold, so their compiled
    /// parts hand back and are reported. Output, gas, logs, storage and the opcode count match the interpreter's at every
    /// gas limit the Legacy run tries, and a cancelable run never polls fewer times than the interpreter.
    /// </summary>
    /// <remarks>
    /// The switchback check runs from the first part, in chaos too: each admitted part runs interpreted or compiled at
    /// random, one in two timed, and the clock moves on with every run. Every 16 programs a loss is primed on the callee's
    /// check, which its next timed interpreted part completes, so the runner is taken off the callee in the middle of a
    /// program's run, and given it back before the next primed one.
    /// </remarks>
    [TestCase(1, 2000, false)]
    [TestCase(2, 2000, false)]
    [TestCase(3, 2000, false)]
    [TestCase(4, 3000, false)]
    [TestCase(5, 2000, true)]
    [TestCase(6, 2000, true)]
    [TestCase(7, 3000, true)]
    [TestCase(8, 2000, false, 6)]
    [TestCase(9, 2000, true, 5)]
    [TestCase(10, 1500, false, 1)]
    [TestCase(11, 2000, false, 1_400, true)]
    [TestCase(12, 2000, true, 1_400, true)]
    [TestCase(13, 1500, false, 1, true)]
    [TestCase(14, 1500, false, 6, true)]
    [TestCase(15, 1500, false, 1_400, false, true)]
    [TestCase(16, 1500, true, 1_400, true, true)]
    [TestCase(17, 1000, false, 6, true, true)]
    public void Random_programs_match_the_interpreter_with_random_gate(int seed, int programs, bool memoryHeavy, int chunkInstructions = 1_400, bool cancelable = false, bool ahead = false)
    {
        Random random = new(seed);
        // The cold chunks come from a generator of their own, and the gate's draws from the thread's, reseeded: the
        // programs, inputs and gas limits stay those of the seed's Legacy run, and a failure repeats.
        Random layout = new(seed * 7_919);
        int savedChunk = IlEvmSettings.ChunkInstructions, savedChunkThreshold = IlEvmSettings.ChunkThreshold;
        IlMode savedMode = IlEvmSettings.Mode;
        bool savedCounting = IlRuntime.QueryCounting, savedChaos = IlGate.Chaos, savedGate = IlEvmSettings.Gate;
        bool savedSelfCheck = IlEvmSettings.SelfCheck, savedQueryCheck = IlEvmSettings.QueryCheck, savedManualTicks = IlCheckClock.ManualTicks;
        int savedCheckDelay = IlEvmSettings.SelfCheckDelayMs, savedWarmUp = IlEvmSettings.SelfCheckWarmUpMs, savedOwnMask = IlWarmUp.AheadOwnMask;
        IlEvmSettings.ChunkInstructions = chunkInstructions;
        // With ahead copies, half of the compiled parts run each code: a frame moves between them at random as well.
        IlWarmUp.AheadOwnMask = 1;
        // A cold chunk stays cold, so its frames hand back every time.
        IlEvmSettings.ChunkThreshold = int.MaxValue;
        IlRuntime.QueryCounting = false;
        IlEvmSettings.Gate = true;
        IlGate.Chaos = true;
        IlEvmSettings.SelfCheck = true;
        IlEvmSettings.QueryCheck = true;
        IlEvmSettings.SelfCheckDelayMs = 0;
        // Out of the start-up window, in which the queries' runner's check leaves shipped contracts alone.
        IlEvmSettings.SawFrame();
        IlEvmSettings.SelfCheckWarmUpMs = 0;
        IlCheckClock.ManualTicks = true;
        IlThread gate = IlThread.Current;
        gate.Reseed((uint)seed);
        IlGateCounters before = gate.Counters;
        (long Kept, long Probation, long Masked, long GivenBack, long Rechecked) verdicts = IlCheck.Verdicts(block: false);
        _cancelable = cancelable;
        OpcodeBodySet bodies = IlEvm.BodySetFor(_spec)!;
        int runs = 0, cold = 0, fewerPolls = 0;
        try
        {
            IlEvm.IlContract callee = ShipForChaos(CalleeCode(), bodies, layout, coldChunks: false);
            callee.CompileAllChunks();
            for (int p = 0; p < programs; p++)
            {
                if (p % 16 == 0)
                {
                    IlCheck.RestoreDue(long.MaxValue);
                    PrimeLoss(callee.QueryCheck!);
                }

                byte[] code = Generate(random, random.Next(4, 160), memoryHeavy);
                bool coldChunks = !cancelable && layout.Next(3) == 0;
                if (coldChunks)
                    cold++;
                IlEvm.IlContract contract = ShipForChaos(code, bodies, layout, coldChunks, ahead);
                CodeInfo codeInfo = new(code);
                byte[] input = new byte[random.Next(0, 70)];
                random.NextBytes(input);
                foreach (ulong gas in GasLimits(random))
                {
                    _cancelAfter = int.MaxValue;
                    Outcome expected = Run(codeInfo, input, gas, compiledCode: false);
                    Outcome actual = RunGated(codeInfo, input, gas, coldChunks);
                    runs++;
                    if (!Same(expected, actual))
                        Assert.Fail($"program {p} seed {seed} gas {gas}{(coldChunks ? " (cold chunks)" : "")}\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {expected}\ngated       {actual}");
                    if (_cancelable && actual.Polls < expected.Polls)
                        fewerPolls++;

                    if (_cancelable && expected.Polls > 2)
                    {
                        // Both machines reach this many polls, whichever ran a part of the frame.
                        _cancelAfter = random.Next(1, expected.Polls - 1);
                        Outcome e = Run(codeInfo, input, gas, compiledCode: false);
                        Outcome a = RunGated(codeInfo, input, gas, coldChunks);
                        runs++;
                        int cancelAfter = _cancelAfter;
                        _cancelAfter = int.MaxValue;
                        if (!Same(e, a))
                            Assert.Fail($"program {p} seed {seed} gas {gas} (cancelled after {cancelAfter} polls)\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {e}\ngated       {a}");
                    }

                    // Every limit just below a completed run's usage puts the out-of-gas point on each of the last instructions.
                    if (!expected.IsError && gas == 400_000)
                    {
                        ulong used = gas - expected.GasLeft;
                        for (ulong back = 1; back <= Math.Min(used, 48UL); back++)
                        {
                            ulong limit = used - back;
                            Outcome e = Run(codeInfo, input, limit, compiledCode: false);
                            Outcome a = RunGated(codeInfo, input, limit, coldChunks);
                            runs++;
                            if (!Same(e, a))
                                Assert.Fail($"program {p} seed {seed} gas {limit} (boundary){(coldChunks ? " (cold chunks)" : "")}\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {e}\ngated       {a}");
                        }
                    }
                }

                Assert.That((IlEvmFrameRunner.CodeSlot(codeInfo) as IlEvm.IlCodeState)?.QueryRun, Is.SameAs(contract), $"program {p}: run under the Adaptive rules");
            }

            (long Kept, long Probation, long Masked, long GivenBack, long Rechecked) now = IlCheck.Verdicts(block: false);
            verdicts = (now.Kept - verdicts.Kept, now.Probation - verdicts.Probation, now.Masked - verdicts.Masked, now.GivenBack - verdicts.GivenBack, now.Rechecked - verdicts.Rechecked);
        }
        finally
        {
            IlEvmSettings.ChunkInstructions = savedChunk;
            IlEvmSettings.ChunkThreshold = savedChunkThreshold;
            IlEvmSettings.Mode = savedMode;
            IlEvmSettings.Gate = savedGate;
            IlRuntime.QueryCounting = savedCounting;
            IlGate.Chaos = savedChaos;
            IlEvmSettings.SelfCheck = savedSelfCheck;
            IlEvmSettings.QueryCheck = savedQueryCheck;
            IlEvmSettings.SelfCheckDelayMs = savedCheckDelay;
            IlEvmSettings.SelfCheckWarmUpMs = savedWarmUp;
            IlWarmUp.AheadOwnMask = savedOwnMask;
            IlCheckClock.ManualTicks = savedManualTicks;
            _cancelable = false;
            _cancelAfter = int.MaxValue;
            IlEvm.ClearPrecompiled();
        }

        IlGateCounters after = gate.Counters;
        long visits = after.VisitsChaos - before.VisitsChaos, switched = after.VisitsSwitched - before.VisitsSwitched;
        long compiled = after.PartsCompiled - before.PartsCompiled, interpreted = after.PartsInterpreted - before.PartsInterpreted;
        long switchedBack = after.PartsSwitchedBack - before.PartsSwitchedBack, samples = after.SamplesCompiled + after.SamplesInterpreted - before.SamplesCompiled - before.SamplesInterpreted;
        TestContext.Out.WriteLine($"seed {seed}: {runs} paired runs of {programs} programs ({cold} with cold chunks), {visits} chaos visits ({switched} switched mid-visit), " +
            $"parts {compiled} compiled, {interpreted} interpreted and {switchedBack} switched back by the check, {samples} check samples, " +
            $"verdicts {verdicts.Kept} kept, {verdicts.Probation} probation, {verdicts.Masked} masked, {verdicts.Rechecked} rechecked; {fewerPolls} runs polled fewer times than the interpreter");
        Assert.That(visits, Is.GreaterThan(0));
        Assert.That(switched, Is.GreaterThan(0));
        Assert.That(compiled, Is.GreaterThan(0));
        Assert.That(interpreted, Is.GreaterThan(0));
        Assert.That(switchedBack, Is.GreaterThan(0));
        Assert.That(samples, Is.GreaterThan(0));
        Assert.That(verdicts.Probation + verdicts.Masked, Is.GreaterThan(0), "the callee was taken off mid-run");
        // The gate never splits a part, so it never moves a poll: only a hand-back's fresh interval could, by one, and
        // none of these programs' does.
        Assert.That(fewerPolls, Is.Zero, "cancellation polls are never fewer than the interpreter's");
    }

    /// <summary>
    /// Six pairs against compiled code over three windows of their own, and an interpreted bucket four opcodes short: the
    /// next timed interpreted part of the code makes the seventh pair, an early loss.
    /// </summary>
    private static void PrimeLoss(IlCheck check)
    {
        foreach (int window in new[] { -30, -20, -10 })
        {
            for (int i = 0; i < IlCheck.RecentSamples; i++)
                check.AddCompiled(40_960, 2_048);
            for (int i = 0; i < 2; i++)
                check.AddInterpreted(20_480, 2_048, window);
        }

        check.AddInterpreted(10_200, 1_020, IlCheckClock.Tick / IlCheckClock.WindowTicks);
    }

    /// <summary>A gated run under the Adaptive rules; with <paramref name="coldChunks"/>, a chunk not compiled is left to hand the frame back.</summary>
    /// <remarks>The check's clock moves on by a third of a window a run.</remarks>
    private Outcome RunGated(CodeInfo codeInfo, byte[] input, ulong gas, bool coldChunks)
    {
        IlCheckClock.SetForTests(IlCheckClock.Tick + 3);
        IlMode mode = IlEvmSettings.Mode;
        IlEvmSettings.Mode = IlMode.Adaptive;
        try
        {
            return Run(codeInfo, input, gas, compiledCode: true, compileColdChunks: !coldChunks);
        }
        finally
        {
            IlEvmSettings.Mode = mode;
        }
    }

    /// <summary>
    /// Ships <paramref name="code"/> as a list entry, laid out with no chunk compiled, or for <paramref name="coldChunks"/>
    /// each compiled on a coin flip; with <paramref name="ahead"/> and no cold chunks, with an ahead copy compiled whole, as
    /// the RPC list's entries get.
    /// </summary>
    private static IlEvm.IlContract ShipForChaos(byte[] code, OpcodeBodySet bodies, Random layout, bool coldChunks, bool ahead = false)
    {
        IlEvm.IlContract? contract = IlEvm.IlCompiler.Compile(new CodeInfo(code), IlEvm.IlGetFork(bodies), out string? reason);
        for (int chunk = 0; coldChunks && contract is not null && chunk < contract.ChunkCount; chunk++)
        {
            if (layout.Next(2) == 0)
                contract.CompileChunk(chunk);
        }

        Assert.That(contract, Is.Not.Null, reason);
        if (ahead && !coldChunks)
        {
            contract!.Ahead = IlEvm.PrecompileAhead(code, bodies, out reason);
            Assert.That(contract.Ahead, Is.Not.Null, reason);
        }
        IlRuntime.PublishShipped([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), contract!)]);
        return contract!;
    }
}
