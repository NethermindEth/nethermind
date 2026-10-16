// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The startup compile under the Adaptive rules: the list of either format with its calibration lines, compiled at every
/// startup and at genesis for the spec of the wall clock, its deadline, the entries it leaves, and the module's choice of
/// rules and runners.
/// </summary>
[NonParallelizable]
public class IlEvmStartupTests : IlEvmAdaptiveFixture
{
    private static string Hash(char digit) => "0x" + new string(digit, 64);

    [Test]
    public void List_v2_parse()
    {
        IlEvmPrecompileList.ListFile list = IlEvmPrecompileList.ParseList([
            "# il-evm list v2 runs=38462642089 N=10000 train=1-5000 rho=0.25 lambda=0.003",
            "# calib a.line=0.25 a.mixed=0.6 a.loop=1.1 g.M=25",
            $"{Hash('a')} score=3712.5 sM=3000 sO=2850 sigma=120.5 size=24576 n=3.5 w=1091 f=0.62 L=loop use=mw minv=11 src=measured",
            $"{Hash('b')} score=1e3 use=m L=Line",
            $"{Hash('c')} score=-5 use=- L=mixed minv=0",
            // Neither is a calibration line: the prefix is a word of its own.
            "#calib x=1",
            "# calibration y=2",
            "# calib gate.block.cold=2048 g.M=30",
            $"{Hash('d')} L=spiral use=q minv=64",
            $"2026-10-13 12:00:00.0000|IL-EVM profile 2 {Hash('e')} ops=50 frames=10 size=10 cum=0.5000",
            // The RPC list's fields: the queries' runner's threshold and the code itself; code that is not hex, or an odd
            // number of digits, is none.
            $"{Hash('f')} uses=37681 size=5 use=- qminv=0 code=0x6001600055",
            $"{Hash('1')} qminv=13 code=6001600055",
            $"{Hash('2')} qminv=64 code=0x60z1",
            $"{Hash('3')} code=0x600",
        ]);
        Assert.That(list.Entries, Is.EqualTo(new IlEvmPrecompileList.Entry[]
        {
            new(new ValueHash256(Hash('a')), Size: 24576, Score: 3712.5, Use: IlEvmUse.Main | IlEvmUse.Workers, MinVisitLog2: 11, Shape: IlEvmCodeShape.Loop),
            new(new ValueHash256(Hash('b')), Score: 1000, Use: IlEvmUse.Main, Shape: IlEvmCodeShape.Line),
            new(new ValueHash256(Hash('c')), Score: -5, Use: IlEvmUse.None, Shape: IlEvmCodeShape.Mixed),
            new(new ValueHash256(Hash('d')), Use: IlEvmUse.None),
            new(new ValueHash256(Hash('e')), 50, 10, 10),
            new(new ValueHash256(Hash('f')), Size: 5, Use: IlEvmUse.None, QueryMinVisitLog2: 0),
            new(new ValueHash256(Hash('1')), QueryMinVisitLog2: 13),
            new(new ValueHash256(Hash('2'))),
            new(new ValueHash256(Hash('3'))),
        }).Using<IlEvmPrecompileList.Entry>(static (a, b) => a with { Code = null } == b with { Code = null }));
        Assert.That(list.Entries.Select(static e => e.Code?.ToHexString(true)), Is.EqualTo(new[] { null, null, null, null, null, "0x6001600055", "0x6001600055", null, null }));
        Assert.That(list.Calibration, Is.EqualTo(new[] { "a.line=0.25 a.mixed=0.6 a.loop=1.1 g.M=25", "gate.block.cold=2048 g.M=30" }));
        Dictionary<string, string> values = IlEvmPrecompileList.CalibrationValues(list.Calibration);
        Assert.That((values["a.loop"], values["gate.block.cold"]), Is.EqualTo(("1.1", "2048")));
        Assert.That(values["g.M"], Is.EqualTo("30"), "the last line wins");
        Assert.That(values.ContainsKey("x") || values.ContainsKey("y"), Is.False);
        Assert.That(IlEvmPrecompileList.ParseList(["# calib only=1"]).Entries, Is.Empty);

        // The shipped list, of either format: each entry with its code's size and the profile's counts, or with the fields
        // tools/make_list.py measures; at most the tool's 500 entries, and calibration values that are numbers.
        Assume.That(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST"), Is.Null.Or.Empty, "an override replaces the lists");
        IlEvmPrecompileList.ListFile shipped = IlEvmPrecompileList.LoadList(IlEvmPrecompileSource.Shipped, out string origin);
        Assert.That(origin, Is.EqualTo("Shipped"));
        Assert.That(shipped.Entries, Has.Count.InRange(1, 500));
        Assert.That(shipped.Entries.All(static e => e.Size > 0 && (e is { Ops: > 0, Frames: > 0, Score: null } || e is { Score: not null, Use: not null, Shape: not null })), Is.True);
        Assert.That(IlEvmPrecompileList.CalibrationValues(shipped.Calibration).Values.All(static v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)), Is.True);
        Assert.That(shipped.Entries.Select(static e => e.Hash), Is.EqualTo(IlEvmPrecompileList.Load()));
    }

