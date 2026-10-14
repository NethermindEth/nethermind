// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Autofac;
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
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.IlEvm.IlEvmStats;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The measurement mode: every frame part timed on the path it took, the per-contract dump, and nothing else changed
/// - outcomes, hotness and the shipped list are as without it.
/// </summary>
[NonParallelizable]
public class IlEvmStatsTests
{
    private static readonly Address Caller = TestItem.AddressA;
    private static readonly Address Executing = TestItem.AddressB;
    private static readonly Address Callee = TestItem.AddressD;

    /// <summary>The runs each timing test makes, so the parts' times add up past a coarse timer's resolution.</summary>
    private const int Runs = 50;

    private IReleaseSpec _spec = null!;
    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IWorldState _state = null!;
    /// <summary>Block processing's machine, with the timed runner Scope=All attaches for a measurement run.</summary>
    private EthereumVirtualMachine _blockVm = null!;
    /// <summary>A read-only query's machine, with the timed runner that compiles at run time.</summary>
    private EthereumVirtualMachine _queryVm = null!;
    /// <summary>The same two with the production runners, which time nothing.</summary>
    private EthereumVirtualMachine _plainBlockVm = null!;
    private EthereumVirtualMachine _plainQueryVm = null!;
    private IDisposable? _stateScope;
    private Settings _saved;

