// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The Simple rules: the startup list's contracts run every part compiled, by keccak and by content; code with nothing
/// compiled declines from its slot until the compiled set changes; live promotion takes the codes past the threshold,
/// within the budget, evicting the least used promoted contract for a busier one; override code is promoted by content;
/// Prefer=None counts nothing; block processing's runner runs the startup list alone.
/// </summary>
[NonParallelizable]
public class IlEvmSimpleTests : IlEvmAdaptiveFixture
{
    [SetUp]
    public void SimpleRules()
    {
        IlSimple.ResetForTests();
        IlEvmSettings.Mode = IlMode.Simple;
        IlSimple.Promote = true;
        IlSimple.PromoteOps = 1;
        IlSimple.CompileThreads = 1;
        IlSimple.BudgetBytes = 128L << 20;
        IlSimple.MaxProfiles = 32_768;
        IlSimple.MaxRetainedBytes = 16L << 20;
    }

    [TearDown]
    public void ForgetSimple() => IlSimple.ResetForTests();

    /// <summary>Compiles <paramref name="code"/> for Osaka and publishes it as a startup list entry.</summary>
    private IlEvm.IlContract Startup(byte[] code)
    {
        IlEvm.IlContract contract = Compile(code, Spec);
        IlSimple.PublishStartup([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), contract)]);
        return contract;
    }

    /// <summary>Runs <paramref name="codeInfo"/> on the queries' machine <paramref name="times"/> times, each matching the interpreter.</summary>
    private void Hot(Func<CodeInfo> codeInfo, byte[] code, byte[] input, int times)
    {
        Outcome expected = Interpret(code, input);
        for (int i = 0; i < times; i++)
            Assert.That(Run(QueryVm, codeInfo(), input), Is.EqualTo(expected), $"run {i}");
    }

    private static IlSimpleState? StateOf(CodeInfo codeInfo) => IlEvmFrameRunner.CodeSlot(codeInfo) as IlSimpleState;

    [Test]
    public void Startup_contracts_run_every_part_compiled_found_by_keccak_or_by_content()
    {
        byte[] code = Unique(LoopCode(), 0x41);
        IlEvm.IlContract contract = Startup(code);
        RunCounter runs = CountRuns(contract);
        Outcome expected = Interpret(code, LoopInput);

        CodeInfo stamped = Stamped(code);
        Assert.That(Run(QueryVm, stamped, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.GreaterThan(0), "the queries' runner, by keccak");
        Assert.That(StateOf(stamped)?.Query, Is.SameAs(contract));
        Assert.That(StateOf(stamped)?.Count, Is.Null, "compiled code is not counted");

        int before = runs.Runs;
        CodeInfo overridden = new(code);
        Assert.That(Run(QueryVm, overridden, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.GreaterThan(before), "code no cache stamped, by its bytes");
        Assert.That(StateOf(overridden)?.Query, Is.SameAs(contract));

        // Scope=All: block processing runs the startup list on every frame too.
        before = runs.Runs;
        Assert.That(Run(BlockVm, stamped, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.GreaterThan(before), "the block runner");
        Assert.That(StateOf(stamped)?.Block, Is.SameAs(contract));

        // Every part: a frame that calls out resumes compiled as well, with no gate between.
        Assert.That(IlSimpleThread.Totals().QueryCompiled, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void Code_with_nothing_compiled_declines_from_its_slot_until_the_compiled_set_changes()
    {
        IlSimple.Promote = false;
        byte[] code = Unique(ArithmeticCode(), 0x42);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, ArithmeticInput);
        Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));
        object? mark = IlEvmFrameRunner.CodeSlot(codeInfo);
        Assert.That(mark, Is.SameAs(IlSimple.Nothing), "nothing to run or count: the slot's mark declines");

        long resolved = IlSimple.Resolved;
        for (int i = 0; i < 5; i++)
            Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(mark));
        Assert.That(IlSimple.Resolved, Is.EqualTo(resolved), "declined from the mark, with no lookup");

        // A contract added to the compiled set moves the generation: the marked code resolves again and runs compiled.
        IlEvm.IlContract contract = Startup(code);
        RunCounter runs = CountRuns(contract);
        Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.GreaterThan(0));
        Assert.That(StateOf(codeInfo)?.Query, Is.SameAs(contract));
    }

    [Test]
    public void A_counted_code_runs_compiled_once_promoted()
    {
        byte[] code = Unique(LoopCode(), 0x43);
        CodeInfo codeInfo = Stamped(code);
        Hot(() => codeInfo, code, LoopInput, 64);
        IlSimpleState? state = StateOf(codeInfo);
        Assert.That(state?.Query, Is.Null);
        IlSimpleProfile? profile = IlSimple.ProfileOf(ValueKeccak.Compute(code));
        Assert.That(state?.Count, Is.SameAs(profile).And.Not.Null, "the decline is cached on the state, with the profile it counts into");
        Assert.That((profile!.Parts, profile.Ops), Is.Not.EqualTo((0L, 0L)), "one part in eight sampled, with its opcodes");
        // Drawn at random: 8 on average, and 1 to 24 but for one run in many thousands.
        Assert.That(profile.Parts, Is.InRange(1, 24));
        Assert.That(profile.Bodies, Is.SameAs(Bodies(Spec)));

        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        IlEvm.IlContract? promoted = profile.Promoted;
        Assert.That(promoted, Is.Not.Null);
        Assert.That(promoted!.Bodies, Is.SameAs(Bodies(Spec)), "compiled for the body set its parts ran on");
        RunCounter runs = CountRuns(promoted);
        Hot(() => codeInfo, code, LoopInput, 3);
        Assert.That(runs.Runs, Is.EqualTo(3), "the same code info resolves again at the new generation and runs every part compiled");
        Assert.That(StateOf(codeInfo)?.Query, Is.SameAs(promoted));
        Assert.That(StateOf(codeInfo)?.Uses, Is.SameAs(profile), "its use is sampled for eviction");
        Assert.That(IlSimple.UsedBytes, Is.EqualTo(promoted.MemoryBytes));
        Assert.That(IlSimple.Describe(), Does.Contain("1 promoted").And.Contain("promoted 1 in 1 of 1 rounds"));
    }

    [Test]
    public void Promotion_takes_the_codes_past_the_threshold_scaled_back_from_the_sample()
    {
        IlSimple.PromoteOps = 1_000_000;
        byte[] code = Unique(LoopCode(), 0x44), cold = Unique(LoopCode(), 0x45);
        CodeInfo codeInfo = Stamped(code);
        Hot(() => codeInfo, code, LoopInput, 256);
        Hot(() => Stamped(cold), cold, LoopInput, 4);
        IlSimpleProfile profile = IlSimple.ProfileOf(ValueKeccak.Compute(code))!;
        long ops = profile.Ops;
        Assert.That(ops, Is.GreaterThan(0));

        // Half the threshold over the round's time: not promoted.
        Assert.That(IlSimple.Round(ops * (double)IlSimple.SampleEvery / 500_000), Is.Zero);
        Assert.That((profile.Promoted, profile.Ops, profile.Parts), Is.EqualTo(((IlEvm.IlContract?)null, 0L, 0L)), "a round folds and clears the counts");

        Hot(() => codeInfo, code, LoopInput, 256);
        ops = profile.Ops;
        // Twice it: promoted; the code with too few parts to be sampled much is not.
        Assert.That(IlSimple.Round(ops * (double)IlSimple.SampleEvery / 2_000_000), Is.EqualTo(1));
        Assert.That(profile.Promoted, Is.Not.Null);
        Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(cold))?.Promoted, Is.Null);
        Assert.That(IlSimple.Round(1.0), Is.Zero, "add-only: nothing more to promote, nothing demoted");
        Assert.That(profile.Promoted, Is.Not.Null);
    }

    [Test]
    public void A_full_budget_evicts_the_least_used_promoted_contract_for_a_busier_one_and_never_a_startup_one()
    {
        byte[] a = Unique(LoopCode(), 0x51), b = Unique(LoopCode(), 0x52), c = Unique(LoopCode(), 0x53), shipped = Unique(LoopCode(), 0x54);
        long size = Compile(a, Spec).MemoryBytes;
        IlSimple.BudgetBytes = size * 3 / 2;
        IlEvm.IlContract startup = Startup(shipped);

        CodeInfo aInfo = Stamped(a);
        Hot(() => aInfo, a, LoopInput, 64);
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        IlSimpleProfile aProfile = IlSimple.ProfileOf(ValueKeccak.Compute(a))!;
        IlEvm.IlContract aContract = aProfile.Promoted!;

        // Busier than a, which ran nothing since: a goes, b comes.
        Hot(() => Stamped(b), b, LoopInput, 256);
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        IlSimpleProfile bProfile = IlSimple.ProfileOf(ValueKeccak.Compute(b))!;
        Assert.That(bProfile.Promoted, Is.Not.Null);
        Assert.That(aProfile.Promoted, Is.Null, "evicted");
        Assert.That(aContract.IsReleased && aContract.TablesReleased, Is.True, "its code released, tables and all");
        Assert.That(IlSimple.Totals.Evicted, Is.EqualTo(1));
        Assert.That(IlSimple.UsedBytes, Is.EqualTo(bProfile.Promoted!.MemoryBytes));

        // Less used than b: turned away, b stays. Run until a part of it is drawn for the sample, so it is a candidate.
        Hot(() => Stamped(c), c, LoopInput, 16);
        while (IlSimple.ProfileOf(ValueKeccak.Compute(c))!.Ops == 0)
            Hot(() => Stamped(c), c, LoopInput, 1);
        Assert.That(IlSimple.Round(1.0), Is.Zero);
        Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(c))!.Promoted, Is.Null);
        Assert.That(bProfile.Promoted, Is.Not.Null);
        Assert.That(IlSimple.Totals.Rejected, Is.EqualTo(1));

        // The evicted code runs on the interpreter again, and counts again; the startup contract outside the budget stays.
        Hot(() => aInfo, a, LoopInput, 16);
        Assert.That(StateOf(aInfo)?.Query, Is.Null);
        Assert.That(StateOf(aInfo)?.Count, Is.SameAs(aProfile));
        Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(shipped))?.Startup, Is.SameAs(startup));
        Assert.That(startup.IsReleased, Is.False);
    }

    [Test]
    public void Override_code_is_counted_and_promoted_by_its_bytes_with_no_hash()
    {
        byte[] code = Unique(LoopCode(), 0x61);
        long contentHashes = IlRuntime.ContentHashes;
        // A new code info on every call, as a state override's.
        Hot(() => new CodeInfo(code), code, LoopInput, 64);
        IlSimpleProfile? profile = IlSimple.ProfileOf(code);
        Assert.That(profile, Is.Not.Null);
        Assert.That(profile!.ByContent, Is.True);
        Assert.That(profile.Bytes.ToArray(), Is.EqualTo(code), "the bytes are kept with the profile");
        Assert.That(IlSimple.ProfileCount, Is.EqualTo(1), "one profile for every code info of the code");

        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        RunCounter runs = CountRuns(profile.Promoted!);
        CodeInfo next = new(code);
        Hot(() => next, code, LoopInput, 2);
        Assert.That(runs.Runs, Is.EqualTo(2), "a later call's code attaches by its bytes");
        Assert.That(StateOf(next)?.Query, Is.SameAs(profile.Promoted));
        Assert.That(IlRuntime.ContentHashes, Is.EqualTo(contentHashes), "nothing hashed");

        // The same bytes from the code database find it too.
        CodeInfo stamped = Stamped(code);
        Hot(() => stamped, code, LoopInput, 1);
        Assert.That(StateOf(stamped)?.Query, Is.SameAs(profile.Promoted));

        // Other bytes of the same length are another code.
        byte[] other = Unique(LoopCode(), 0x62);
        Hot(() => new CodeInfo(other), other, LoopInput, 8);
        Assert.That(IlSimple.ProfileOf(other), Is.Not.SameAs(profile).And.Not.Null);
    }

    [Test]
    public void Prefer_none_counts_nothing_and_promotes_nothing()
    {
        IlSimple.Promote = false;
        byte[] code = Unique(LoopCode(), 0x71);
        CodeInfo codeInfo = Stamped(code);
        Hot(() => codeInfo, code, LoopInput, 64);
        Hot(() => new CodeInfo(code), code, LoopInput, 16);
        Assert.That(IlSimple.ProfileCount, Is.Zero);
        Assert.That(IlEvmFrameRunner.CodeSlot(codeInfo), Is.SameAs(IlSimple.Nothing));
        Assert.That(IlSimple.Round(1.0), Is.Zero);
    }

    [Test]
    public void Prefer_blocks_counts_as_none_under_the_simple_rules()
    {
        using IlEvmSettingsScope settings = new();
        IlEvmModule.Configure(new IlEvmConfig { Enabled = true, Selection = IlEvmSelection.Simple, Prefer = IlEvmPrefer.Blocks, ListSize = 7, PromoteMs = 1234, PromoteOps = 99, CompileThreads = 2, LiveBudgetMiB = 3 });
        Assert.That((IlEvmSettings.Mode, IlSimple.Promote, IlEvmSettings.Scope, IlEvmSettings.ScopeForcedToAll), Is.EqualTo((IlMode.Simple, false, IlEvmScope.Rpc, false)));
        Assert.That((IlSimple.PromoteMs, IlSimple.PromoteOps, IlSimple.CompileThreads, IlSimple.BudgetBytes), Is.EqualTo((1234, 99L, 2, 3L << 20)));
        Assert.That(IlRuntime.QueryCounting, Is.False, "the Adaptive rules' counting stays off");
        IlEvmModule.Configure(new IlEvmConfig { Enabled = true, Selection = IlEvmSelection.Simple });
        Assert.That((IlEvmSettings.Mode, IlSimple.Promote, IlSimple.PromoteMs, IlSimple.PromoteOps), Is.EqualTo((IlMode.Simple, true, 5_000, 1_000_000L)), "the Simple defaults");
    }

    [Test]
    public void Block_processing_runs_the_startup_list_alone_and_feeds_no_promotion()
    {
        byte[] code = Unique(LoopCode(), 0x81), blockOnly = Unique(LoopCode(), 0x82);
        CodeInfo codeInfo = Stamped(code);
        Hot(() => codeInfo, code, LoopInput, 64);
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        IlEvm.IlContract promoted = IlSimple.ProfileOf(ValueKeccak.Compute(code))!.Promoted!;
        RunCounter runs = CountRuns(promoted);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(Run(BlockVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.Zero, "a promoted contract runs on the queries' runner only");

        // Code block processing meets first gets no profile, and a mark the queries' runner resolves past.
        CodeInfo blockInfo = Stamped(blockOnly);
        for (int i = 0; i < 64; i++)
            Assert.That(Run(BlockVm, blockInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(blockOnly)), Is.Null);
        Assert.That(IlEvmFrameRunner.CodeSlot(blockInfo), Is.SameAs(IlSimple.NothingBlock));
        Assert.That(Run(QueryVm, blockInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(StateOf(blockInfo)?.Count, Is.Not.Null, "the queries' runner counts it");
    }

    [Test]
    public void Initcode_is_never_counted_or_compiled()
    {
        byte[] init = InitCode();
        Outcome expected = Interpret(init, [], ExecutionType.CREATE);
        for (int i = 0; i < 32; i++)
            Assert.That(Run(QueryVm, new CodeInfo(init), [], type: ExecutionType.CREATE), Is.EqualTo(expected));
        Assert.That(IlSimple.ProfileCount, Is.Zero);
    }

    [Test]
    public void A_fork_with_another_body_set_runs_on_the_interpreter_and_is_promoted_for_it()
    {
        byte[] code = Unique(LoopCode(), 0x91);
        IlEvm.IlContract startup = Startup(code);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput, vm: PragueQueryVm);
        for (int i = 0; i < 64; i++)
            Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        IlSimpleProfile profile = IlSimple.ProfileOf(ValueKeccak.Compute(code))!;
        Assert.That(profile.Startup, Is.SameAs(startup));
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1), "Prague's parts were counted");
        Assert.That(profile.Promoted!.Bodies, Is.SameAs(Bodies(Nethermind.Specs.Forks.Prague.Instance)));
        RunCounter runs = CountRuns(profile.Promoted);
        Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
    }

    [Test]
    public void The_compiled_path_counts_parts_per_thread_for_the_exit_line()
    {
        byte[] code = Unique(LoopCode(), 0xa1), plain = Unique(ArithmeticCode(), 0xa2);
        Startup(code);
        (long compiled, long interpreted, _, _) = IlSimpleThread.Totals();
        Hot(() => Stamped(code), code, LoopInput, 5);
        Hot(() => Stamped(plain), plain, ArithmeticInput, 7);
        (long compiledNow, long interpretedNow, _, _) = IlSimpleThread.Totals();
        Assert.That((compiledNow - compiled, interpretedNow - interpreted), Is.EqualTo((5L, 7L)));
        Assert.That(IlSimple.Describe(), Does.Contain("parts compiled/interpreted: queries ").And.Contain("1 startup"));
    }

    [Test]
    public void Codes_that_alternate_on_one_thread_are_both_sampled()
    {
        // A fixed one-in-eight counter would take the same position of the pattern every time, and one code never.
        byte[] a = Unique(LoopCode(), 0xb1), b = Unique(LoopCode(), 0xb2);
        CodeInfo aInfo = Stamped(a), bInfo = Stamped(b);
        Outcome aExpected = Interpret(a, LoopInput), bExpected = Interpret(b, LoopInput);
        for (int i = 0; i < 128; i++)
        {
            Assert.That(Run(QueryVm, aInfo, LoopInput), Is.EqualTo(aExpected));
            Assert.That(Run(QueryVm, bInfo, LoopInput), Is.EqualTo(bExpected));
        }

        IlSimpleProfile aProfile = IlSimple.ProfileOf(ValueKeccak.Compute(a))!, bProfile = IlSimple.ProfileOf(ValueKeccak.Compute(b))!;
        Assert.That(aProfile.Parts, Is.InRange(3, 40), "16 on average");
        Assert.That(bProfile.Parts, Is.InRange(3, 40), "16 on average");

        // The compiled parts' sample, which eviction ranks by, is drawn the same way.
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(2));
        for (int i = 0; i < 128; i++)
        {
            Assert.That(Run(QueryVm, aInfo, LoopInput), Is.EqualTo(aExpected));
            Assert.That(Run(QueryVm, bInfo, LoopInput), Is.EqualTo(bExpected));
        }

        Assert.That(aProfile.CompiledParts, Is.InRange(3, 40));
        Assert.That(bProfile.CompiledParts, Is.InRange(3, 40));
    }

    [Test]
    public void A_code_promoted_for_one_fork_is_promoted_again_for_the_next_its_old_contract_replaced()
    {
        byte[] code = Unique(LoopCode(), 0xb3);
        CodeInfo codeInfo = Stamped(code);
        Outcome prague = Interpret(code, LoopInput, vm: PragueQueryVm);
        for (int i = 0; i < 64; i++)
            Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(prague));
        IlSimpleProfile profile = IlSimple.ProfileOf(ValueKeccak.Compute(code))!;
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        IlEvm.IlContract old = profile.Promoted!;
        Assert.That(old.Bodies, Is.SameAs(Bodies(Nethermind.Specs.Forks.Prague.Instance)));

        // The fork: the same code info's parts now run Osaka's bodies, interpreted and counted.
        Hot(() => codeInfo, code, LoopInput, 64);
        Assert.That(StateOf(codeInfo)?.Count, Is.SameAs(profile));
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1), "promoted again for the new body set");
        IlEvm.IlContract current = profile.Promoted!;
        Assert.That(current.Bodies, Is.SameAs(Bodies(Spec)));
        Assert.That(old.IsReleased && old.TablesReleased, Is.True, "the old fork's contract is released, tables and all");
        Assert.That((IlSimple.PromotedCount, IlSimple.UsedBytes, IlSimple.Totals.Evicted), Is.EqualTo((1, current.MemoryBytes, 0)), "replaced in the budget, not evicted");
        RunCounter runs = CountRuns(current);
        Hot(() => codeInfo, code, LoopInput, 3);
        Assert.That(runs.Runs, Is.EqualTo(3));
    }

    [Test]
    public void A_startup_contract_runs_compiled_after_a_call_at_an_older_fork_resolved_its_state()
    {
        byte[] code = Unique(LoopCode(), 0xb4);
        IlEvm.IlContract contract = Startup(code);
        RunCounter runs = CountRuns(contract);
        CodeInfo codeInfo = Stamped(code);
        // An eth_call at a block of the older fork first: interpreted, and counted for that fork.
        Assert.That(Run(PragueQueryVm, codeInfo, LoopInput), Is.EqualTo(Interpret(code, LoopInput, vm: PragueQueryVm)));
        Assert.That(runs.Runs, Is.Zero);
        Assert.That(StateOf(codeInfo)?.Bodies, Is.SameAs(Bodies(Nethermind.Specs.Forks.Prague.Instance)));

        // Calls at the head's fork resolve the state for their bodies and run the startup contract, with no generation change.
        int generation = IlSimple.Generation;
        Hot(() => codeInfo, code, LoopInput, 8);
        Assert.That(runs.Runs, Is.EqualTo(8));
        Assert.That(StateOf(codeInfo)?.Query, Is.SameAs(contract));
        Assert.That(IlSimple.Generation, Is.EqualTo(generation));

        // And back: the older fork's parts are counted again.
        for (int i = 0; i < 64; i++)
            Run(PragueQueryVm, codeInfo, LoopInput);
        Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(code))!.Parts, Is.GreaterThan(0));
    }

    [Test]
    public void Idle_override_profiles_are_pruned_so_a_new_hot_override_of_their_length_is_promoted()
    {
        IlSimple.PromoteOps = long.MaxValue;
        for (int k = 0; k < IlSimple.MaxPerLength; k++)
        {
            byte[] once = Unique(LoopCode(), (byte)(0xc0 + k));
            Run(QueryVm, new CodeInfo(once), LoopInput);
        }

        Assert.That(IlSimple.ProfileCount, Is.EqualTo(IlSimple.MaxPerLength));
        Assert.That(IlSimple.RetainedBytes, Is.EqualTo(IlSimple.MaxPerLength * (long)Unique(LoopCode(), 0).Length));

        // Their length is full: a new override code of it gets no profile, refused without the lock.
        byte[] hot = Unique(LoopCode(), 0xee);
        Hot(() => new CodeInfo(hot), hot, LoopInput, 4);
        Assert.That(IlSimple.ProfileOf(hot), Is.Null);

        // Idle rounds: the refusal has the idle ones pruned, their bytes given back; one whose single part was drawn
        // for the sample is idle a round later.
        for (int r = 0; r < IlSimple.IdleToPrune; r++)
            IlSimple.Round(1.0);
        Assert.That(IlSimple.ProfileCount, Is.LessThan(IlSimple.MaxPerLength));
        Assert.That(IlSimple.RetainedBytes, Is.EqualTo(IlSimple.ProfileCount * (long)hot.Length));

        IlSimple.PromoteOps = 1;
        Hot(() => new CodeInfo(hot), hot, LoopInput, 64);
        Assert.That(IlSimple.ProfileOf(hot), Is.Not.Null);
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        Assert.That(IlSimple.ProfileOf(hot)!.Promoted, Is.Not.Null);
    }

    [Test]
    public void The_hard_cap_is_checked_before_anything_is_evicted()
    {
        long savedMax = IlEvmSettings.MaxLiveIlBytes;
        try
        {
            byte[] a = Unique(LoopCode(), 0xd1);
            byte[] b = [.. LoopCode(), (byte)Instruction.INVALID, .. Enumerable.Repeat((byte)0x5b, 600), 0xd2];
            long sizeA = Compile(a, Spec).MemoryBytes, sizeB = Compile(b, Spec).MemoryBytes;
            Assert.That(sizeB, Is.GreaterThan(sizeA));
            IlSimple.BudgetBytes = sizeB;

            CodeInfo aInfo = Stamped(a);
            Hot(() => aInfo, a, LoopInput, 64);
            Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
            IlSimpleProfile aProfile = IlSimple.ProfileOf(ValueKeccak.Compute(a))!;
            IlEvm.IlContract aContract = aProfile.Promoted!;
            // Room for a's contract and half the difference: b would fit the budget once a went, but not the hard cap.
            IlEvmSettings.MaxLiveIlBytes = IlEvmSettings.LiveIlBytes + (sizeB - sizeA) / 2;

            Hot(() => Stamped(b), b, LoopInput, 256);
            Assert.That(IlSimple.Round(1.0), Is.Zero);
            Assert.That(aProfile.Promoted, Is.SameAs(aContract), "a stays: nothing is evicted for a newcomer turned away");
            Assert.That(aContract.IsReleased, Is.False);
            Assert.That((IlSimple.Totals.Evicted, IlSimple.Totals.Rejected), Is.EqualTo((0, 1)));
            Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(b))!.RejectedBytes, Is.EqualTo(sizeB));
        }
        finally
        {
            IlEvmSettings.MaxLiveIlBytes = savedMax;
        }
    }

    [Test]
    public void Code_from_the_database_and_override_code_of_the_same_bytes_share_one_contract()
    {
        byte[] code = Unique(LoopCode(), 0xf1);
        CodeInfo stamped = Stamped(code);
        // Counted under both profiles before either is promoted.
        Hot(() => stamped, code, LoopInput, 64);
        Hot(() => new CodeInfo(code), code, LoopInput, 64);
        IlSimpleProfile byHash = IlSimple.ProfileOf(ValueKeccak.Compute(code))!;
        Assert.That(IlSimple.ProfileOf(code), Is.Not.SameAs(byHash), "a content profile of its own until then");

        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1), "compiled once");
        IlEvm.IlContract contract = byHash.Promoted!;
        Assert.That(IlSimple.ProfileOf(code), Is.SameAs(byHash), "the content profile is retired for the stamped one");
        Assert.That(IlSimple.UsedBytes, Is.EqualTo(contract.MemoryBytes));
        RunCounter runs = CountRuns(contract);
        CodeInfo overridden = new(code);
        Hot(() => overridden, code, LoopInput, 2);
        Hot(() => stamped, code, LoopInput, 2);
        Assert.That(runs.Runs, Is.EqualTo(4));
        Assert.That(StateOf(overridden)?.Query, Is.SameAs(contract));
        Assert.That(IlSimple.Round(1.0), Is.Zero);

        // Stamped code met after its bytes were promoted as override code runs that contract too, and is not counted.
        byte[] other = Unique(LoopCode(), 0xf2);
        Hot(() => new CodeInfo(other), other, LoopInput, 64);
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        IlEvm.IlContract otherContract = IlSimple.ProfileOf(other)!.Promoted!;
        CodeInfo otherStamped = Stamped(other);
        Hot(() => otherStamped, other, LoopInput, 64);
        Assert.That(StateOf(otherStamped)?.Query, Is.SameAs(otherContract));
        Assert.That(IlSimple.Round(1.0), Is.Zero);
        Assert.That(IlSimple.PromotedCount, Is.EqualTo(2));
    }

    [Test]
    public void A_startup_entry_found_later_takes_over_from_a_contract_promoted_for_the_same_code()
    {
        byte[] code = Unique(LoopCode(), 0xf3), overridden = Unique(LoopCode(), 0xf4);
        CodeInfo codeInfo = Stamped(code);
        Hot(() => codeInfo, code, LoopInput, 64);
        Hot(() => new CodeInfo(overridden), overridden, LoopInput, 64);
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(2));
        IlEvm.IlContract promoted = IlSimple.ProfileOf(ValueKeccak.Compute(code))!.Promoted!;
        IlEvm.IlContract promotedByContent = IlSimple.ProfileOf(overridden)!.Promoted!;

        // The retry of the entries the code database lacked publishes both: the promoted contracts give their bytes back.
        IlEvm.IlContract startup = Startup(code), startupOverridden = Startup(overridden);
        Assert.That((promoted.IsReleased, promotedByContent.IsReleased), Is.EqualTo((true, true)));
        Assert.That((IlSimple.PromotedCount, IlSimple.UsedBytes), Is.EqualTo((0, 0L)));
        Assert.That(IlSimple.ProfileOf(overridden), Is.SameAs(IlSimple.ProfileOf(ValueKeccak.Compute(overridden))), "one profile for the bytes");
        CodeInfo next = new(overridden);
        Hot(() => next, overridden, LoopInput, 1);
        Hot(() => codeInfo, code, LoopInput, 1);
        Assert.That(StateOf(next)?.Query, Is.SameAs(startupOverridden));
        Assert.That(StateOf(codeInfo)?.Query, Is.SameAs(startup));
    }

    [Test]
    public void A_candidate_the_budget_turned_away_is_not_compiled_again_while_it_still_could_not_be_admitted()
    {
        byte[] a = Unique(LoopCode(), 0xd3), c = Unique(LoopCode(), 0xd4);
        IlSimple.BudgetBytes = Compile(a, Spec).MemoryBytes * 3 / 2;
        CodeInfo aInfo = Stamped(a), cInfo = Stamped(c);
        Hot(() => aInfo, a, LoopInput, 64);
        Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
        IlSimpleProfile aProfile = IlSimple.ProfileOf(ValueKeccak.Compute(a))!;

        // a stays busier than c: c is compiled once, turned away, and then only checked.
        aProfile.Usage = 1e12;
        Hot(() => cInfo, c, LoopInput, 64);
        int compiles = IlSimple.Compiles;
        Assert.That(IlSimple.Round(1.0), Is.Zero);
        Assert.That((IlSimple.Compiles - compiles, IlSimple.Totals.Rejected), Is.EqualTo((1, 1)));
        for (int r = 0; r < 2 * IlSimple.BudgetRetryRounds; r++)
        {
            aProfile.Usage = 1e12;
            Hot(() => cInfo, c, LoopInput, 16);
            Assert.That(IlSimple.Round(1.0), Is.Zero);
        }

        Assert.That(IlSimple.Compiles - compiles, Is.EqualTo(1), "the retries are checked against the size it had, not compiled");
        Assert.That(IlSimple.Totals.Rejected, Is.GreaterThanOrEqualTo(2));

        // Once a is idle, c's next try compiles it and evicts a.
        IlSimpleProfile cProfile = IlSimple.ProfileOf(ValueKeccak.Compute(c))!;
        for (int r = 0; r <= IlSimple.BudgetRetryRounds && cProfile.Promoted is null; r++)
        {
            aProfile.Usage = 0;
            Hot(() => cInfo, c, LoopInput, 64);
            IlSimple.Round(1.0);
        }

        Assert.That(cProfile.Promoted, Is.Not.Null);
        Assert.That(aProfile.Promoted, Is.Null);
    }

    private sealed class ReleasingRunner(IlEvm.IlRunner inner, IlEvm.IlContract contract)
    {
        public int Calls;

        public EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas,
            scoped ref nint programCounter, scoped ref IlEvm.IlRunState state)
        {
            if (Calls++ == 0)
                contract.Release(dropTables: true);
            return inner(vm, ref stack, ref gas, ref programCounter, ref state);
        }
    }

    [Test]
    public void An_evicted_contract_drops_its_tables_and_a_frame_running_it_hands_back_cleanly()
    {
        int saved = IlEvmSettings.ChunkInstructions;
        IlEvmSettings.ChunkInstructions = 6;
        try
        {
            byte[] code = Unique(LoopCode(), 0xc1);
            CodeInfo codeInfo = Stamped(code);
            Hot(() => codeInfo, code, LoopInput, 64);
            Assert.That(IlSimple.Round(1.0), Is.EqualTo(1));
            IlEvm.IlContract promoted = IlSimple.ProfileOf(ValueKeccak.Compute(code))!.Promoted!;
            Assert.That(promoted.ChunkCount, Is.GreaterThan(1));
            ReleasingRunner releasing = new(promoted.Runners[0]!, promoted);
            promoted.Runners[0] = releasing.Run;
            Outcome expected = Interpret(code, LoopInput);
            Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected), "released, tables and all, while its first chunk runs");
            Assert.That(releasing.Calls, Is.EqualTo(1));
            Assert.That(promoted.TablesReleased, Is.True);
            // The state still names it until the generation moves: its parts hand back on entry.
            Assert.That(StateOf(codeInfo)?.Query, Is.SameAs(promoted));
            for (int i = 0; i < 4; i++)
                Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        }
        finally
        {
            IlEvmSettings.ChunkInstructions = saved;
        }
    }

    [Test]
    public void Override_codes_of_one_length_log_under_distinct_names()
    {
        byte[] a = Unique(LoopCode(), 0xe1), b = Unique(LoopCode(), 0xe2);
        Hot(() => new CodeInfo(a), a, LoopInput, 1);
        Hot(() => new CodeInfo(b), b, LoopInput, 1);
        string aName = IlSimple.ProfileOf(a)!.ShortName, bName = IlSimple.ProfileOf(b)!.ShortName;
        Assert.That(aName, Does.StartWith($"c{a.Length}-"));
        Assert.That(aName, Is.Not.EqualTo(bName));
    }
}
