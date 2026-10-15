// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Threading;
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
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>The plugin's static settings and the runtime's switches, as they were when made, restored when disposed.</summary>
/// <remarks>For tests that change them, or that load the module, which sets them all.</remarks>
internal sealed class IlEvmSettingsScope : IDisposable
{
    private readonly bool _enabled = IlEvmSettings.Enabled, _sync = IlEvmSettings.CompileSynchronously, _selfCheck = IlEvmSettings.SelfCheck,
        _profile = IlEvmSettings.Profile, _stats = IlEvmSettings.Stats, _blockCompile = IlEvmSettings.BlockRuntimeCompile,
        _blockSelfCheck = IlEvmSettings.BlockSelfCheck, _forced = IlEvmSettings.ScopeForcedToAll, _gate = IlEvmSettings.Gate,
        _queryCheck = IlEvmSettings.QueryCheck, _blockCheck = IlEvmSettings.BlockCheck, _declineAll = IlEvmSettings.BlockDeclineAll,
        _queryCounting = IlRuntime.QueryCounting, _blocksCounting = IlRuntime.BlocksCounting;
    private readonly int _threshold = IlEvmSettings.Threshold, _hotRate = IlEvmSettings.HotOpcodesPerSecond, _blockThreads = IlRuntime.BlockThreadsMask,
        _maxProfiles = IlRuntime.MaxProfiles, _chunkThreshold = IlEvmSettings.ChunkThreshold, _chunkInstructions = IlEvmSettings.ChunkInstructions;
    private readonly long _opcodes = IlEvmSettings.OpcodeThreshold, _maxRetained = IlRuntime.MaxRetainedBytes;
    private readonly IlMode _mode = IlEvmSettings.Mode;
    private readonly IlEvmPrefer _prefer = IlEvmSettings.Prefer;
    private readonly IlEvmScope _scope = IlEvmSettings.Scope;
    private readonly IlGateParams _blockGate = IlGate.Block, _queryGate = IlGate.Query;
    private readonly bool _chaos = IlGate.Chaos, _eagerChunks = IlEvmSettings.EagerChunks;
    private readonly int _checkDelay = IlEvmSettings.SelfCheckDelayMs, _warmUp = IlEvmSettings.SelfCheckWarmUpMs, _recheck = IlEvmSettings.SelfCheckRecheckMs,
        _earlyRecheck = IlEvmSettings.SelfCheckEarlyRecheckMs, _trialShare = IlCheck.TrialShare, _keptShare = IlCheck.KeptShare, _blockShare = IlCheck.BlockShare,
        _blockRecheck = IlCheck.BlockRecheckMs, _maskedRecheck = IlCheck.MaskedRecheckMs;
    private readonly double _keepRatio = IlCheck.KeepRatio, _rollingLoss = IlCheck.RollingLossRatio;
    private readonly bool _manualTicks = IlCheckClock.ManualTicks, _inBranch = IlRuntime.InBranch, _schedulerRestores = IlCheck.SchedulerRestores,
        _manualScheduler = IlScheduler.ManualForTests;
    private readonly Action<IlEvm.IlContract> _releaseLive = IlCheck.ReleaseLive;
    private readonly IlLiveParams _liveRpc = IlLive.Rpc, _liveBlocks = IlLive.Blocks;
    private readonly long _liveBudget = IlLive.BudgetBytes;
    private readonly double _tickBudget = IlLive.TickBudgetMs;
    private readonly int _manualRpcEpoch = IlRuntime.ManualRpcEpoch, _querySample = IlGate.QuerySampleEvery, _blockSample = IlGate.BlockSampleEvery;
    private readonly int _aheadMax = IlWarmUp.AheadMaxMs, _aheadOwnMask = IlWarmUp.AheadOwnMask;
    private readonly bool _aheadManual = IlWarmUp.AheadManualForTests;