    /// <summary>The settings these tests change, restored after each.</summary>
    private readonly record struct Settings(
        bool Enabled, int Threshold, int Opcodes, int HotRate, bool Sync, bool SelfCheck, bool Profile, bool Stats, bool BlockCompile, bool BlockCheck,
        int Sampling, int CheckDelay, double KeepRatio, int WarmUp, int Recheck, int EarlyRecheck)
    {
        public static Settings Read() => new(IlEvmSettings.Enabled, IlEvmSettings.Threshold, IlEvmSettings.OpcodeThreshold, IlEvmSettings.HotOpcodesPerSecond,
            IlEvmSettings.CompileSynchronously, IlEvmSettings.SelfCheck, IlEvmSettings.Profile, IlEvmSettings.Stats,
            IlEvmSettings.BlockRuntimeCompile, IlEvmSettings.BlockSelfCheck, IlEvmSettings.HotnessSampling, IlEvmSettings.SelfCheckDelayMs,
            IlEvmSettings.SelfCheckKeepRatio, IlEvmSettings.SelfCheckWarmUpMs, IlEvmSettings.SelfCheckRecheckMs, IlEvmSettings.SelfCheckEarlyRecheckMs);

        public void Write()
        {
            IlEvmSettings.Enabled = Enabled;
            IlEvmSettings.Threshold = Threshold;
            IlEvmSettings.OpcodeThreshold = Opcodes;
            IlEvmSettings.HotOpcodesPerSecond = HotRate;
            IlEvmSettings.CompileSynchronously = Sync;
            IlEvmSettings.SelfCheck = SelfCheck;
            IlEvmSettings.Profile = Profile;
            IlEvmSettings.Stats = Stats;
            IlEvmSettings.BlockRuntimeCompile = BlockCompile;
            IlEvmSettings.BlockSelfCheck = BlockCheck;
            IlEvmSettings.HotnessSampling = Sampling;
            IlEvmSettings.SelfCheckDelayMs = CheckDelay;
            IlEvmSettings.SelfCheckKeepRatio = KeepRatio;
            IlEvmSettings.SelfCheckWarmUpMs = WarmUp;
            IlEvmSettings.SelfCheckRecheckMs = Recheck;
            IlEvmSettings.SelfCheckEarlyRecheckMs = EarlyRecheck;
        }
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _spec = Osaka.Instance;
        _container = new ContainerBuilder().AddModule(new TestNethermindModule(_spec)).Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _scope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        _state = _scope.Resolve<IWorldState>();
        EthereumVirtualMachine Machine(FrameCodeRunner<EthereumGasPolicy> runner) =>
            new(_scope.Resolve<IBlockhashProvider>(), _scope.Resolve<ISpecProvider>(), LimboLogs.Instance, runner);
        _blockVm = Machine(IlEvmTimedFrameRunner.Block);
        _queryVm = Machine(IlEvmTimedFrameRunner.Instance);
        _plainBlockVm = Machine(IlEvmFrameRunner.Block);
        _plainQueryVm = Machine(IlEvmFrameRunner.Instance);
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(Caller, 1_000_000);
        _state.CreateAccount(Executing, 1_000);
        _state.CreateAccount(Callee, 1_000);
        _state.InsertCode(Callee, CalleeCode(), _spec);
        _state.Commit(_spec);
        _state.CommitTree(0);
        foreach (IVirtualMachine vm in new IVirtualMachine[] { _blockVm, _queryVm, _plainBlockVm, _plainQueryVm })
        {
            vm.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.WithNumber(7).WithTimestamp(1234).TestObject, _spec));
            vm.SetTxExecutionContext(new TxExecutionContext(Caller, _scope.Resolve<ICodeInfoRepository>(), null, 3));
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _stateScope?.Dispose();
        foreach (EthereumVirtualMachine? vm in new[] { _blockVm, _queryVm, _plainBlockVm, _plainQueryVm })
            vm?.Dispose();
        _scope?.Dispose();
        _container?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _saved = Settings.Read();
        Forget();
        IlEvmSettings.Enabled = true;
        // Nothing becomes hot on its own: whatever runs compiled was shipped, unless a test says otherwise.
        IlEvmSettings.Threshold = int.MaxValue;
        IlEvmSettings.OpcodeThreshold = int.MaxValue;
        IlEvmSettings.CompileSynchronously = false;
        IlEvmSettings.SelfCheck = false;
        IlEvmSettings.Profile = false;
        IlEvmSettings.Stats = false;
        IlEvmSettings.BlockRuntimeCompile = false;
        IlEvmSettings.BlockSelfCheck = false;
    }

    [TearDown]
    public void TearDown()
    {
        _saved.Write();
        ProcessingThread.IsBlockProcessingThread = false;
        Forget();
    }

    private static void Forget()
    {
        IlEvmStats.Reset();
        IlEvmProfile.Reset();
        IlEvm.ClearPrecompiled();
    }

    /// <summary>Marks the calling thread as block processing marks its own for a processing pass, until disposed.</summary>
    private sealed class BlockProcessing : IDisposable
    {
        private readonly bool _previous = ProcessingThread.IsBlockProcessingThread;

        public BlockProcessing() => ProcessingThread.IsBlockProcessingThread = true;

        public void Dispose() => ProcessingThread.IsBlockProcessingThread = _previous;
    }

    /// <summary>ADD and MUL over calldata, SSTORE of the result, then RETURN of it: 16 opcodes; a trailing STOP no frame reaches makes the code this fixture's own.</summary>
    private static byte[] CalleeCode() => Bytes.FromHexString("6000356020350160403502806000556000526020" + "6000f3" + "00");

    private const int CalleeOps = 16;

    /// <summary>Calls the callee with no data, so the caller's frame is offered again when the call returns, then stops.</summary>
    /// <remarks>PUSH1 x5, PUSH20, GAS, CALL before the call; POP, STOP after it.</remarks>
    private static byte[] CallerCode() =>
        Bytes.FromHexString("60006000600060006000" + "73" + Callee.ToString(false).Substring(2) + "5af15000");

    private const int CallerOps = 10;

    /// <summary>
    /// A segment (PUSH1 1, PUSH1 2, ADD, POP), a JUMPDEST, then one that stores a word to memory before ten PUSH1 1, POP pairs
    /// and a STOP. On <see cref="ShortGas"/> the second segment's precondition fails and the walk meets MSTORE's memory
    /// cost, which it cannot know: compiled code hands the frame back there, and the interpreter runs out of gas in the pairs.
    /// </summary>
    private static byte[] HandBackCode() =>
        Bytes.FromHexString("6001600201505b" + "6020600052" + string.Concat(Enumerable.Repeat("600150", 10)) + "00");

    private const ulong ShortGas = 40;

    /// <summary>A loop that stores <c>calldata[0] &amp; 31</c> words, then returns the first, and a trailing STOP no frame reaches.</summary>
    private static byte[] LoopCode() => Bytes.FromHexString("600035601f165b8015601657600190038080526006565b5060205ff3" + "00");

    private static readonly byte[] LoopInput = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000007");

    /// <summary>Initcode that deploys the one-byte code STOP: PUSH1 0, PUSH1 0, MSTORE8, PUSH1 1, PUSH1 0, RETURN.</summary>
    private static byte[] InitCode() => Bytes.FromHexString("600060005360016000f3");

    /// <summary>
    /// Deploys <see cref="InitCode"/> with CREATE, then with CREATE2, then stops: three parts of seven, six and two
    /// opcodes, around two initcode frames of six.
    /// </summary>
    private static byte[] FactoryCode() =>
        Bytes.FromHexString("69" + "600060005360016000f3" + "600052" + "600a60166000f050" + "6001600a60166000f550" + "00");

    private const int FactoryOps = 15;

    private readonly record struct Outcome(bool Error, string Output, ulong GasLeft, string Storage, int OpCodes);

    private Outcome Run(IVirtualMachine vm, CodeInfo codeInfo, byte[] input, ulong gas = 1_000_000)
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Executing, codeSource: Executing, caller: Caller,
            codeInfo: codeInfo, callDepth: 0, value: 0, inputData: input);
        using StackAccessTracker accessTracker = new();
        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, environment, accessTracker, _state.TakeSnapshot());
        TransactionSubstate result = vm.ExecuteTransaction<OffFlag>(state, _state, NullTxTracer.Instance);
        _state.Get(new StorageCell(Executing, 0), out UInt256 slot0);
        Outcome outcome = new(result.IsError, result.Output.ToArray().ToHexString(), state.Gas.Value, slot0.ToString(), vm.OpCodeCount);
        _state.Reset();
        return outcome;
    }

    /// <summary>The interpreter's outcome: the runner declines every frame, and nothing is timed.</summary>
    private Outcome Interpret(byte[] code, byte[] input, ulong gas = 1_000_000)
    {
        bool enabled = IlEvmSettings.Enabled;
        IlEvmSettings.Enabled = false;
        try
        {
            return Run(_blockVm, new CodeInfo(code), input, gas);
        }
        finally
        {
            IlEvmSettings.Enabled = enabled;
        }
    }

    private void Ship(params byte[][] codes)
    {
        OpcodeBodySet? bodies = IlEvm.BodySetFor(_spec);
        Assert.That(bodies, Is.Not.Null);
        Dictionary<ValueHash256, IlEvm.IlContract> compiled = [];
        foreach (byte[] code in codes)
        {
            IlEvm.IlContract? contract = IlEvm.Precompile(code, bodies!, out string? reason);
            Assert.That(contract, Is.Not.Null, reason);
            compiled[ValueKeccak.Compute(code)] = contract!;
        }

        IlEvm.PublishPrecompiled(compiled);
    }

    private static ContractTotals Totals(byte[] code)
    {
        ValueHash256 hash = ValueKeccak.Compute(code);
        ContractTotals? totals = IlEvmStats.Snapshot().SingleOrDefault(c => c.Hash == hash);
        Assert.That(totals, Is.Not.Null, $"no stats for {hash}");
        return totals!;
    }

    /// <summary>The paths and thread kinds <paramref name="totals"/> holds parts on.</summary>
    private static List<(RunPath, ThreadKind)> PathsOf(ContractTotals totals) =>
        [.. from path in Enum.GetValues<RunPath>() from thread in Enum.GetValues<ThreadKind>() where totals.Of(path, thread).Parts > 0 select (path, thread)];

    private static void OnThread(Action action, bool blockProcessing = false)
    {
        Thread thread = new(() =>
        {
            using BlockProcessing? scope = blockProcessing ? new BlockProcessing() : null;
            action();
        });
        thread.Start();
        thread.Join();
    }

    [Test]
    public void Profile_with_stats_times_each_part_of_a_frame_on_the_interpreter()
    {
        IlEvmSettings.Profile = true;
        IlEvmSettings.Stats = true;
        long compiledBefore = IlEvmSettings.CompiledContracts;
        Outcome expected = Interpret(CallerCode(), []);
        using (new BlockProcessing())
        {
            for (int i = 0; i < Runs; i++)
                Assert.That(Run(_blockVm, new CodeInfo(CallerCode()), []), Is.EqualTo(expected));
        }

        ContractTotals caller = Totals(CallerCode());
        ContractTotals callee = Totals(CalleeCode());
        Assert.That(PathsOf(caller), Is.EqualTo(new[] { (RunPath.Interpreted, ThreadKind.Main) }));
        Assert.That(PathsOf(callee), Is.EqualTo(new[] { (RunPath.Interpreted, ThreadKind.Main) }));
        // The caller's frame is offered on entry and again when the call returns; the callee's once.
        Assert.That(caller.Entries, Is.EqualTo(Runs));
        Assert.That(caller.Of(RunPath.Interpreted, ThreadKind.Main).Parts, Is.EqualTo(2 * Runs));
        Assert.That(caller.Of(RunPath.Interpreted, ThreadKind.Main).Ops, Is.EqualTo(CallerOps * Runs));
        // Eight opcodes up to the call, two after it.
        Assert.That(caller.Of(RunPath.Interpreted, ThreadKind.Main, 3).Parts, Is.EqualTo(Runs));
        Assert.That(caller.Of(RunPath.Interpreted, ThreadKind.Main, 3).Ops, Is.EqualTo(8 * Runs));
        Assert.That(caller.Of(RunPath.Interpreted, ThreadKind.Main, 1).Parts, Is.EqualTo(Runs));
        Assert.That(callee.Entries, Is.EqualTo(Runs));
        Assert.That(callee.Of(RunPath.Interpreted, ThreadKind.Main).Parts, Is.EqualTo(Runs));
        // A part never includes its callee: the two frames' opcodes are the transaction's.
        Assert.That(caller.Ops + callee.Ops, Is.EqualTo((long)expected.OpCodes * Runs));
        Assert.That(caller.Ticks, Is.GreaterThan(0));
        Assert.That(callee.Ticks, Is.GreaterThan(0));
        (long parts, _, long ticks) = caller.Of(RunPath.Interpreted, ThreadKind.Main, 3);
        Assert.That(caller.LongestTicks(RunPath.Interpreted, ThreadKind.Main, 3), Is.InRange(ticks / parts, ticks), "the longest part is at least the mean");
        // Nothing is compiled while profiling, neither shipped nor at run time.
        Assert.That(IlEvmStats.Snapshot().Where(static c => c.RanCompiled), Is.Empty);
        Assert.That(IlEvmSettings.CompiledContracts, Is.EqualTo(compiledBefore));
        Assert.That(IlEvmProfile.Snapshot(), Is.Empty, "the stats replace the profile's counters");
    }

    [Test]
    public void A_shipped_contract_s_parts_are_timed_on_compiled_code()
    {
        IlEvmSettings.Stats = true;
        Ship(CallerCode());
        Outcome expected = Interpret(CallerCode(), []);
        using (new BlockProcessing())
        {
            for (int i = 0; i < Runs; i++)
                Assert.That(Run(_blockVm, new CodeInfo(CallerCode()), []), Is.EqualTo(expected));
        }

        ContractTotals caller = Totals(CallerCode());
        ContractTotals callee = Totals(CalleeCode());
        Assert.That(PathsOf(caller), Is.EqualTo(new[] { (RunPath.Compiled, ThreadKind.Main) }));
        Assert.That(caller.Entries, Is.EqualTo(Runs));
        Assert.That(caller.Of(RunPath.Compiled, ThreadKind.Main).Parts, Is.EqualTo(2 * Runs));
        Assert.That(caller.Of(RunPath.Compiled, ThreadKind.Main).Ops, Is.EqualTo(CallerOps * Runs), "the opcodes the interpreter runs for the frame");
        Assert.That(caller.Of(RunPath.Compiled, ThreadKind.Main, 3).Ops, Is.EqualTo(8 * Runs));
        Assert.That(caller.Ticks, Is.GreaterThan(0));
        Assert.That(caller.Shipped, Is.True);
        // The block runner compiles nothing it was not shipped: the callee stays on the interpreter.
        Assert.That(PathsOf(callee), Is.EqualTo(new[] { (RunPath.Interpreted, ThreadKind.Main) }));
        Assert.That(callee.Shipped, Is.False);
        Assert.That(caller.Ops + callee.Ops, Is.EqualTo((long)expected.OpCodes * Runs));
    }

    [Test]
    public void A_part_compiled_code_hands_back_is_timed_on_both_and_ends_as_on_the_interpreter()
    {
        IlEvmSettings.Stats = true;
        Ship(HandBackCode());
        Outcome expected = Interpret(HandBackCode(), [], ShortGas);
        Assume.That(expected.Error, Is.True, "the interpreter runs out of gas in the second segment");
        using (new BlockProcessing())
        {
            for (int i = 0; i < Runs; i++)
                Assert.That(Run(_blockVm, new CodeInfo(HandBackCode()), [], ShortGas), Is.EqualTo(expected));
        }

        ContractTotals totals = Totals(HandBackCode());
        Assert.That(PathsOf(totals), Is.EqualTo(new[] { (RunPath.HandedBack, ThreadKind.Main) }));
        (long parts, long ops, long ticks) = totals.Of(RunPath.HandedBack, ThreadKind.Main);
        Assert.That(parts, Is.EqualTo(Runs));
        Assert.That(totals.Entries, Is.EqualTo(Runs));
        // Compiled code ran the first segment and the JUMPDEST at most; the interpreter the rest.
        Assert.That(ops, Is.EqualTo((long)expected.OpCodes * Runs), "compiled and interpreted opcodes together");
        Assert.That(totals.HandedBackCompiledOps, Is.InRange(4L * Runs, 5L * Runs), "compiled code ran the first segment before it handed back");
        Assert.That(ticks, Is.GreaterThan(0));

        // With the gas for all of it, the frame runs compiled to its end.
        IlEvmStats.Reset();
        Outcome full = Interpret(HandBackCode(), []);
        using (new BlockProcessing())
            Assert.That(Run(_blockVm, new CodeInfo(HandBackCode()), []), Is.EqualTo(full));
        Assert.That(PathsOf(Totals(HandBackCode())), Is.EqualTo(new[] { (RunPath.Compiled, ThreadKind.Main) }));
        Assert.That(Totals(HandBackCode()).Ops, Is.EqualTo(full.OpCodes));
    }

    [Test]
    public void Handed_back_parts_never_feed_hotness()
    {
        IlEvmSettings.Stats = true;
        Ship(HandBackCode());
        Outcome expected = Interpret(HandBackCode(), [], ShortGas);
        CodeInfo codeInfo = new(HandBackCode());
        for (int i = 0; i < Runs; i++)
            Assert.That(Run(_queryVm, codeInfo, [], ShortGas), Is.EqualTo(expected));

        Assert.That(Totals(HandBackCode()).Of(RunPath.HandedBack, ThreadKind.Other).Parts, Is.EqualTo(Runs));
        // The query runner looked the shipped contract up, so the code has a state; no frame counted toward it.
        IlEvm.IlCodeState state = (IlEvm.IlCodeState)IlEvmFrameRunner.CodeSlot(codeInfo)!;
        Assert.That((state.Executions, state.OpCodesRun), Is.EqualTo((0, 0)), "the interpreter's opcodes after a hand-back are not reported for hotness");
    }

    [Test]
    public void Parts_in_block_processing_count_as_main_and_the_others_as_other()
    {
        IlEvmSettings.Profile = true;
        IlEvmSettings.Stats = true;
        using (new BlockProcessing())
            Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
        Assert.That(PathsOf(Totals(LoopCode())), Is.EqualTo(new[] { (RunPath.Interpreted, ThreadKind.Main) }));

        OnThread(() => Run(_blockVm, new CodeInfo(LoopCode()), LoopInput));
        OnThread(() => Run(_blockVm, new CodeInfo(LoopCode()), LoopInput), blockProcessing: true);
        // A thread whose processing pass ended is block processing no more, whichever thread the next pass runs on.
        Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
        ContractTotals totals = Totals(LoopCode());
        Assert.That(totals.Of(RunPath.Interpreted, ThreadKind.Main).Parts, Is.EqualTo(2), "a table is merged after its thread is gone");
        Assert.That(totals.Of(RunPath.Interpreted, ThreadKind.Other).Parts, Is.EqualTo(2));
        Assert.That(totals.Of(RunPath.Interpreted, ThreadKind.Other).Ops, Is.EqualTo(totals.Of(RunPath.Interpreted, ThreadKind.Main).Ops));
    }

    [Test]
    public void Initcode_is_never_timed()
    {
        IlEvmSettings.Stats = true;
        Outcome expected = Interpret(FactoryCode(), []);
        Assume.That(expected.Error, Is.False, "the factory stops");
        Assert.That(expected.OpCodes, Is.EqualTo(FactoryOps + 2 * 6));
        ValueHash256 initHash = ValueKeccak.Compute(InitCode());

        // On the interpreter, then with the factory and its initcode shipped, on both runners.
        foreach (bool shipped in new[] { false, true })
        {
            IlEvmStats.Reset();
            if (shipped)
                Ship(FactoryCode(), InitCode());
            using (new BlockProcessing())
            {
                for (int i = 0; i < Runs; i++)
                    Assert.That(Run(_blockVm, new CodeInfo(FactoryCode()), []), Is.EqualTo(expected), $"shipped {shipped}");
            }

            for (int i = 0; i < Runs; i++)
                Assert.That(Run(_queryVm, new CodeInfo(FactoryCode()), []), Is.EqualTo(expected), $"shipped {shipped}, query");

            List<ContractTotals> contracts = IlEvmStats.Snapshot();
            Assert.That(contracts.Select(static c => c.Hash), Is.EqualTo(new[] { ValueKeccak.Compute(FactoryCode()) }), $"shipped {shipped}: no initcode {initHash}");
            ContractTotals factory = contracts[0];
            RunPath path = shipped ? RunPath.Compiled : RunPath.Interpreted;
            Assert.That(PathsOf(factory), Is.EqualTo(new[] { (path, ThreadKind.Main), (path, ThreadKind.Other) }));
            Assert.That(factory.Entries, Is.EqualTo(2 * Runs));
            Assert.That(factory.Of(path, ThreadKind.Main).Parts, Is.EqualTo(3 * Runs), "on entry and after each create");
            Assert.That(factory.Ops, Is.EqualTo(2L * FactoryOps * Runs), "the initcode's opcodes are no part of the factory's");
        }
    }

    [Test]
    public void A_part_s_time_leaves_out_the_collections_it_waited_for()
    {
        IlEvmSettings.Stats = true;
        CodeInfo codeInfo = new(LoopCode());
        long before = Stopwatch.GetTimestamp();
        IlEvmStats.BeginInterpreted(codeInfo, entry: true, count: false);
        long pausedBefore = GC.GetTotalPauseDuration().Ticks;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long paused = GC.GetTotalPauseDuration().Ticks - pausedBefore;
        long end = Stopwatch.GetTimestamp();
        Assert.That(IlEvmStats.TryEnd(codeInfo, 5, end, out _), Is.True);
        Assume.That(paused, Is.GreaterThan(0), "a forced blocking collection pauses the process");

        long pausedTicks = paused * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        long ticks = Totals(LoopCode()).Ticks;
        Assert.That(ticks, Is.LessThanOrEqualTo(end - before - pausedTicks + 1), "the collection is taken off the part's time");
        TableTotals tables = IlEvmStats.Tables();
        Assert.That(tables.PausedParts, Is.EqualTo(1));
        Assert.That(tables.PausedTicks, Is.GreaterThanOrEqualTo(pausedTicks - 1));

        // A compiled part's too, and never below zero: a pause longer than the part leaves it no time.
        long start = IlEvmStats.PausedTicks();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        IlEvmStats.RecordCompiled(codeInfo, entry: true, opCodes: 5, ticks: 1, start);
        Assert.That(IlEvmStats.Tables().PausedParts, Is.EqualTo(2));
        Assert.That(Totals(LoopCode()).Of(RunPath.Compiled, ThreadKind.Other), Is.EqualTo((1L, 5L, 0L)));
    }

    [Test]
    public void Slow_parts_and_each_bucket_s_longest_part_are_kept_apart()
    {
        IlEvmSettings.Stats = true;
        CodeInfo codeInfo = new(LoopCode());
        long slowPerOp = Math.Max(1, SlowNanosecondsPerOpcode * Stopwatch.Frequency / 1_000_000_000);
        long slow = slowPerOp * 12 + 1;
        IlEvmStats.RecordCompiled(codeInfo, entry: true, opCodes: 10, ticks: 3, IlEvmStats.PausedTicks());
        IlEvmStats.RecordCompiled(codeInfo, entry: false, opCodes: 12, ticks: slow, IlEvmStats.PausedTicks());
        IlEvmStats.RecordCompiled(codeInfo, entry: false, opCodes: 9, ticks: 7, IlEvmStats.PausedTicks());
        IlEvmStats.RecordCompiled(codeInfo, entry: false, opCodes: 0, ticks: slowPerOp, IlEvmStats.PausedTicks());

        ContractTotals totals = Totals(LoopCode());
        Assert.That(totals.Of(RunPath.Compiled, ThreadKind.Other, 3), Is.EqualTo((3L, 31L, slow + 10)));
        Assert.That(totals.LongestTicks(RunPath.Compiled, ThreadKind.Other, 3), Is.EqualTo(slow));
        Assert.That(totals.Of(RunPath.Compiled, ThreadKind.Other, 0), Is.EqualTo((1L, 0L, slowPerOp)), "a part of no opcodes is in the first bucket");
        Assert.That(totals.Slow(RunPath.Compiled, ThreadKind.Other), Is.EqualTo((1L, 12L, slow)), "at the limit a part is not slow");
        Assert.That(totals.Ticks, Is.EqualTo(slow + 10 + slowPerOp), "the totals count the slow parts too");

        string line = DumpTo(10).LogList.Single(static l => l.StartsWith("IL-EVM stats 1 ", StringComparison.Ordinal));
        Assert.That(line, Does.Contain($" C.O=4/31/{Nanoseconds(slow + 10 + slowPerOp)} sC.O=1/12/{Nanoseconds(slow)} hC.O=0:1/0/{Nanoseconds(slowPerOp)}/{Nanoseconds(slowPerOp)},3:3/31/{Nanoseconds(slow + 10)}/{Nanoseconds(slow)}"));
    }

    [Test]
    public void Distinct_blocks_count_block_processing_only_and_never_go_back()
    {
        IlEvmSettings.Profile = true;
        IlEvmSettings.Stats = true;
        IlEvmStats.BlockStarted();
        // Prewarming runs a block's code before block processing reaches it, and before the block starts.
        Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
        Assert.That(Totals(LoopCode()).Blocks, Is.EqualTo(0));
        using (new BlockProcessing())
        {
            Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
            Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
        }

        OnThread(() => Run(_blockVm, new CodeInfo(LoopCode()), LoopInput), blockProcessing: true);
        Assert.That(Totals(LoopCode()).Blocks, Is.EqualTo(1), "one block, however many threads ran the code in it");
        IlEvmStats.BlockStarted();
        OnThread(() => Run(_blockVm, new CodeInfo(LoopCode()), LoopInput), blockProcessing: true);
        Assert.That(Totals(LoopCode()).Blocks, Is.EqualTo(2));
        Assert.That(IlEvmStats.Blocks, Is.EqualTo(2));

        // A thread that read the block before the next one started counts nothing when it gets to it.
        Shared shared = new();
        Assert.That(IlEvmStats.CountBlock(shared, 5), Is.True);
        Assert.That(IlEvmStats.CountBlock(shared, 6), Is.True);
        Assert.That(IlEvmStats.CountBlock(shared, 5), Is.False);
        Assert.That(IlEvmStats.CountBlock(shared, 6), Is.False);
        Assert.That(shared.Blocks, Is.EqualTo(2));
    }

    [Test]
    public void Tables_are_keyed_by_code_hash_and_keep_no_code_alive()
    {
        IlEvmSettings.Profile = true;
        IlEvmSettings.Stats = true;
        // Code a cache reloaded is a new code info of the same hash: one entry, whatever the churn.
        for (int i = 0; i < 5; i++)
            Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
        TableTotals tables = IlEvmStats.Tables();
        Assert.That((tables.Entries, tables.Threads), Is.EqualTo((1, 1)));
        Assert.That(Totals(LoopCode()).Entries, Is.EqualTo(5));

        byte[] dropped = Unique(LoopCode(), 0x51);
        WeakReference codeInfo = RunAndForget(dropped);
        // Another code's part takes the one code info a table keeps, its last.
        Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(codeInfo.IsAlive, Is.False, "the tables keep no code alive");
        Assert.That(Totals(dropped).Entries, Is.EqualTo(1), "its counts outlive it");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference RunAndForget(byte[] code)
    {
        CodeInfo codeInfo = new(code);
        Run(_blockVm, codeInfo, LoopInput);
        return new WeakReference(codeInfo);
    }

    private static readonly Regex HeaderLine = new(
        @"^IL-EVM stats: \d+ contracts, \d+ blocks, totals( [ICH]\.[MO]=\d+/\d+/\d+){6}( s[ICH]\.[MO]=\d+/\d+/\d+){6}" +
        @"( h[ICH]\.[MO]=\d+:\d+/\d+/\d+/\d+(,\d+:\d+/\d+/\d+/\d+)*)* gc=\d+/\d+ runtime-compiled=\d+ handoffs=\d+/\d+/\d+ tables=\d+/\d+ heapMiB=\d+$");

    private static readonly Regex ContractLine = new(
        @"^IL-EVM stats (?<rank>\d+) (?<hash>0x[0-9a-f]{64}) size=(?<size>\d+) entries=(?<entries>\d+) blocks=(?<blocks>\d+) shipped=(?<shipped>[01])" +
        @"(?<combo> [ICH]\.[MO]=\d+/\d+/(?<ns>\d+))+(?<slow> s[ICH]\.[MO]=\d+/\d+/\d+)*(?<buckets> h[ICH]\.[MO]=\d+:\d+/\d+/\d+/\d+(,\d+:\d+/\d+/\d+/\d+)*)+$");

    private static readonly Regex ProfileLine = new(@"^IL-EVM profile \d+ 0x[0-9a-f]{64} ops=\d+ frames=\d+ size=\d+ cum=\d\.\d{4}$");

    private static Nethermind.Core.Test.TestLogger DumpTo(int top)
    {
        Nethermind.Core.Test.TestLogger logger = new();
        IlEvmStats.Dump(new ILogger(logger), top);
        return logger;
    }

    [Test]
    public void Dump_logs_a_header_a_line_per_contract_and_an_end_line_once()
    {
        IlEvmSettings.Stats = true;
        Ship(CallerCode(), HandBackCode());
        IlEvmStats.BlockStarted();
        using (new BlockProcessing())
        {
            Run(_blockVm, new CodeInfo(CallerCode()), []);
            Run(_blockVm, new CodeInfo(HandBackCode()), [], ShortGas);
            IlEvmStats.BlockStarted();
            Run(_blockVm, new CodeInfo(CallerCode()), []);
        }

        OnThread(() => Run(_blockVm, new CodeInfo(CallerCode()), []));

        List<string> lines = DumpTo(10).LogList;
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.That(lines[0], Does.Match(HeaderLine.ToString()));
        Assert.That(lines[0], Does.StartWith("IL-EVM stats: 3 contracts, 2 blocks, totals "));
        Assert.That(lines[^1], Is.EqualTo("IL-EVM stats end"));
        List<string> contracts = lines[1..^1];
        Assert.That(contracts, Has.Count.EqualTo(3));
        Assert.That(contracts, Has.All.Match(ContractLine.ToString()));
        Assert.That(lines.Any(static l => l.StartsWith("IL-EVM profile", StringComparison.Ordinal)), Is.False, "the profile's lines only with Profile on");

        Dictionary<ValueHash256, Match> byHash = contracts.Select(static l => ContractLine.Match(l)).ToDictionary(static m => new ValueHash256(m.Groups["hash"].Value));
        Match caller = byHash[ValueKeccak.Compute(CallerCode())];
        Assert.That(caller.Groups["size"].Value, Is.EqualTo(CallerCode().Length.ToString()));
        Assert.That(caller.Groups["entries"].Value, Is.EqualTo("3"));
        Assert.That(caller.Groups["blocks"].Value, Is.EqualTo("2"), "the part on another thread counts no block");
        Assert.That(caller.Groups["shipped"].Value, Is.EqualTo("1"));
        Assert.That(caller.Groups["combo"].Captures.Select(static c => c.Value.Split('=')[0].Trim()), Is.EqualTo(new[] { "C.M", "C.O" }));
        Assert.That(caller.Groups["combo"].Captures[0].Value, Does.StartWith(" C.M=4/20/"));
        Assert.That(caller.Groups["buckets"].Captures.Select(static c => c.Value), Has.Some.StartWith(" hC.M=1:2/4/").And.Some.Contains(",3:2/16/"));
        Match callee = byHash[ValueKeccak.Compute(CalleeCode())];
        Assert.That(callee.Groups["combo"].Captures.Select(static c => c.Value.Split('=')[0].Trim()), Is.EqualTo(new[] { "I.M", "I.O" }));
        Assert.That(callee.Groups["shipped"].Value, Is.EqualTo("0"));
        Match handBack = byHash[ValueKeccak.Compute(HandBackCode())];
        Assert.That(handBack.Groups["combo"].Captures.Select(static c => c.Value.Split('=')[0].Trim()), Is.EqualTo(new[] { "H.M" }));
        Assert.That(handBack.Groups["blocks"].Value, Is.EqualTo("1"));

        // Ranked by time over every path, the most first.
        List<long> times = [.. contracts.Select(static l => ContractLine.Match(l).Groups["ns"].Captures.Sum(static c => long.Parse(c.Value)))];
        Assert.That(times, Is.Ordered.Descending);
        Assert.That(contracts.Select(static l => ContractLine.Match(l).Groups["rank"].Value), Is.EqualTo(new[] { "1", "2", "3" }));
        List<ContractTotals> snapshot = IlEvmStats.Snapshot();
        Assert.That(snapshot.Select(static c => c.Ticks), Is.Ordered.Descending);
        // The header's totals are the contracts' sums, and its bookkeeping the run's.
        long compiledMainOps = snapshot.Sum(static c => c.Of(RunPath.Compiled, ThreadKind.Main).Ops);
        Assert.That(lines[0], Does.Contain($" C.M={snapshot.Sum(static c => c.Of(RunPath.Compiled, ThreadKind.Main).Parts)}/{compiledMainOps}/"));
        TableTotals tables = IlEvmStats.Tables();
        Assert.That(tables.Threads, Is.EqualTo(2));
        Assert.That(lines[0], Does.Contain($" runtime-compiled={IlEvmSettings.CompiledContracts} ").And.Contain($" tables={tables.Entries}/2 "));
        Assert.That(lines[0], Does.Contain($" handoffs={Nethermind.Blockchain.Metrics.PrewarmHandoffs}/{Nethermind.Blockchain.Metrics.PrewarmHandoffsRejected}/{Nethermind.Blockchain.Metrics.PrewarmHandoffsMissing} "));
        Assert.That(DumpTo(10).LogList, Is.Empty, "logged once");

        // Past the cut only contracts that ran compiled, at their place by time: none of the shipped ones is left out.
        IlEvmStats.Reset();
        byte[] busyInput = Bytes.FromHexString("000000000000000000000000000000000000000000000000000000000000001f");
        for (int i = 0; i < 20; i++)
            Run(_blockVm, new CodeInfo(LoopCode()), busyInput);
        Run(_blockVm, new CodeInfo(CallerCode()), []);
        lines = DumpTo(1).LogList;
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.That(lines, Has.Count.EqualTo(4), "the header, the top contract, the compiled one and the end line");
        snapshot = IlEvmStats.Snapshot();
        Assert.That(ContractLine.Match(lines[1]).Groups["hash"].Value, Is.EqualTo(snapshot.MaxBy(static c => c.Ticks)!.Hash.ToString()));
        Match compiled = ContractLine.Match(lines[2]);
        Assert.That(compiled.Groups["hash"].Value, Is.EqualTo(ValueKeccak.Compute(CallerCode()).ToString()));
        Assert.That(compiled.Groups["rank"].Value, Is.EqualTo((snapshot.FindIndex(static c => c.Hash == ValueKeccak.Compute(CallerCode())) + 1).ToString()));
    }

    [Test]
    public void A_snapshot_logs_the_totals_so_far_tagged_with_its_block_and_leaves_the_shutdown_dump_alone()
    {
        IlEvmSettings.Stats = true;
        Ship(CallerCode());
        IlEvmStats.BlockStarted();
        using (new BlockProcessing())
            Run(_blockVm, new CodeInfo(CallerCode()), []);

        Nethermind.Core.Test.TestLogger logger = new();
        IlEvmStats.DumpSnapshot(new ILogger(logger), 10, 2500);
        List<string> lines = logger.LogList;
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.That(lines[0], Does.StartWith("IL-EVM stats@2500: 2 contracts, 1 blocks, totals "));
        Assert.That(lines[^1], Is.EqualTo("IL-EVM stats@2500 end"));
        Assert.That(lines[1..^1], Has.Count.EqualTo(2));
        Assert.That(lines[1..^1], Has.All.StartWith("IL-EVM stats@2500 "));
        // Tagged lines are not the shutdown dump's: neither its line format nor the list parser takes them.
        Assert.That(lines.Any(static l => ContractLine.IsMatch(l)), Is.False);
        Assert.That(IlEvmPrecompileList.Parse(lines), Is.Empty);
        // The shutdown dump still logs once, after any number of snapshots.
        IlEvmStats.DumpSnapshot(new ILogger(new Nethermind.Core.Test.TestLogger()), 10, 5000);
        Assert.That(DumpTo(10).LogList, Has.Some.StartWith("IL-EVM stats: "));
    }

    [Test]
    public void Dump_with_profile_on_logs_the_profile_s_lines_for_the_list_tooling()
    {
        IlEvmSettings.Profile = true;
        IlEvmSettings.Stats = true;
        for (int i = 0; i < 3; i++)
            Run(_blockVm, new CodeInfo(CallerCode()), []);
        Outcome expected = Interpret(CallerCode(), []);

        List<string> lines = DumpTo(10).LogList;
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, lines));
        int profile = lines.FindIndex(static l => l.StartsWith("IL-EVM profile:", StringComparison.Ordinal));
        Assert.That(profile, Is.EqualTo(3), "after the stats' header and two contract lines");
        Assert.That(lines[profile], Is.EqualTo($"IL-EVM profile: 2 contracts, 6 frames, {3 * expected.OpCodes} interpreted opcodes"));
        List<string> profileLines = lines[(profile + 1)..^2];
        Assert.That(profileLines, Has.Count.EqualTo(2).And.All.Match(ProfileLine.ToString()));
        Assert.That(lines[^2], Is.EqualTo("IL-EVM profile end"));
        Assert.That(lines[^1], Is.EqualTo("IL-EVM stats end"));

        // Busiest by opcodes first: the callee's sixteen per run before the caller's ten.
        ValueHash256 callee = ValueKeccak.Compute(CalleeCode());
        ValueHash256 caller = ValueKeccak.Compute(CallerCode());
        Assert.That(profileLines[0], Does.StartWith($"IL-EVM profile 1 {callee} ops={3 * CalleeOps} frames=3 size={CalleeCode().Length} cum=0.6154"));
        Assert.That(profileLines[1], Does.StartWith($"IL-EVM profile 2 {caller} ops={3 * CallerOps} frames=3 size={CallerCode().Length} cum=1.0000"));

        // The tooling reads the whole log as the profile's lines alone, order and counts included.
        List<IlEvmPrecompileList.Entry> entries = IlEvmPrecompileList.ParseEntries(lines);
        Assert.That(entries, Is.EqualTo(IlEvmPrecompileList.ParseEntries(profileLines)));
        Assert.That(entries, Is.EqualTo(new IlEvmPrecompileList.Entry[]
        {
            new(callee, 3 * CalleeOps, 3, CalleeCode().Length),
            new(caller, 3 * CallerOps, 3, CallerCode().Length),
        }));
    }

    [Test]
    public void Stats_change_no_outcome_and_take_no_self_check_verdict()
    {
        // The query runners compile on the first frame here, as they compile hot code in production.
        IlEvmSettings.Threshold = 1;
        IlEvmSettings.CompileSynchronously = true;
        IlEvmSettings.Stats = true;
        IlEvmDifferentialTests.Shape[] shapes = [.. IlEvmDifferentialTests.Shapes().Where(static s => s.Size == "small" &&
            s.Name is "ADD" or "MULMOD" or "MSTORE-word" or "KECCAK256" or "SLOAD" or "CALLDATALOAD" or "JUMPI-taken" or "verifier-loop" or "dispatch-last" or "dispatch-first")];
        Assert.That(shapes, Has.Length.EqualTo(10));
        foreach (IlEvmDifferentialTests.Shape shape in shapes)
        {
            // The two dispatch shapes run the same code.
            IlEvmStats.Reset();
            CodeInfo codeInfo = new(shape.Code);
            foreach (ulong gas in new ulong[] { shape.Gas, 60_000, 21_007 })
            {
                Outcome expected = Interpret(shape.Code, shape.Input, gas);
                Outcome plain = Run(_plainQueryVm, codeInfo, shape.Input, gas);
                Outcome timed = Run(_queryVm, codeInfo, shape.Input, gas);
                Outcome plainBlock = Run(_plainBlockVm, codeInfo, shape.Input, gas);
                IlEvmSettings.Profile = true;
                Outcome profiled = Run(_queryVm, new CodeInfo(shape.Code), shape.Input, gas);
                IlEvmSettings.Profile = false;
                Assert.That(plain, Is.EqualTo(expected), $"{shape} at gas {gas}");
                Assert.That(timed, Is.EqualTo(expected), $"{shape} at gas {gas}, timed");
                Assert.That(plainBlock, Is.EqualTo(expected), $"{shape} at gas {gas}, block runner");
                Assert.That(profiled, Is.EqualTo(expected), $"{shape} at gas {gas}, profiled");
            }

            Assert.That(IlEvm.IsCompiled(codeInfo), Is.True, $"{shape}: {IlEvm.FailureReason(codeInfo)}");
            ContractTotals totals = Totals(shape.Code);
            Assert.That(totals.Of(RunPath.Compiled, ThreadKind.Other).Parts + totals.Of(RunPath.HandedBack, ThreadKind.Other).Parts, Is.EqualTo(3), $"{shape}: timed on compiled code");
            Assert.That(totals.Of(RunPath.Interpreted, ThreadKind.Other).Parts, Is.EqualTo(3), $"{shape}: timed on the interpreter while profiling");
        }

        // A self-check that would decide within a few hundred frames, on code of these tests' own.
        IlEvmSettings.SelfCheck = true;
        IlEvmSettings.SelfCheckDelayMs = 0;
        IlEvmSettings.SelfCheckWarmUpMs = 0;
        IlEvmSettings.SelfCheckRecheckMs = 0;
        IlEvmSettings.SelfCheckEarlyRecheckMs = 0;
        IlEvmSettings.SelfCheckKeepRatio = 1e-6;
        IlEvmDifferentialTests.Shape loop = IlEvmDifferentialTests.Shapes().First(static s => s.Name == "NOT-copy" && s.Size == "small");
        CodeInfo timedCode = new(Unique(loop.Code, 0x31));
        for (int frame = 0; frame < 2_000; frame++)
            Run(_queryVm, timedCode, loop.Input, 100_000);
        Assert.That(IlEvm.SelfCheckOf(timedCode), Is.EqualTo(("undecided", 0.0, 0)), "under Stats compiled code runs every frame");
        Assert.That(Totals(Unique(loop.Code, 0x31)).Of(RunPath.Compiled, ThreadKind.Other).Parts, Is.EqualTo(2_000));

        // The production runner, with the same check, samples the interpreter at once, and decides.
        CodeInfo checkedCode = new(Unique(loop.Code, 0x32));
        for (int frame = 0; frame < 2_000 && IlEvm.SelfCheckOf(checkedCode) is null or { Verdict: "undecided" }; frame++)
            Run(_plainQueryVm, checkedCode, loop.Input, 100_000);
        Assert.That(IlEvm.SelfCheckOf(checkedCode)?.Verdict, Is.Not.EqualTo("undecided"));
    }

    /// <summary><paramref name="code"/> with bytes no frame reaches after it, so it is compiled and checked apart from every other copy.</summary>
    private static byte[] Unique(byte[] code, byte tag) => [.. code, (byte)Instruction.INVALID, 0x5a, tag];

    [Test]
    public void Only_frames_the_lookup_counted_feed_hotness_under_stats()
    {
        IlEvmSettings.Stats = true;
        IlEvmSettings.HotnessSampling = 8;
        CodeInfo codeInfo = new(LoopCode());
        int counted = 0, skipped = 0;
        for (int frame = 0; frame < 200; frame++)
        {
            IlEvm.IlCodeState? before = IlEvmFrameRunner.CodeSlot(codeInfo) as IlEvm.IlCodeState;
            (int executions, int opCodes) = (before?.Executions ?? 0, before?.OpCodesRun ?? 0);
            Run(_queryVm, codeInfo, LoopInput);
            IlEvm.IlCodeState state = (IlEvm.IlCodeState)IlEvmFrameRunner.CodeSlot(codeInfo)!;
            if (state.Executions == executions)
            {
                skipped++;
                Assert.That(state.OpCodesRun, Is.EqualTo(opCodes), $"frame {frame} was not counted, and adds nothing to hotness");
            }
            else
            {
                counted++;
                Assert.That(state.OpCodesRun, Is.GreaterThan(opCodes), $"frame {frame} was counted, and adds its opcodes");
            }
        }

        Assert.That(IlEvm.IsCompiled(codeInfo), Is.False);
        Assert.That(counted, Is.GreaterThan(2), "the first two frames and a sample of the rest");
        Assert.That(skipped, Is.GreaterThan(100), "most frames past the second are not sampled");
        Assert.That(Totals(LoopCode()).Of(RunPath.Interpreted, ThreadKind.Other).Parts, Is.EqualTo(200), "every frame is timed, counted or not");
    }

    [Test]
    public void Block_processing_runs_compiled_only_what_was_shipped()
    {
        // A read-only query compiles a loop at run time (code of this test's own: the compiled cache is by content); the
        // production block runner takes that contract up.
        IlEvmSettings.Stats = true;
        IlEvmSettings.Threshold = 1;
        IlEvmSettings.CompileSynchronously = true;
        Ship(CallerCode());
        byte[] loop = Unique(LoopCode(), 0x61);
        CodeInfo codeInfo = new(loop);
        Outcome expected = Interpret(loop, LoopInput);
        Assert.That(Run(_queryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(Run(_queryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(IlEvm.IsCompiled(codeInfo), Is.True, IlEvm.FailureReason(codeInfo));
        Assert.That(IlEvm.FindPrecompiledOnly(codeInfo, IlEvm.BodySetFor(_spec)!), Is.Not.Null);

        // A measurement run's block runner does not, so two runs differ only by their lists.
        IlEvmStats.Reset();
        using (new BlockProcessing())
        {
            Assert.That(Run(_blockVm, codeInfo, LoopInput), Is.EqualTo(expected));
            Assert.That(Run(_blockVm, new CodeInfo(CallerCode()), []), Is.EqualTo(Interpret(CallerCode(), [])));
        }

        Assert.That(PathsOf(Totals(loop)), Is.EqualTo(new[] { (RunPath.Interpreted, ThreadKind.Main) }));
        Assert.That(PathsOf(Totals(CallerCode())), Is.EqualTo(new[] { (RunPath.Compiled, ThreadKind.Main) }));
    }

    [Test]
    public void Module_attaches_the_timed_runners_only_for_stats()
    {
        Assume.That(IlEvmModule.Unsupported(), Is.Null);
        foreach ((bool stats, IlEvmScope scope) in new[] { (false, IlEvmScope.All), (true, IlEvmScope.All), (true, IlEvmScope.Rpc) })
        {
            IlEvmSettings.SelfCheck = true;
            ContainerBuilder builder = new();
            builder.RegisterModule(new IlEvmModule(new IlEvmConfig { Enabled = true, Scope = scope, Stats = stats }));
            using IContainer container = builder.Build();
            string name = $"stats {stats}, scope {scope}";
            FrameCodeRunner<EthereumGasPolicy>? blockRunner = scope != IlEvmScope.All ? null : stats ? IlEvmTimedFrameRunner.Block : IlEvmFrameRunner.Block;
            FrameCodeRunner<EthereumGasPolicy> queryRunner = stats ? IlEvmTimedFrameRunner.Instance : IlEvmFrameRunner.Instance;
            Assert.That(container.ResolveOptional<FrameCodeRunner<EthereumGasPolicy>>(), Is.SameAs(blockRunner), name);
            IlEvmQueryModule query = (IlEvmQueryModule)container.Resolve<IReadOnlyQueryModule>();
            Assert.That(query.Runner, Is.SameAs(queryRunner), name);
            ContainerBuilder queryBuilder = new();
            queryBuilder.RegisterModule(query);
            using IContainer queryContainer = queryBuilder.Build();
            Assert.That(queryContainer.Resolve<FrameCodeRunner<EthereumGasPolicy>>(), Is.SameAs(query.Runner), name);
            Assert.That(IlEvmSettings.Stats, Is.EqualTo(stats), name);
            Assert.That(IlEvmSettings.SelfCheck, Is.EqualTo(!stats), $"{name}: a measurement run turns the self-check off");
        }
    }

    [Test]
    public void Candidates_list_loads_and_the_shipped_list_is_unchanged()
    {
        Assume.That(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST"), Is.Null.Or.Empty, "an override replaces both lists");
        List<IlEvmPrecompileList.Entry> candidates = IlEvmPrecompileList.LoadEntries(IlEvmPrecompileSource.Candidates, out string origin);
        Assert.That(origin, Is.EqualTo("Candidates"));
        Assert.That(candidates, Has.Count.GreaterThanOrEqualTo(500));
        Assert.That(candidates.All(static e => e.Ops > 0 && e.Frames > 0 && e.Size > 0), Is.True, "every candidate carries the profile's counts");
        Assert.That(IlEvmPrecompileList.Load(IlEvmPrecompileSource.Candidates), Is.EqualTo(candidates.Select(static e => e.Hash)));

        List<ValueHash256> shipped = IlEvmPrecompileList.Load(IlEvmPrecompileSource.Shipped);
        Assert.That(shipped, Has.Count.EqualTo(300));
        Assert.That(IlEvmPrecompileList.Load(), Is.EqualTo(shipped));
        Assert.That(IlEvmPrecompileList.Parse(File.ReadLines(ListFile("Precompile.txt"))), Is.EqualTo(shipped), "the shipped list is read as it always was");
        Assert.That(candidates, Has.Count.GreaterThan(shipped.Count), "the candidates are the wider list");
        Assert.That(shipped, Is.SubsetOf(candidates.Select(static e => e.Hash)), "from the same profile");
    }

    /// <summary>A list file of the plugin's sources.</summary>
    private static string ListFile(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "Nethermind.IlEvm", name);
            if (File.Exists(path))
                return path;
        }

        Assert.Ignore($"{name} not found above {AppContext.BaseDirectory}");
        return null!;
    }

    [Test]
    public void Min_opcodes_per_frame_skips_entries_by_their_counts_and_keeps_those_without()
    {
        string Hash(char c) => "0x" + new string(c, 64);
        List<IlEvmPrecompileList.Entry> entries = IlEvmPrecompileList.ParseEntries([
            $"{Hash('a')} ops=1000 frames=10 size=99",
            $"2026-10-13 12:00:00.0000|IL-EVM profile 2 {Hash('b')} ops=50 frames=10 size=10 cum=0.5000",
            Hash('c'),
            $"{Hash('d')} ops=7",
            $"{Hash('e')} ops=7 frames=0",
            $"{Hash('f')} frames=x ops=200 frames=10",
            // A stats dump's line is no list entry.
            $"2026-10-13 12:00:00.0000|IL-EVM stats 1 {Hash('9')} size=10 entries=1 blocks=0 shipped=0 I.O=1/1/100 hI.O=0:1/1/100/100",
        ]);
        Assert.That(entries.Select(static e => e.OpsPerFrame), Is.EqualTo(new double?[] { 100, 5, null, null, null, 20 }));
        Assert.That(entries[0], Is.EqualTo(new IlEvmPrecompileList.Entry(new ValueHash256(Hash('a')), 1000, 10, 99)));

        List<IlEvmPrecompileList.Entry> kept = IlEvmPrecompileList.WithMinOpsPerFrame(entries, 20, out int skipped);
        Assert.That(skipped, Is.EqualTo(1));
        Assert.That(kept.Select(static e => e.Hash), Is.EqualTo(new[] { Hash('a'), Hash('c'), Hash('d'), Hash('e'), Hash('f') }.Select(static h => new ValueHash256(h))));
        Assert.That(IlEvmPrecompileList.WithMinOpsPerFrame(entries, 0, out skipped), Is.EqualTo(entries));
        Assert.That(skipped, Is.EqualTo(0));
        Assert.That(IlEvmPrecompileList.WithMinOpsPerFrame(entries, 101, out skipped).Select(static e => e.Hash),
            Is.EqualTo(new[] { Hash('c'), Hash('d'), Hash('e') }.Select(static h => new ValueHash256(h))));
        Assert.That(skipped, Is.EqualTo(3));

        // On the candidates, a bar that some entries miss.
        List<IlEvmPrecompileList.Entry> candidates = IlEvmPrecompileList.LoadEntries(IlEvmPrecompileSource.Candidates, out _);
        double median = candidates.Select(static e => e.OpsPerFrame!.Value).Order().ElementAt(candidates.Count / 2);
        List<IlEvmPrecompileList.Entry> heavy = IlEvmPrecompileList.WithMinOpsPerFrame(candidates, (int)Math.Ceiling(median), out skipped);
        Assert.That(skipped, Is.InRange(1, candidates.Count - 1));
        Assert.That(heavy.All(e => e.OpsPerFrame >= Math.Ceiling(median)), Is.True);
        Assert.That(heavy.Select(static e => e.Hash), Is.EqualTo(candidates.Select(static e => e.Hash).Where(h => heavy.Any(k => k.Hash == h))), "in list order");
    }

    private sealed class ExitSource(CancellationToken token) : IProcessExitSource
    {
        public void Exit(int exitCode) { }
        public CancellationToken Token => token;
    }

    private sealed class OneLogger(Nethermind.Core.Test.TestLogger logger) : ILogManager
    {
        public ILogger GetClassLogger<T>() => new(logger);
        public ILogger GetLogger(string loggerName) => new(logger);
    }

    [Test]
    public void Startup_step_counts_blocks_warns_of_the_handoff_and_dumps_at_exit()
    {
        IlEvmSettings.Stats = true;
        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        IMainProcessingContext context = Substitute.For<IMainProcessingContext>();
        context.BranchProcessor.Returns(branchProcessor);
        using CancellationTokenSource exit = new();
        Nethermind.Core.Test.TestLogger logger = new();
        IlEvmStartupStep step = new(new IlEvmConfig { Enabled = true, Stats = true, Precompile = false, ProfileTop = 5, Scope = IlEvmScope.All }, null!, null!, null!,
            new Lazy<IMainProcessingContext>(() => context), new BlocksConfig(), new ExitSource(exit.Token), new OneLogger(logger));
        step.Execute(default).Wait();
        Assert.That(logger.LogList, Has.One.StartsWith("IL-EVM timing: every contract's frame parts are timed").And.Contains("in block processing").And.Contains("self-check is off"));
        Assert.That(logger.LogList, Has.One.StartsWith("IL-EVM timing: Blocks.PreWarmHandoff is on"), "the handoff is on by default");

        Block block = Build.A.Block.WithNumber(1).TestObject;
        OnThread(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                branchProcessor.BlockProcessing += Raise.EventWith(branchProcessor, new BlockEventArgs(block));
                Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
            }
        }, blockProcessing: true);
        Run(_blockVm, new CodeInfo(LoopCode()), LoopInput);
        Assert.That(IlEvmStats.Blocks, Is.EqualTo(3));
        ContractTotals totals = Totals(LoopCode());
        Assert.That(totals.Of(RunPath.Interpreted, ThreadKind.Main).Parts, Is.EqualTo(3), "block processing's parts");
        Assert.That(totals.Of(RunPath.Interpreted, ThreadKind.Other).Parts, Is.EqualTo(1));
        Assert.That(totals.Blocks, Is.EqualTo(3));

        Assert.That(logger.LogList.Any(static l => l.StartsWith("IL-EVM stats", StringComparison.Ordinal)), Is.False, "nothing before exit");
        exit.Cancel();
        List<string> lines = logger.LogList.Where(static l => l.StartsWith("IL-EVM stats", StringComparison.Ordinal)).ToList();
        Assert.That(lines[0], Does.StartWith("IL-EVM stats: 1 contracts, 3 blocks, totals I.M=3/"));
        Assert.That(lines[1], Does.StartWith($"IL-EVM stats 1 {ValueKeccak.Compute(LoopCode())} size={LoopCode().Length} entries=4 blocks=3 shipped=0 I.M=3/"));
        Assert.That(lines[^1], Is.EqualTo("IL-EVM stats end"));
        Assert.That(logger.LogList.Any(static l => l.StartsWith("IL-EVM profile", StringComparison.Ordinal)), Is.False);

        // No warning with the handoff off, nor where block processing is not timed.
        foreach ((IlEvmScope scope, bool handoff, string timed) in new[] { (IlEvmScope.All, false, "in block processing"), (IlEvmScope.Rpc, true, "in read-only queries only") })
        {
            Nethermind.Core.Test.TestLogger quiet = new();
            using CancellationTokenSource quietExit = new();
            new IlEvmStartupStep(new IlEvmConfig { Enabled = true, Stats = true, Precompile = false, Scope = scope }, null!, null!, null!,
                new Lazy<IMainProcessingContext>(() => context), new BlocksConfig { PreWarmHandoff = handoff }, new ExitSource(quietExit.Token), new OneLogger(quiet)).Execute(default).Wait();
            Assert.That(quiet.LogList, Has.One.StartsWith("IL-EVM timing: every contract's frame parts are timed").And.Contains(timed), $"{scope}");
            Assert.That(quiet.LogList.Any(static l => l.Contains("PreWarmHandoff", StringComparison.Ordinal)), Is.False, $"{scope}, handoff {handoff}");
        }
    }

    [Test]
    public void Startup_compile_reads_the_chosen_list_skips_light_entries_and_runs_under_stats()
    {
        Assume.That(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST"), Is.Null.Or.Empty, "an override replaces both lists");
        using IDisposable dbProvider = (IDisposable)TestMemDbProvider.Init();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(5).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(_spec);
        IMainProcessingContext context = Substitute.For<IMainProcessingContext>();
        context.BranchProcessor.Returns(Substitute.For<IBranchProcessor>());

        List<string> Precompile(IlEvmConfig config)
        {
            Nethermind.Core.Test.TestLogger logger = new();
            using CancellationTokenSource exit = new();
            IlEvmStartupStep step = new(config, (IDbProvider)dbProvider, blockTree, specProvider,
                new Lazy<IMainProcessingContext>(() => context), new BlocksConfig(), new ExitSource(exit.Token), new OneLogger(logger));
            step.Execute(default).Wait();
            return logger.LogList.Where(static l => l.StartsWith("IL-EVM precompile:", StringComparison.Ordinal)).ToList();
        }

        // The candidates, none of them in this empty code database.
        List<IlEvmPrecompileList.Entry> candidates = IlEvmPrecompileList.LoadEntries(IlEvmPrecompileSource.Candidates, out _);
        IlEvmPrecompileList.WithMinOpsPerFrame(candidates, 2_000, out int light);
        Assume.That(light, Is.InRange(1, candidates.Count - 1));
        List<string> lines = Precompile(new IlEvmConfig { Enabled = true, PrecompileList = IlEvmPrecompileSource.Candidates, PrecompileMinOpsPerFrame = 2_000 });
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.StartWith($"IL-EVM precompile: 0 of {candidates.Count - light} shipped contracts compiled for the spec of block 6 " +
            $"(list Candidates, {light} of {candidates.Count} entries under 2000 opcodes per frame skipped; {candidates.Count - light} not in the code database, 0 declined"));
        Assert.That(Precompile(new IlEvmConfig { Enabled = true })[0],
            Does.StartWith("IL-EVM precompile: 0 of 300 shipped contracts compiled for the spec of block 6 (list Shipped; 300 not in the code database, 0 declined"));

        // A list file names code that is there: the entries under the bar are not compiled, those without counts are.
        IDb codeDb = ((IDbProvider)dbProvider).CodeDb;
        byte[][] codes = [Unique(LoopCode(), 0x41), Unique(LoopCode(), 0x42), Unique(LoopCode(), 0x43)];
        foreach (byte[] code in codes)
        {
            ValueHash256 hash = ValueKeccak.Compute(code);
            codeDb[hash.Bytes] = code;
        }
        string list = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"ilevm-stats-list-{Environment.ProcessId}.txt");
        File.WriteAllLines(list, [
            $"{ValueKeccak.Compute(codes[0])} ops=500 frames=10",
            $"{ValueKeccak.Compute(codes[1])} ops=50 frames=10",
            $"{ValueKeccak.Compute(codes[2])}",
        ]);
        Environment.SetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST", list);
        try
        {
            lines = Precompile(new IlEvmConfig { Enabled = true, PrecompileList = IlEvmPrecompileSource.Candidates, PrecompileMinOpsPerFrame = 20 });
            Assert.That(lines[0], Does.StartWith($"IL-EVM precompile: 2 of 2 shipped contracts compiled for the spec of block 6 (list {list}, 1 of 3 entries under 20 opcodes per frame skipped; 0 not in the code database, 0 declined"));
            Assert.That(IlEvm.PrecompiledCount, Is.EqualTo(2));
            Assert.That(IlEvm.FindPrecompiledOnly(new CodeInfo(codes[0]), IlEvm.BodySetFor(_spec)!), Is.Not.Null);
            Assert.That(IlEvm.FindPrecompiledOnly(new CodeInfo(codes[1]), IlEvm.BodySetFor(_spec)!), Is.Null, "under the bar");
            Assert.That(IlEvm.FindPrecompiledOnly(new CodeInfo(codes[2]), IlEvm.BodySetFor(_spec)!), Is.Not.Null, "no counts to judge it by");

            // A measurement run compiles its list at startup as well; only profiling compiles nothing.
            IlEvm.ClearPrecompiled();
            IlEvmSettings.Stats = true;
            lines = Precompile(new IlEvmConfig { Enabled = true, Stats = true, Scope = IlEvmScope.All });
            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines[0], Does.StartWith("IL-EVM precompile: 3 of 3 shipped contracts compiled"));
            Assert.That(IlEvm.PrecompiledCount, Is.EqualTo(3));
            IlEvm.ClearPrecompiled();
            IlEvmSettings.Profile = true;
            Assert.That(Precompile(new IlEvmConfig { Enabled = true, Stats = true, Profile = true, Scope = IlEvmScope.All }), Is.Empty);
            Assert.That(IlEvm.PrecompiledCount, Is.EqualTo(0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETHERMIND_ILEVM_PRECOMPILE_LIST", null);
            File.Delete(list);
        }
    }
}
