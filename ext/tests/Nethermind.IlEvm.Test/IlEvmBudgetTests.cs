// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Linq;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>The bounds on what compiling may cost, and the release of what the self-check gives back.</summary>
public partial class IlEvmDifferentialTests
{
    /// <summary>A loop of <paramref name="count"/> complements of a copied word, different per <paramref name="seed"/>, that runs until gas runs out.</summary>
    /// <remarks>Each body compiles to IL of its own, where pushes that are dropped compile to none.</remarks>
    private static byte[] Filler(int count, int seed) =>
        Loop("filler", "budget", count * 3 + 40, new Bytecode().PushWord(seed),
            new Bytecode().Op(Instruction.DUP1, Instruction.NOT, Instruction.POP)).Code;

    [Test]
    public void A_contract_past_the_IL_budget_stays_on_the_interpreter()
    {
        int saved = IlEvmSettings.MaxContractIlBytes;
        try
        {
            IlEvmSettings.MaxContractIlBytes = 4_096;
            CodeInfo codeInfo = new(Filler(400, 1));
            Outcome expected = Run(codeInfo, [], 200_000, compiledCode: false);
            Outcome actual = Run(codeInfo, [], 200_000, compiledCode: true);
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(IlEvm.IsCompiled(codeInfo), Is.False);
            Assert.That(IlEvm.FailureReason(codeInfo), Does.Contain("bytes of IL"));
        }
        finally
        {
            IlEvmSettings.MaxContractIlBytes = saved;
        }
    }

    [Test]
    public void The_shared_cache_keeps_to_its_byte_budget()
    {
        long saved = IlEvmSettings.MaxCachedIlBytes;
        try
        {
            IlEvmSettings.MaxCachedIlBytes = 64 * 1024;
            for (int seed = 2; seed < 40; seed++)
                Run(new CodeInfo(Filler(60, seed)), [], 50_000, compiledCode: true);
            Assert.That(IlEvm.CachedIlBytes, Is.LessThanOrEqualTo(IlEvmSettings.MaxCachedIlBytes));
            Assert.That(IlEvm.CachedIlBytes, Is.GreaterThan(0));
        }
        finally
        {
            IlEvmSettings.MaxCachedIlBytes = saved;
        }
    }

    /// <summary>A contract the self-check gives back loses its compiled code and runs on, exactly, on the interpreter.</summary>
    [Test]
    public void A_contract_given_back_by_the_self_check_releases_its_code()
    {
        bool savedSelfCheck = IlEvmSettings.SelfCheck;
        double savedRatio = IlEvmSettings.SelfCheckKeepRatio;
        int savedRecheck = IlEvmSettings.SelfCheckRecheckMs;
        int savedDelay = IlEvmSettings.SelfCheckDelayMs;
        try
        {
            IlEvmSettings.SelfCheckDelayMs = 0;
            IlEvmSettings.SelfCheck = true;
            // No compiled code is fast enough to keep at this ratio; the probation ends at once.
            IlEvmSettings.SelfCheckKeepRatio = 1e-6;
            IlEvmSettings.SelfCheckRecheckMs = 0;
            CodeInfo codeInfo = new(Filler(50, 77));
            Outcome expected = Run(codeInfo, [], 100_000, compiledCode: false);
            Run(codeInfo, [], 100_000, compiledCode: true);
            long liveBefore = IlEvmSettings.LiveIlBytes;
            bool sawProbation = false;
            for (int frame = 0; frame < 40_000 && IlEvm.SelfCheckOf(codeInfo) is { Verdict: "undecided" or "probation" } check; frame++)
            {
                sawProbation |= check.Verdict == "probation";
                Assert.That(Run(codeInfo, [], 100_000, compiledCode: true), Is.EqualTo(expected));
            }

            Assert.That(sawProbation, Is.True, "the first verdict against compiled code only puts it on probation");

            Assert.That(IlEvm.SelfCheckOf(codeInfo)?.Verdict, Is.EqualTo("interpreted"));
            Assert.That(IlEvmSettings.LiveIlBytes, Is.LessThan(liveBefore));
            Assert.That(Run(codeInfo, [], 100_000, compiledCode: true), Is.EqualTo(expected));
            // With the self-check off the contract is offered again, and its released chunks hand every frame back.
            IlEvmSettings.SelfCheck = false;
            Assert.That(Run(codeInfo, [], 100_000, compiledCode: true), Is.EqualTo(expected));
        }
        finally
        {
            IlEvmSettings.SelfCheck = savedSelfCheck;
            IlEvmSettings.SelfCheckKeepRatio = savedRatio;
            IlEvmSettings.SelfCheckRecheckMs = savedRecheck;
            IlEvmSettings.SelfCheckDelayMs = savedDelay;
        }
    }
}