    public void Dispose()
    {
        IlEvmSettings.Enabled = _enabled;
        IlEvmSettings.CompileSynchronously = _sync;
        IlEvmSettings.SelfCheck = _selfCheck;
        IlEvmSettings.Profile = _profile;
        IlEvmSettings.Stats = _stats;
        IlEvmSettings.BlockRuntimeCompile = _blockCompile;
        IlEvmSettings.BlockSelfCheck = _blockSelfCheck;
        IlEvmSettings.ScopeForcedToAll = _forced;
        IlEvmSettings.Gate = _gate;
        IlEvmSettings.QueryCheck = _queryCheck;
        IlEvmSettings.BlockCheck = _blockCheck;
        IlEvmSettings.BlockDeclineAll = _declineAll;
        IlEvmSettings.Threshold = _threshold;
        IlEvmSettings.HotOpcodesPerSecond = _hotRate;
        IlEvmSettings.OpcodeThreshold = _opcodes;
        IlEvmSettings.Mode = _mode;
        IlEvmSettings.Prefer = _prefer;
        IlEvmSettings.Scope = _scope;
        IlRuntime.QueryCounting = _queryCounting;
        IlRuntime.BlocksCounting = _blocksCounting;
        IlRuntime.BlockThreadsMask = _blockThreads;
        IlRuntime.MaxProfiles = _maxProfiles;
        IlRuntime.MaxRetainedBytes = _maxRetained;
        IlEvmSettings.ChunkThreshold = _chunkThreshold;
        IlEvmSettings.ChunkInstructions = _chunkInstructions;
        IlEvmSettings.EagerChunks = _eagerChunks;
        IlGate.Block = _blockGate;
        IlGate.Query = _queryGate;
        IlGate.Chaos = _chaos;
        IlEvmSettings.SelfCheckDelayMs = _checkDelay;
        IlEvmSettings.SelfCheckWarmUpMs = _warmUp;
        IlEvmSettings.SelfCheckRecheckMs = _recheck;
        IlEvmSettings.SelfCheckEarlyRecheckMs = _earlyRecheck;
        IlCheck.TrialShare = _trialShare;
        IlCheck.KeptShare = _keptShare;
        IlCheck.BlockShare = _blockShare;
        IlCheck.BlockRecheckMs = _blockRecheck;
        IlCheck.MaskedRecheckMs = _maskedRecheck;
        IlCheck.KeepRatio = _keepRatio;
        IlCheck.RollingLossRatio = _rollingLoss;
        IlCheck.SchedulerRestores = _schedulerRestores;
        IlCheck.ReleaseLive = _releaseLive;
        IlCheckClock.ManualTicks = _manualTicks;
        IlRuntime.InBranch = _inBranch;
        IlScheduler.ManualForTests = _manualScheduler;
        IlLive.Rpc = _liveRpc;
        IlLive.Blocks = _liveBlocks;
        IlLive.BudgetBytes = _liveBudget;
        IlLive.TickBudgetMs = _tickBudget;
        IlRuntime.ManualRpcEpoch = _manualRpcEpoch;
        IlGate.QuerySampleEvery = _querySample;
        IlGate.BlockSampleEvery = _blockSample;
        IlWarmUp.AheadMaxMs = _aheadMax;
        IlWarmUp.AheadOwnMask = _aheadOwnMask;
        IlWarmUp.AheadManualForTests = _aheadManual;
    }
}

/// <summary>
/// Machines with each production runner on one world state, under the Adaptive rules, with nothing shipped and no
/// runner counting at the start of each test, and the visit gate off: every part of a contract a runner may run runs
/// compiled, as the lookup's tests count. The gate's own tests turn it on.
/// </summary>
public abstract class IlEvmAdaptiveFixture
{
    protected static readonly Address Caller = TestItem.AddressA;
    protected static readonly Address Executing = TestItem.AddressB;

    /// <summary>An account with <see cref="CalleeCode"/>: what callers call, so each call ends one of the caller's frame parts.</summary>
    protected static readonly Address Callee = TestItem.AddressC;

    /// <summary>MSTORE of 1, then RETURN of that word: PUSH1 1, PUSH1 0, MSTORE, PUSH1 32, PUSH1 0, RETURN.</summary>
    protected static byte[] CalleeCode() => Bytes.FromHexString("600160005260206000f3");

    /// <summary>An account with <see cref="LoopCalleeCode"/>, which routers call with their calldata word.</summary>
    protected static readonly Address LoopCallee = TestItem.AddressE;

    /// <summary>Counts calldata's first word down to zero and stops, in one part: 10 opcodes a turn and 10 more.</summary>
    protected static byte[] LoopCalleeCode() => Bytes.FromHexString("6000355b8015601057600190036003565b5000");

