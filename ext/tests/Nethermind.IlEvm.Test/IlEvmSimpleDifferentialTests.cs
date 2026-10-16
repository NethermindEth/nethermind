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
    /// The random programs of <see cref="Random_programs_match_the_interpreter"/>, seed for seed, under the Simple rules:
    /// each shipped as a startup list entry, found by its bytes as override code is, with the callee found by its keccak,
    /// and every part run compiled. With <paramref name="coldChunks"/> a random half of a third of the programs' chunks
    /// stay cold, so their parts hand back mid-frame; with <paramref name="promoted"/> the programs are not shipped but
    /// counted on the interpreter until a promotion round compiles them, so they run the live path's contracts. Output,
    /// gas, logs, storage and the opcode count match the interpreter's at every gas limit the Legacy run tries.
    /// </summary>
    [TestCase(1, 400, false)]
    [TestCase(5, 400, true)]
    [TestCase(8, 300, false, 6)]
    [TestCase(11, 300, false, 1_400, true)]
    [TestCase(14, 300, false, 6, true)]
    [TestCase(2, 400, false, 1_400, false, true)]
    [TestCase(6, 400, true, 6, false, true)]
    [TestCase(3, 200, false, 1_400, false, false, true)]
    [TestCase(12, 150, true, 6, true, false, true)]
    public void Random_programs_under_the_simple_rules_match_the_interpreter(int seed, int programs, bool memoryHeavy, int chunkInstructions = 1_400,
        bool cancelable = false, bool coldChunks = false, bool promoted = false)
    {
        Random random = new(seed);
        Random layout = new(seed * 7_919);
        int savedChunk = IlEvmSettings.ChunkInstructions;
        IlMode savedMode = IlEvmSettings.Mode;
        bool savedPromote = IlSimple.Promote, savedManual = IlSimple.ManualForTests;
        long savedOps = IlSimple.PromoteOps;
        IlEvmSettings.ChunkInstructions = chunkInstructions;
        IlSimple.ResetForTests();
        IlSimple.ManualForTests = true;
        IlSimple.Promote = promoted;
        IlSimple.PromoteOps = 1;
        _cancelable = cancelable;
        OpcodeBodySet bodies = IlEvm.BodySetFor(_spec)!;
        int runs = 0, cold = 0, unpromoted = 0;
        try
        {
            IlEvm.IlContract? callee = IlEvm.Precompile(CalleeCode(), bodies, out string? calleeReason);
            Assert.That(callee, Is.Not.Null, calleeReason);
            IlSimple.PublishStartup([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(CalleeCode())), callee!)]);
            for (int p = 0; p < programs; p++)
            {
                byte[] code = Generate(random, random.Next(4, 160), memoryHeavy);
                byte[] input = new byte[random.Next(0, 70)];
                random.NextBytes(input);
                bool someCold = coldChunks && layout.Next(3) == 0;
                IlEvm.IlContract? contract;
                if (promoted)
                {
                    // Counted on the interpreter, one part in eight drawn at random, until a sampled part's opcodes are
                    // counted and a round promotes it.
                    IlEvmSettings.Mode = IlMode.Simple;
                    for (int i = 0; i < 32 * IlSimple.SampleEvery && (IlSimple.ProfileOf(code)?.Ops ?? 0) == 0; i++)
                        Run(new CodeInfo(code), input, 400_000, compiledCode: true);
                    IlEvmSettings.Mode = savedMode;
                    IlSimple.Round(1.0);
                    contract = IlSimple.ProfileOf(code)?.Promoted;
                    // A program that faults on its first opcode counts none, and is never promoted.
                    if (contract is null)
                    {
                        unpromoted++;
                        continue;
                    }
                }
                else
                {
                    contract = someCold ? IlEvm.IlCompiler.Compile(new CodeInfo(code), IlEvm.IlGetFork(bodies), out string? reason) : IlEvm.Precompile(code, bodies, out reason);
                    Assert.That(contract, Is.Not.Null, $"program {p} seed {seed}: {reason}");
                    for (int chunk = 0; someCold && chunk < contract!.ChunkCount; chunk++)
                    {
                        if (layout.Next(2) == 0)
                            contract.CompileChunk(chunk);
                    }

                    if (someCold)
                        cold++;
                    IlSimple.PublishStartup([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), contract!)]);
                }

                CodeInfo codeInfo = new(code);
                foreach (ulong gas in GasLimits(random))
                {
                    _cancelAfter = int.MaxValue;
                    Outcome expected = Run(codeInfo, input, gas, compiledCode: false);
                    Outcome actual = RunSimple(codeInfo, input, gas);
                    runs++;
                    if (!Same(expected, actual))
                        Assert.Fail($"program {p} seed {seed} gas {gas}{(someCold ? " (cold chunks)" : "")}\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {expected}\nsimple      {actual}");

                    if (_cancelable && expected.Polls > 2)
                    {
                        _cancelAfter = random.Next(1, expected.Polls - 1);
                        Outcome e = Run(codeInfo, input, gas, compiledCode: false);
                        Outcome a = RunSimple(codeInfo, input, gas);
                        runs++;
                        int cancelAfter = _cancelAfter;
                        _cancelAfter = int.MaxValue;
                        if (!Same(e, a))
                            Assert.Fail($"program {p} seed {seed} gas {gas} (cancelled after {cancelAfter} polls)\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {e}\nsimple      {a}");
                    }

                    if (!expected.IsError && gas == 400_000)
                    {
                        ulong used = gas - expected.GasLeft;
                        for (ulong back = 1; back <= Math.Min(used, 48UL); back++)
                        {
                            ulong limit = used - back;
                            Outcome e = Run(codeInfo, input, limit, compiledCode: false);
                            Outcome a = RunSimple(codeInfo, input, limit);
                            runs++;
                            if (!Same(e, a))
                                Assert.Fail($"program {p} seed {seed} gas {limit} (boundary)\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {e}\nsimple      {a}");
                        }
                    }
                }

                Assert.That((IlEvmFrameRunner.CodeSlot(codeInfo) as IlSimpleState)?.Query, Is.SameAs(contract), $"program {p}: run under the Simple rules");
            }
        }
        finally
        {
            IlEvmSettings.ChunkInstructions = savedChunk;
            IlEvmSettings.Mode = savedMode;
            IlSimple.Promote = savedPromote;
            IlSimple.PromoteOps = savedOps;
            IlSimple.ResetForTests();
            IlSimple.ManualForTests = savedManual;
            _cancelable = false;
            _cancelAfter = int.MaxValue;
        }

        (long compiled, long interpreted, _, _) = IlSimpleThread.Totals();
        TestContext.Out.WriteLine($"seed {seed}: {runs} paired runs of {programs} programs ({cold} with cold chunks{(promoted ? $", promoted live, {unpromoted} never promoted" : "")}); parts so far {compiled} compiled, {interpreted} interpreted");
        Assert.That(unpromoted, Is.LessThan(programs / 10), "nearly every program is promoted");
    }

    /// <summary>A run under the Simple rules, cold chunks left to hand back.</summary>
    private Outcome RunSimple(CodeInfo codeInfo, byte[] input, ulong gas)
    {
        IlMode mode = IlEvmSettings.Mode;
        IlEvmSettings.Mode = IlMode.Simple;
        try
        {
            return Run(codeInfo, input, gas, compiledCode: true, compileColdChunks: false);
        }
        finally
        {
            IlEvmSettings.Mode = mode;
        }
    }
}