    [Test]
    public void List_entries_carry_their_fields_into_the_shipped_profiles()
    {
        byte[] code = Unique(LoopCode(), 0x21);
        IlEvm.IlContract contract = Ship(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code), Score: 12.5, Use: IlEvmUse.Main, MinVisitLog2: 10, Shape: IlEvmCodeShape.Loop), code);
        IlCodeProfile profile = contract.Profile!;
        Assert.That((profile.ListScore, profile.Shape, profile.BlockMinVisitLog2, profile.UseMask), Is.EqualTo(((double?)12.5, (IlEvmCodeShape?)IlEvmCodeShape.Loop, (byte)10, (byte)(IlRuntime.RunMain | IlRuntime.RunQuery))));
        Assert.That(contract.RunMask, Is.EqualTo(IlRuntime.RunMain | IlRuntime.RunQuery));
        // An entry of the format before runs everywhere, as before.
        byte[] old = Unique(LoopCode(), 0x22);
        IlEvm.IlContract plain = Ship(old)[0];
        Assert.That((plain.Profile!.UseMask, plain.Profile.BlockMinVisitLog2, plain.RunMask), Is.EqualTo((IlRuntime.RunAll, (byte)0, IlRuntime.RunAll)));
    }

    /// <summary>A startup step on <paramref name="codes"/> in the code database and a list file of <paramref name="list"/>, run to its end; the step's log lines.</summary>
    private List<string> Startup(IlEvmConfig config, IEnumerable<byte[]> codes, IEnumerable<string> list, ulong headNumber, out ForkActivation activation,
        IBlocksConfig? blocksConfig = null)
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        foreach (byte[] code in codes)
        {
            ValueHash256 hash = ValueKeccak.Compute(code);
            dbProvider.CodeDb[hash.Bytes] = code;
        }

        _codeDb = dbProvider.CodeDb;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(headNumber).WithTimestamp(1_700_000_000).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        ForkActivation seen = default;
        specProvider.GetSpec(Arg.Do<ForkActivation>(a => seen = a)).Returns(Spec);

        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"ilevm-startup-list-{Environment.ProcessId}.txt");
        File.WriteAllLines(path, list);
        string? saved = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST");
        Environment.SetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST", path);
        Nethermind.Core.Test.TestLogger logger = new();
        // Block processing, whose branches the Adaptive rules' scheduler waits for; none come in these tests.
        IMainProcessingContext context = Substitute.For<IMainProcessingContext>();
        context.BranchProcessor.Returns(Substitute.For<IBranchProcessor>());
        try
        {
            using CancellationTokenSource exit = new();
            IlEvmStartupStep step = new(config, dbProvider, blockTree, specProvider, new Lazy<IMainProcessingContext>(() => context),
                blocksConfig ?? new BlocksConfig(), new ExitSource(exit.Token), new OneLogger(logger));
            step.Execute(default).Wait();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST", saved);
            File.Delete(path);
        }

        activation = seen;
        return logger.LogList;
    }

    private IDb? _codeDb;

    /// <summary>
    /// The Simple rules compile the list's first ListSize entries at startup, at genesis too, publish them to the Simple
    /// lookup, and compile no RPC list; a missing entry compiles once its code appears; Prefer=Blocks is logged as None.
    /// </summary>
    [Test]
    public void Simple_startup_compiles_the_first_list_size_entries()
    {
        IlSimple.ResetForTests();
        IlEvmSettings.Mode = IlMode.Simple;
        try
        {
            byte[] first = Unique(LoopCode(), 0x26), second = Unique(ArithmeticCode(), 0x27), late = Unique(LoopCode(), 0x28), third = Unique(LoopCode(), 0x29);
            string[] list = [$"{ValueKeccak.Compute(first)}", $"{ValueKeccak.Compute(late)}", $"{ValueKeccak.Compute(second)}", $"{ValueKeccak.Compute(third)}"];
            IlEvmConfig config = new() { Enabled = true, ListSize = 3, Prefer = IlEvmPrefer.Blocks };
            List<string> lines = Startup(config, [first, second, third], list, headNumber: 0, out _);
            string compiled = lines.Single(static l => l.StartsWith("IL-EVM precompile:", StringComparison.Ordinal));
            Assert.That(compiled, Does.StartWith("IL-EVM precompile: 2 of 3 shipped contracts compiled for the spec of the wall clock, "));
            Assert.That(compiled, Does.Contain("the first 3 of 4 entries (ListSize=3)").And.Not.Contain("RPC list").And.Not.Contain("deadline"));
            Assert.That(lines, Has.One.Contains("Prefer=Blocks counts as None"));
            Assert.That((IlSimple.StartupCount, IlShippedBacklog.Compiled, IlShippedBacklog.MissingCount), Is.EqualTo((2, 2, 1)));
            Assert.That(IlRuntime.ProfileCount, Is.Zero, "nothing published to the Adaptive rules");

            CodeInfo codeInfo = Stamped(first);
            Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(Interpret(first, LoopInput)));
            Assert.That((IlEvmFrameRunner.CodeSlot(codeInfo) as IlSimpleState)?.Query, Is.Not.Null, "compiled at startup");
            Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(third)), Is.Null, "past ListSize");

            _codeDb![ValueKeccak.Compute(late).Bytes] = late;
            Assert.That(IlShippedBacklog.RetryMissing(), Is.EqualTo(1));
            Assert.That(IlSimple.ProfileOf(ValueKeccak.Compute(late))?.Startup, Is.Not.Null, "a retried entry joins the Simple rules' startup set");
        }
        finally
        {
            IlSimple.ResetForTests();
        }
    }

    [Test]
    public void Always_load()
    {
        byte[] first = Unique(LoopCode(), 0x23), second = Unique(ArithmeticCode(), 0x24), late = Unique(LoopCode(), 0x25);
        string[] list = [$"{ValueKeccak.Compute(first)} score=10", $"{ValueKeccak.Compute(late)}", $"{ValueKeccak.Compute(second)} use=m"];

        // Scope=Rpc and Prefer=None on a node at genesis: the list compiles all the same, for the spec of the wall clock.
        IlEvmConfig config = new() { Enabled = true, Selection = IlEvmSelection.Adaptive, Scope = IlEvmScope.Rpc, Prefer = IlEvmPrefer.None };
        ulong before = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<string> lines = Startup(config, [first, second], list, headNumber: 0, out ForkActivation activation);
        ulong after = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.That(activation.BlockNumber, Is.EqualTo(ulong.MaxValue), "past every block-numbered fork");
        Assert.That(activation.Timestamp, Is.InRange(before, after), "the wall clock's");
        string compiled = lines.Single(static l => l.StartsWith("IL-EVM precompile:", StringComparison.Ordinal));
        Assert.That(compiled, Does.StartWith("IL-EVM precompile: 2 of 3 shipped contracts compiled for the spec of the wall clock, "));
        Assert.That(compiled, Does.Contain("; 1 not in the code database, 0 declined, 0 past the ").And.Contain(", 0 past the 60 s deadline, left for the windows between blocks)"));
        Assert.That((IlShippedBacklog.Compiled, IlShippedBacklog.MissingCount, IlShippedBacklog.LeftoverCount), Is.EqualTo((2, 1, 0)));
        Assert.That(IlShippedBacklog.Missing().Single().Hash, Is.EqualTo(ValueKeccak.Compute(late)));
        Assert.That(IlShippedBacklog.Bodies, Is.SameAs(Bodies(Spec)));

        foreach ((byte[] code, byte[] input) in new[] { (first, LoopInput), (second, ArithmeticInput) })
        {
            CodeInfo codeInfo = Stamped(code);
            Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(Interpret(code, input)));
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(Interpret(code, input)));
            Assert.That(StateOf(codeInfo).QueryRun, Is.Not.Null, "compiled at startup");
            Assert.That(StateOf(codeInfo).BlockRun, Is.SameAs(StateOf(codeInfo).QueryRun));
        }

        // The entry missing at startup, met first on the interpreter, compiles once its code appears, and its code runs compiled.
        CodeInfo lateInfo = Stamped(late);
        Assert.That(Run(QueryVm, lateInfo, LoopInput), Is.EqualTo(Interpret(late, LoopInput)));
        Assert.That(IlEvmFrameRunner.CodeSlot(lateInfo), Is.SameAs(IlRuntime.QuietAll));
        Assert.That(IlShippedBacklog.RetryMissing(), Is.EqualTo(0), "still missing");
        ValueHash256 lateHash = ValueKeccak.Compute(late);
        _codeDb![lateHash.Bytes] = late;
        Assert.That(IlShippedBacklog.RetryMissing(), Is.EqualTo(1));
        Assert.That((IlShippedBacklog.Compiled, IlShippedBacklog.MissingCount), Is.EqualTo((3, 0)));
        Assert.That(Run(QueryVm, lateInfo, LoopInput), Is.EqualTo(Interpret(late, LoopInput)));
        Assert.That(StateOf(lateInfo).QueryRun, Is.Not.Null);
        Assert.That(IlEvm.IsPrecompiled(lateHash), Is.True);

        // Past genesis: the spec of the block after the head, a slot later.
        IlEvm.ClearPrecompiled();
        lines = Startup(config, [first], [list[0]], headNumber: 5, out activation, new BlocksConfig { SecondsPerSlot = 2 });
        Assert.That((activation.BlockNumber, activation.Timestamp), Is.EqualTo((6UL, (ulong?)1_700_000_002)));
        Assert.That(lines.Single(static l => l.StartsWith("IL-EVM precompile:", StringComparison.Ordinal)), Does.StartWith("IL-EVM precompile: 1 of 1 shipped contracts compiled for the spec of block 6 ("));
    }

    [Test]
    public void Deadline()
    {
        byte[][] codes = [Unique(LoopCode(), 0x26), Unique(LoopCode(), 0x27), Unique(LoopCode(), 0x28), Unique(LoopCode(), 0x29)];
        string[] list = [.. codes.Select(static c => ValueKeccak.Compute(c).ToString())];

        // No time at all: every entry is left for the windows between blocks, in list order.
        List<string> lines = Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, StartupCompileSeconds = 0 }, codes[..3], list, headNumber: 5, out _);
        Assert.That(lines.Single(static l => l.StartsWith("IL-EVM precompile:", StringComparison.Ordinal)),
            Does.StartWith("IL-EVM precompile: 0 of 4 shipped contracts compiled").And.Contain(", 4 past the 0 s deadline, left for the windows between blocks)"));
        Assert.That(IlShippedBacklog.Leftovers().Select(static e => e.Hash), Is.EqualTo(codes.Select(static c => ValueKeccak.Compute(c))));
        // Each is a job of the shipped class for the scheduler's windows, in list order; the scheduler started once the
        // compile was done, idle until a block comes.
        Assert.That(IlScheduler.Current!.State, Is.EqualTo(IlSchedulerState.Idle));
        Assert.That(IlScheduler.ScorerTick, Is.EqualTo((Action<long>)IlLive.Tick), "live profiling scores at the windows' maintenance");
        List<IlJob> jobs = IlScheduler.Current.PendingForTests();
        Assert.That(jobs.Select(static j => j.Class), Is.All.EqualTo(IlJob.ClassShipped));
        Assert.That(jobs.Select(static j => ((ShippedLayoutJob)j).Entry.Hash), Is.EqualTo(codes.Select(static c => ValueKeccak.Compute(c))));
        Assert.That(IlEvm.PrecompiledCount, Is.EqualTo(0));
        CodeInfo firstInfo = Stamped(codes[0]);
        Assert.That(Run(QueryVm, firstInfo, LoopInput), Is.EqualTo(Interpret(codes[0], LoopInput)));
        Assert.That(IlEvmFrameRunner.CodeSlot(firstInfo), Is.SameAs(IlRuntime.QuietAll));

        // Each step compiles the next entry, which runs compiled from then on; an entry whose code is missing joins the missing ones.
        for (int i = 0; i < codes.Length; i++)
        {
            Assert.That(IlShippedBacklog.CompileNextLeftover(), Is.True);
            Assert.That(IlShippedBacklog.LeftoverCount, Is.EqualTo(codes.Length - 1 - i));
        }

        Assert.That(IlShippedBacklog.CompileNextLeftover(), Is.False, "nothing left");
        Assert.That(IlEvm.PrecompiledCount, Is.EqualTo(3));
        Assert.That(IlShippedBacklog.Missing().Single().Hash, Is.EqualTo(ValueKeccak.Compute(codes[3])));
        Assert.That(Run(QueryVm, firstInfo, LoopInput), Is.EqualTo(Interpret(codes[0], LoopInput)));
        Assert.That(StateOf(firstInfo).QueryRun, Is.Not.Null, "the mark of the code met before resolves again");

        // The other rules have no windows between blocks: they compile the whole list before blocks start, as before.
        IlEvm.ClearPrecompiled();
        IlEvmSettings.Mode = IlMode.Legacy;
        lines = Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, StartupCompileSeconds = 0 }, codes[..3], list, headNumber: 5, out _);
        string compiled = lines.Single(static l => l.StartsWith("IL-EVM precompile:", StringComparison.Ordinal));
        Assert.That(compiled, Does.StartWith("IL-EVM precompile: 3 of 4 shipped contracts compiled for the spec of block 6 (list "));
        Assert.That(compiled, Does.Not.Contain("deadline"));
        Assert.That(IlShippedBacklog.LeftoverCount, Is.EqualTo(0));
    }

    [Test]
    public void Module_picks_the_rules_and_the_runners()
    {
        Assume.That(IlEvmModule.Unsupported(), Is.Null);
        Assume.That(IlEvmDiagnostics.Enabled, Is.False, "diagnostics keep the Legacy rules");
        IlEvmSettings.SelfCheck = true;

        (IContainer Container, IlEvmQueryModule? Query) Load(IlEvmConfig config)
        {
            ContainerBuilder builder = new();
            builder.RegisterModule(new IlEvmModule(config));
            IContainer container = builder.Build();
            return (container, container.ResolveOptional<IReadOnlyQueryModule>() as IlEvmQueryModule);
        }

        foreach ((IlEvmConfig config, IlMode mode) in new (IlEvmConfig, IlMode)[]
        {
            (new() { Enabled = true }, IlMode.Legacy),
            (new() { Enabled = true, Selection = IlEvmSelection.Simple }, IlMode.Simple),
            (new() { Enabled = true, Selection = IlEvmSelection.Adaptive }, IlMode.Adaptive),
            (new() { Enabled = true, Selection = IlEvmSelection.Legacy }, IlMode.Legacy),
            (new() { Enabled = true, Profile = true, Selection = IlEvmSelection.Legacy }, IlMode.Profile),
            (new() { Enabled = true, Stats = true, Profile = true }, IlMode.Stats),
        })
        {
            (IContainer container, _) = Load(config);
            using (container)
                Assert.That(IlEvmSettings.Mode, Is.EqualTo(mode), $"{config.Selection}, profile {config.Profile}, stats {config.Stats}");
        }

        // Live profiling of the queries is the default; of blocks it needs block processing's runner, so it sets Scope=All.
        IlEvmSettings.SelfCheck = true;
        (IContainer rpc, IlEvmQueryModule? rpcQuery) = Load(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, OpcodeThreshold = 0, BlockThreads = IlEvmBlockThreads.Main, Check = false });
        using (rpc)
        {
            Assert.That((IlRuntime.QueryCounting, IlEvmSettings.Prefer, IlEvmSettings.Scope, IlEvmSettings.ScopeForcedToAll), Is.EqualTo((true, IlEvmPrefer.Rpc, IlEvmScope.Rpc, false)));
            Assert.That(rpc.ResolveOptional<FrameCodeRunner<EthereumGasPolicy>>(), Is.Null, "no block runner with Scope=Rpc");
            Assert.That(rpcQuery?.Runner, Is.SameAs(IlEvmFrameRunner.Instance));
            Assert.That(IlEvmSettings.OpcodeThreshold, Is.EqualTo(long.MaxValue), "0 never compiles on the opcode count");
            Assert.That(IlRuntime.BlockThreadsMask, Is.EqualTo(IlRuntime.RunMain));
            Assert.That((IlEvmSettings.Gate, IlEvmSettings.QueryCheck, IlEvmSettings.BlockCheck, IlEvmSettings.BlockDeclineAll), Is.EqualTo((true, false, true, false)));
        }

        (IContainer blocks, IlEvmQueryModule? blocksQuery) = Load(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, Prefer = IlEvmPrefer.Blocks, Gate = false, BlockThreads = IlEvmBlockThreads.Workers, BlockDeclineAll = true });
        using (blocks)
        {
            Assert.That((IlRuntime.QueryCounting, IlEvmSettings.Scope, IlEvmSettings.ScopeForcedToAll), Is.EqualTo((false, IlEvmScope.All, true)));
            Assert.That(blocks.ResolveOptional<FrameCodeRunner<EthereumGasPolicy>>(), Is.SameAs(IlEvmFrameRunner.Block));
            Assert.That(blocksQuery?.AppliesTo, Is.EqualTo(ReadOnlyQueryKind.All), "every kind of query, as with Scope=All");
            Assert.That((IlEvmSettings.Gate, IlRuntime.BlockThreadsMask, IlEvmSettings.BlockDeclineAll), Is.EqualTo((false, IlRuntime.RunWorkers, true)));
            Assert.That(IlEvmSettings.OpcodeThreshold, Is.EqualTo(20_000));

            // The startup step warns of the scope it overrode.
            Nethermind.Core.Test.TestLogger logger = new();
            using CancellationTokenSource exit = new();
            new IlEvmStartupStep(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, Precompile = false }, null!, null!, null!, null!, null!, new ExitSource(exit.Token), new OneLogger(logger))
                .Execute(default).Wait();
            Assert.That(logger.LogList, Has.One.Contains("Prefer=Blocks").And.Contains("Scope is All"));
        }

        // Neither source, or rules other than the Adaptive ones: nothing counts, and the scope is as configured.
        foreach (IlEvmConfig config in new IlEvmConfig[]
        {
            new() { Enabled = true, Selection = IlEvmSelection.Adaptive, Prefer = IlEvmPrefer.None },
            new() { Enabled = true, Prefer = IlEvmPrefer.Blocks, Selection = IlEvmSelection.Legacy },
        })
        {
            (IContainer container, _) = Load(config);
            using (container)
            {
                Assert.That((IlRuntime.QueryCounting, IlEvmSettings.Scope, IlEvmSettings.ScopeForcedToAll), Is.EqualTo((false, IlEvmScope.Rpc, false)), $"{config.Prefer} {config.Selection}");
                Assert.That(container.ResolveOptional<FrameCodeRunner<EthereumGasPolicy>>(), Is.Null);
            }
        }
    }

    [Test]
    public void Exit_line_names_the_rules_the_list_and_the_code_states()
    {
        byte[] code = Unique(LoopCode(), 0x2a);
        Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true }, [code], [ValueKeccak.Compute(code).ToString(), Hash('f')], headNumber: 5, out _);
        Run(QueryVm, Stamped(code), LoopInput);
        IlEvmSettings.Prefer = IlEvmPrefer.Blocks;
        IlEvmSettings.Scope = IlEvmScope.All;
        Nethermind.Core.Test.TestLogger logger = new();
        using (CancellationTokenSource exit = new())
        {
            new IlEvmStartupStep(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, Precompile = false }, null!, null!, null!, null!, null!, new ExitSource(exit.Token), new OneLogger(logger))
                .Execute(default).Wait();
            exit.Cancel();
        }

        string line = logger.LogList.Single(static l => l.StartsWith("IL-EVM at exit:", StringComparison.Ordinal));
        Assert.That(line, Does.StartWith("IL-EVM at exit: mode Adaptive, prefer Blocks, scope All; list 1 compiled, 1 missing, 0 declined, 0 left; code states "));
        // Live profiling's promotions and what they hold, the visit gate's decisions, summed over the threads, the checks'
        // verdicts, the scheduler's windows, with the JIT's time in its steps, and what was compiled ahead follow; the
        // Legacy rules' counters, which these rules never move, do not.
        Assert.That(line, Does.Contain($" attached, {IlRuntime.ProfileCount} profiles; live blocks: 0 promoted, 0 demoted, 0 displaced for room, 0 released, 0 layouts failed, 0 deferred, 0 moved by a fork; 0 live in 0.0 of 128 MiB; "));
        Assert.That(line, Does.Not.Contain(" precompiled, taken by ").And.Not.Contain(" compiled at run time").And.Not.Contain("self-check").And.Not.Contain("compile thread busy"));
        Assert.That(line, Does.Contain(" profiles evicted; gate off (block ").And.Contain(" visits ").And.Contain(" parts "));
        Assert.That(line, Does.Contain("; check query ").And.Contain("; block "));
        Assert.That(line, Does.Contain("; scheduler idle: 0 windows, 0 closed back to back; ").And.Contain(" s of JIT in the steps; ").And.Contain(" 0 turned away, 0 failed; "));
        Assert.That(line, Does.Contain(" methods compiled ahead, ").And.EndWith(" left to their first call; no ahead copies"));

        // Under the Legacy rules, their own counters, as before the Adaptive ones, and none of the Adaptive rules' parts.
        IlEvmSettings.Mode = IlMode.Legacy;
        logger = new Nethermind.Core.Test.TestLogger();
        using (CancellationTokenSource exit = new())
        {
            new IlEvmStartupStep(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, Precompile = false }, null!, null!, null!, null!, null!, new ExitSource(exit.Token), new OneLogger(logger))
                .Execute(default).Wait();
            exit.Cancel();
        }

        line = logger.LogList.Single(static l => l.StartsWith("IL-EVM at exit:", StringComparison.Ordinal));
        Assert.That(line, Does.StartWith("IL-EVM at exit: mode Legacy, "));
        Assert.That(line, Does.Contain(" profiles; 1 precompiled, taken by ").And.Contain(" compiled at run time, ").And.Contain("; self-check ").And.Contain("; compile thread busy "));
        Assert.That(line, Does.Not.Contain("; live ").And.Not.Contain("; gate ").And.Not.Contain("scheduler").And.Not.Contain("compiled ahead"));
    }

    [Test]
    public void A_list_s_calibration_sets_the_block_runner_s_gate()
    {
        byte[] code = Unique(LoopCode(), 0x2b);
        Startup(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true }, [code], ["# calib fit=ok gate_block_visit_cold_ops=1024 gate_block_switch_ops=768 rho=0.4", ValueKeccak.Compute(code).ToString()], headNumber: 5, out _);
        Assert.That((IlGate.Block.VisitColdOps, IlGate.Block.SwitchOps), Is.EqualTo((1024, 768)));
        Assert.That(IlLive.Blocks.Model.Rho, Is.EqualTo(0.4), "and the live model's constants");
        Assert.That(IlGate.Query.VisitColdOps, Is.EqualTo(IlGate.QueryPrior.VisitColdOps), "the queries' runner keeps the laptop thresholds");
        Assert.That(IlRuntime.Calibration, Is.EqualTo(new[] { "fit=ok gate_block_visit_cold_ops=1024 gate_block_switch_ops=768 rho=0.4" }));
    }
    [Test]
    public void Always_load_for_every_source_scope_and_node()
    {
        // The module's own settings for each source and scope, on a node at genesis and one with a head: the list compiles.
        byte[] code = Unique(LoopCode(), 0x2c);
        ValueHash256 hash = ValueKeccak.Compute(code);
        string[] list = [hash.ToString()];
        foreach (IlEvmPrefer prefer in Enum.GetValues<IlEvmPrefer>())
        {
            foreach (IlEvmScope scope in Enum.GetValues<IlEvmScope>())
            {
                foreach (ulong head in new ulong[] { 0, 5 })
                {
                    IlEvm.ClearPrecompiled();
                    IlShippedBacklog.Reset();
                    IlEvmConfig config = new() { Enabled = true, Selection = IlEvmSelection.Adaptive, Prefer = prefer, Scope = scope };
                    IlEvmModule.Configure(config);
                    Assert.That((IlEvmSettings.Mode, IlEvmSettings.Prefer), Is.EqualTo((IlMode.Adaptive, prefer)));
                    Startup(config, [code], list, headNumber: head, out _);
                    Assert.That((IlShippedBacklog.Compiled, IlEvm.IsPrecompiled(hash)), Is.EqualTo((1, true)), $"Prefer={prefer}, Scope={scope}, head {head}");
                }
            }
        }

        // The Legacy rules compile as they did: nothing at genesis, and for the spec 12 seconds after the head, whatever
        // the slot.
        IlEvm.ClearPrecompiled();
        IlEvmConfig legacy = new() { Enabled = true, Selection = IlEvmSelection.Legacy };
        IlEvmModule.Configure(legacy);
        Assert.That(IlEvmSettings.Mode, Is.EqualTo(IlMode.Legacy));
        List<string> lines = Startup(legacy, [code], list, headNumber: 0, out _);
        Assert.That(lines, Has.One.EqualTo("IL-EVM precompile: skipped, the node is at genesis"));
        Assert.That(IlEvm.IsPrecompiled(hash), Is.False);
        Startup(legacy, [code], list, headNumber: 5, out ForkActivation activation, new BlocksConfig { SecondsPerSlot = 2 });
        Assert.That((activation.BlockNumber, activation.Timestamp), Is.EqualTo((6UL, (ulong?)1_700_000_012)));
        Assert.That(IlEvm.IsPrecompiled(hash), Is.True);
    }

    /// <summary>A contract over the opcode bodies compiled code reaches through entry points and helpers: storage, logs, hashing, memory and calldata.</summary>
    private static byte[] BodiesCode() => Nethermind.Core.Extensions.Bytes.FromHexString(
        // PUSH1 1, PUSH1 0, SSTORE; PUSH1 0, SLOAD, POP; PUSH1 32, PUSH1 0, KECCAK256, POP; PUSH1 0, CALLDATALOAD, PUSH1 0,
        // MSTORE; PUSH1 9, PUSH1 32, PUSH1 0, LOG1; PUSH1 2, PUSH1 0, TSTORE; PUSH1 0, TLOAD, POP; PUSH1 32, PUSH1 0,
        // PUSH1 64, MCOPY; ADDRESS, BALANCE, POP; STOP
        "6001600055" + "60005450" + "602060002050" + "6000356000" + "52" + "600960206000a1" + "600260005d" + "60005c50" + "60206000604" + "05e" + "303150" + "00");

    [Test]
    public void What_compiled_code_calls_is_compiled_ahead_with_it()
    {
        // Under the Adaptive rules a chunk's JIT compiles what it calls too, off the frames' threads, and lets go of the list.
        byte[] code = Unique(BodiesCode(), 0x2d);
        long before = IlWarmUp.Prepared;
        IlEvm.IlContract adaptive = Compile(code, Spec);
        Assert.That(Enumerable.Range(0, adaptive.ChunkCount).All(c => adaptive.CalleesOf(c) is null), Is.True, "compiled ahead, the list let go of");
        Assert.That(IlWarmUp.Prepared, Is.GreaterThanOrEqualTo(before), "each method once a process");

        // The Legacy rules leave them to the first frames, as before: the list stays, and names the bodies' entry points
        // compiled code calls through pointers, and the helpers its IL calls.
        IlEvmSettings.Mode = IlMode.Legacy;
        IlEvm.IlContract legacy = Compile(Unique(BodiesCode(), 0x2e), Spec);
        System.Reflection.MethodBase[] callees = [.. Enumerable.Range(0, legacy.ChunkCount).SelectMany(c => legacy.CalleesOf(c) ?? [])];
        Assert.That(callees, Is.Not.Empty);
        Assert.That(callees.Any(static m => m.Name == "Execute" && m.DeclaringType!.Name.Contains("SLoad", StringComparison.OrdinalIgnoreCase)), Is.True, "SLOAD's body");
        Assert.That(callees.Any(static m => m.Name == "Execute" && m.DeclaringType!.Name.StartsWith("Log", StringComparison.OrdinalIgnoreCase)), Is.True, "LOG1's body");
        // What those call is read from their IL, down to the instructions the interpreter would run.
        System.Reflection.MethodBase sload = callees.First(static m => m.Name == "Execute" && m.DeclaringType!.Name.Contains("SLoad", StringComparison.OrdinalIgnoreCase));
        Assert.That(IlWarmUp.CalleesOf(sload), Is.Not.Empty);

        // The plugin's own frame path, once, before any part runs.
        IlWarmUp.PrepareFramePath();
        Assert.That((IlWarmUp.FramePathPrepared, IlWarmUp.Failed), Is.EqualTo((true, 0L)));
    }

    /// <summary>
    /// The JIT a contract's first compiled run pays on the frame's own thread, in a process whose interpreter is warm: the
    /// methods compiled there and their time. Run alone, in a process of its own; the first run used to compile 138 methods.
    /// </summary>
    [Test, Explicit("JIT")]
    public void First_compiled_run_jit_on_the_frame_s_thread()
    {
        IlEvmSettings.Gate = false;
        byte[] code = Unique(BodiesCode(), 0x2f);
        byte[] input = new byte[32];
        CodeInfo interpreted = Stamped(code);
        for (int i = 0; i < 2_000; i++)
            Run(PlainVm, interpreted, input);
        Thread.Sleep(2_000);
        for (int i = 0; i < 200; i++)
            Run(PlainVm, interpreted, input);

        // As the startup step does: the frame path, then the list's compile, here one contract.
        (int Methods, double Ms) Jit() => ((int)System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true), System.Runtime.JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds);
        (int f0, double tf0) = Jit();
        System.Diagnostics.Stopwatch path = System.Diagnostics.Stopwatch.StartNew();
        IlWarmUp.PrepareFramePath();
        (int f1, double tf1) = Jit();
        TestContext.Out.WriteLine($"frame path: {IlWarmUp.Prepared} compiled ahead, {f1 - f0} methods and {tf1 - tf0:F1} ms of JIT on this thread, {path.ElapsedMilliseconds} ms in all");
        (int m0, double t0) = Jit();
        Ship(code);
        (int m1, double t1) = Jit();
        TestContext.Out.WriteLine($"startup-like compile: {m1 - m0} methods, {t1 - t0:F1} ms of JIT on the compiling thread; {IlWarmUp.Prepared} compiled ahead in all, {IlWarmUp.Failed} left");

        // The first compiled runs, on a thread of their own, as block processing's.
        string report = "";
        using JitNames names = new();
        Thread frame = new(() =>
        {
            names.Thread = OperatingSystem.IsWindows() ? (long)GetCurrentThreadId() : -1;
            CodeInfo compiled = Stamped(code);
            for (int run = 1; run <= 3; run++)
            {
                (int a, double ta) = Jit();
                OnBlockThread(() => Run(BlockVm, compiled, input));
                (int b, double tb) = Jit();
                report += $"run {run}: {b - a} methods, {tb - ta:F2} ms of JIT; ";
            }
        });
        frame.Start();
        frame.Join();
        TestContext.Out.WriteLine(report);
        // The runtime's events reach the listener a while after.
        Thread.Sleep(3_000);
        foreach (string name in names.Names)
            TestContext.Out.WriteLine("  jit: " + name);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>The methods the runtime reports JIT-compiling on one thread, by its operating system id; on Windows.</summary>
    private sealed class JitNames : System.Diagnostics.Tracing.EventListener
    {
        public long Thread = -1;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Names = new();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x10);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName != "MethodJittingStarted_V1" || Interlocked.Read(ref Thread) < 0 || e.OSThreadId != Interlocked.Read(ref Thread))
                return;
            Names.Enqueue($"{e.Payload?[4]}::{e.Payload?[5]}");
        }
    }
}