    /// <summary>
    /// A router: MSTOREs its calldata word, STATICCALLs <see cref="LoopCallee"/> with it, and stops; a part of 11 opcodes
    /// and one of 2. Unique by <paramref name="tag"/>.
    /// </summary>
    protected static byte[] RouterCode(byte tag) =>
        Unique([.. Bytes.FromHexString("600035600052600060006020600073"), .. LoopCallee.Bytes, .. Bytes.FromHexString("5afa5000")], tag);

    protected IReleaseSpec Spec { get; private set; } = null!;
    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private readonly List<EthereumVirtualMachine> _extraMachines = [];
    private IWorldState _state = null!;
    private IDisposable? _stateScope;
    private IlEvmSettingsScope? _settings;

    /// <summary>Block processing's machine, with the runner Scope=All attaches.</summary>
    protected EthereumVirtualMachine BlockVm { get; private set; } = null!;

    /// <summary>A read-only query's machine.</summary>
    protected EthereumVirtualMachine QueryVm { get; private set; } = null!;

    /// <summary>A read-only query's machine on a block of Prague, whose body set is not Osaka's.</summary>
    protected EthereumVirtualMachine PragueQueryVm { get; private set; } = null!;

    /// <summary>A machine with no runner attached: the interpreter alone, for timing the hook against.</summary>
    protected EthereumVirtualMachine PlainVm { get; private set; } = null!;

    [OneTimeSetUp]
    public void CreateMachines()
    {
        Spec = Osaka.Instance;
        _container = new ContainerBuilder().AddModule(new TestNethermindModule(Spec)).Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _scope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        _state = _scope.Resolve<IWorldState>();
        EthereumVirtualMachine Machine(FrameCodeRunner<EthereumGasPolicy>? runner) =>
            new(_scope.Resolve<IBlockhashProvider>(), _scope.Resolve<ISpecProvider>(), LimboLogs.Instance, runner);
        BlockVm = Machine(IlEvmFrameRunner.Block);
        QueryVm = Machine(IlEvmFrameRunner.Instance);
        PragueQueryVm = Machine(IlEvmFrameRunner.Instance);
        PlainVm = Machine(null);
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(Caller, 1_000_000);
        _state.CreateAccount(Executing, 1_000);
        _state.CreateAccount(Callee, 0);
        _state.InsertCode(Callee, CalleeCode(), Spec);
        _state.CreateAccount(LoopCallee, 0);
        _state.InsertCode(LoopCallee, LoopCalleeCode(), Spec);
        _state.Commit(Spec);
        _state.CommitTree(0);
        foreach ((EthereumVirtualMachine vm, IReleaseSpec spec) in new[] { (BlockVm, Spec), (QueryVm, Spec), (PragueQueryVm, (IReleaseSpec)Prague.Instance), (PlainVm, Spec) })
        {
            vm.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.WithNumber(7).WithTimestamp(1234).TestObject, spec));
            vm.SetTxExecutionContext(new TxExecutionContext(Caller, _scope.Resolve<ICodeInfoRepository>(), null, 3));
        }
    }

    /// <summary>Another read-only query's machine on Osaka, as a thread of its own would have; disposed with the fixture's.</summary>
    protected EthereumVirtualMachine NewQueryMachine()
    {
        EthereumVirtualMachine vm = new(_scope.Resolve<IBlockhashProvider>(), _scope.Resolve<ISpecProvider>(), LimboLogs.Instance, IlEvmFrameRunner.Instance);
        vm.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.WithNumber(7).WithTimestamp(1234).TestObject, Spec));
        vm.SetTxExecutionContext(new TxExecutionContext(Caller, _scope.Resolve<ICodeInfoRepository>(), null, 3));
        _extraMachines.Add(vm);
        return vm;
    }

    [OneTimeTearDown]
    public void DisposeMachines()
    {
        _stateScope?.Dispose();
        foreach (EthereumVirtualMachine? vm in new[] { BlockVm, QueryVm, PragueQueryVm, PlainVm })
            vm?.Dispose();
        foreach (EthereumVirtualMachine vm in _extraMachines)
            vm.Dispose();
        _scope?.Dispose();
        _container?.Dispose();
    }

    [SetUp]
    public void AdaptiveRules()
    {
        _settings = new IlEvmSettingsScope();
        IlEvm.ClearPrecompiled();
        IlShippedBacklog.Reset();
        IlWarmUp.ResetAheadForTests();
        IlWarmUp.AheadManualForTests = true;
        IlEvmSettings.Enabled = true;
        IlEvmSettings.Mode = IlMode.Adaptive;
        IlEvmSettings.SelfCheck = false;
        IlEvmSettings.Gate = false;
        IlEvmSettings.BlockDeclineAll = false;
        IlRuntime.QueryCounting = false;
        IlRuntime.BlocksCounting = false;
        IlRuntime.InBranch = false;
        IlRuntime.BlockThreadsMask = IlRuntime.RunMain | IlRuntime.RunWorkers;
        // A counting runner samples every transaction, so counts are exact; the tests of sampling set its rates.
        IlGate.QuerySampleEvery = 1;
        IlGate.BlockSampleEvery = 1;
        // A startup step a test runs attaches a scheduler whose workers the test pumps, if it pumps them at all.
        IlScheduler.ManualForTests = true;
    }

    [TearDown]
    public void RestoreRules()
    {
        _settings?.Dispose();
        IlEvm.ClearPrecompiled();
        IlShippedBacklog.Reset();
        IlWarmUp.ResetAheadForTests();
        ProcessingThread.IsBlockProcessingThread = false;
    }

    /// <summary>A loop that stores <c>calldata[0] &amp; 31</c> words, then returns the first: jumps, memory and storage.</summary>
    protected static byte[] LoopCode() => Bytes.FromHexString("600035601f165b8015601657600190038080526006565b5060205ff3");

    protected static readonly byte[] LoopInput = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000007");

    /// <summary>ADD and MUL over calldata, SSTORE of the result, then RETURN of it.</summary>
    protected static byte[] ArithmeticCode() => Bytes.FromHexString("6000356020350160403502806000556000526020" + "6000f3");

    protected static readonly byte[] ArithmeticInput = Bytes.FromHexString(
        "00000000000000000000000000000000000000000000000000000000000000ff" + "0000000000000000000000000000000000000000000000000000000000000003" +
        "0000000000000000000000000000000000000000000000000000000000000005");

    /// <summary>Initcode that deploys the one-byte code STOP: PUSH1 0, PUSH1 0, MSTORE8, PUSH1 1, PUSH1 0, RETURN.</summary>
    protected static byte[] InitCode() => Bytes.FromHexString("600060005360016000f3");

    /// <summary><paramref name="code"/> with bytes no frame reaches after it, so each test's code is its own.</summary>
    protected static byte[] Unique(byte[] code, byte tag) => [.. code, (byte)Instruction.INVALID, 0x5a, tag];

    protected readonly record struct Outcome(bool Error, string Output, ulong GasLeft, string Storage, int OpCodes);

    protected Outcome Run(IVirtualMachine vm, CodeInfo codeInfo, byte[] input, ulong gas = 1_000_000, ExecutionType type = ExecutionType.TRANSACTION)
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Executing, codeSource: Executing, caller: Caller,
            codeInfo: codeInfo, callDepth: 0, value: 0, inputData: input);
        using StackAccessTracker accessTracker = new();
        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), type, environment, accessTracker, _state.TakeSnapshot());
        TransactionSubstate result = vm.ExecuteTransaction<OffFlag>(state, _state, NullTxTracer.Instance);
        _state.Get(new StorageCell(Executing, 0), out UInt256 slot0);
        Outcome outcome = new(result.IsError, result.Output.ToArray().ToHexString(), state.Gas.Value, slot0.ToString(), vm.OpCodeCount);
        _state.Reset();
        return outcome;
    }

    /// <summary>The interpreter's outcome: the runner declines every frame.</summary>
    protected Outcome Interpret(byte[] code, byte[] input, ExecutionType type = ExecutionType.TRANSACTION, IVirtualMachine? vm = null)
    {
        bool enabled = IlEvmSettings.Enabled;
        IlEvmSettings.Enabled = false;
        try
        {
            return Run(vm ?? QueryVm, new CodeInfo(code), input, type: type);
        }
        finally
        {
            IlEvmSettings.Enabled = enabled;
        }
    }

    /// <summary>Runs <paramref name="run"/> as block processing's own thread, as the core marks it for a processing pass.</summary>
    protected static T OnBlockThread<T>(Func<T> run)
    {
        bool previous = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = true;
        try
        {
            return run();
        }
        finally
        {
            ProcessingThread.IsBlockProcessingThread = previous;
        }
    }

    /// <summary>A code info of <paramref name="code"/> stamped with its keccak, as the code cache hands block processing.</summary>
    protected static CodeInfo Stamped(byte[] code)
    {
        CodeInfo codeInfo = new(code);
        new StaticCodeCache(4).Set(ValueKeccak.Compute(code), codeInfo);
        return codeInfo;
    }

    protected static OpcodeBodySet Bodies(IReleaseSpec spec)
    {
        OpcodeBodySet? bodies = IlEvm.BodySetFor(spec);
        Assert.That(bodies, Is.Not.Null, "the core's per-spec body sets must be reachable");
        return bodies!;
    }

    /// <summary>Compiles <paramref name="code"/> whole for <paramref name="spec"/>'s body set, as the startup compile does.</summary>
    private protected static IlEvm.IlContract Compile(byte[] code, IReleaseSpec spec)
    {
        IlEvm.IlContract? contract = IlEvm.Precompile(code, Bodies(spec), out string? reason);
        Assert.That(contract, Is.Not.Null, reason);
        return contract!;
    }

    /// <summary>Ships each code as a list entry with no fields, compiled for Osaka, and publishes them.</summary>
    private protected List<IlEvm.IlContract> Ship(params byte[][] codes)
    {
        List<(IlEvmPrecompileList.Entry, IlEvm.IlContract)> shipped = [];
        foreach (byte[] code in codes)
            shipped.Add((new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), Compile(code, Spec)));
        IlRuntime.PublishShipped(shipped);
        return shipped.ConvertAll(static s => s.Item2);
    }

    /// <summary>Ships <paramref name="code"/> as <paramref name="entry"/>, compiled for Osaka.</summary>
    private protected IlEvm.IlContract Ship(IlEvmPrecompileList.Entry entry, byte[] code)
    {
        IlEvm.IlContract contract = Compile(code, Spec);
        IlRuntime.PublishShipped([(entry, contract)]);
        return contract;
    }

    /// <summary>Compiles <paramref name="code"/> as live profiling would promote it for <paramref name="origin"/>'s source, and publishes the promotion.</summary>
    private protected IlEvm.IlContract Promote(byte[] code, IlOrigin origin)
    {
        IlEvm.IlContract contract = Compile(code, Spec);
        IlProfileKey key = IlProfileKey.ByHash(ValueKeccak.Compute(code), code.Length);
        IlCodeProfile profile = IlRuntime.Profiles.GetOrAdd(key, static (k, length) => new IlCodeProfile(k, length), code.Length);
        contract.Profile = profile;
        contract.Origin = origin;
        contract.RunMask = IlRuntime.RunAll;
        IlCheck.Arm(contract);
        profile.Live = contract;
        IlRuntime.Grew(block: origin == IlOrigin.LiveBlocks, query: true);
        return contract;
    }

    /// <summary>The code's state, which the test expects it to have.</summary>
    private protected static IlEvm.IlCodeState StateOf(CodeInfo codeInfo)
    {
        object? slot = IlEvmFrameRunner.CodeSlot(codeInfo);
        Assert.That(slot, Is.InstanceOf<IlEvm.IlCodeState>());
        return (IlEvm.IlCodeState)slot!;
    }

    /// <summary>Runs compiled code counted: every chunk entry of <paramref name="contract"/>, so every compiled part of a one-chunk contract.</summary>
    private protected static RunCounter CountRuns(IlEvm.IlContract contract)
    {
        RunCounter counter = new();
        for (int chunk = 0; chunk < contract.Runners.Length; chunk++)
        {
            if (contract.Runners[chunk] is { } inner)
                contract.Runners[chunk] = new CountingRunner(inner, counter).Run;
        }

        return counter;
    }

    protected sealed class RunCounter
    {
        public int Runs;
    }

    private sealed class CountingRunner(IlEvm.IlRunner inner, RunCounter counter)
    {
        public EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas,
            scoped ref nint programCounter, scoped ref IlEvm.IlRunState state)
        {
            Interlocked.Increment(ref counter.Runs);
            return inner(vm, ref stack, ref gas, ref programCounter, ref state);
        }
    }

    protected sealed class ExitSource(CancellationToken token) : Nethermind.Config.IProcessExitSource
    {
        public void Exit(int exitCode) { }
        public CancellationToken Token => token;
    }

    protected sealed class OneLogger(Nethermind.Core.Test.TestLogger logger) : ILogManager
    {
        public ILogger GetClassLogger<T>() => new(logger);
        public ILogger GetLogger(string loggerName) => new(logger);
    }
}
