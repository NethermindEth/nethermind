// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The startup compile and the block runner: shipped contracts run compiled from their first frame, block processing
/// compiles nothing else when told not to, and the profile counts the interpreter's work without compiling.
/// </summary>
[NonParallelizable]
public class IlEvmPrecompileTests
{
    private static readonly Address Caller = TestItem.AddressA;
    private static readonly Address Executing = TestItem.AddressB;
    private static readonly Address Callee = TestItem.AddressC;

    private IReleaseSpec _spec = null!;
    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IWorldState _state = null!;
    private IVirtualMachine _vm = null!;
    private IDisposable? _stateScope;
    private (bool Enabled, int Threshold, long Opcodes, bool Sync, bool SelfCheck, bool Profile, bool BlockCompile, bool BlockCheck) _saved;
    private IlMode _savedMode;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _saved = (IlEvmSettings.Enabled, IlEvmSettings.Threshold, IlEvmSettings.OpcodeThreshold, IlEvmSettings.CompileSynchronously,
            IlEvmSettings.SelfCheck, IlEvmSettings.Profile, IlEvmSettings.BlockRuntimeCompile, IlEvmSettings.BlockSelfCheck);
        IlEvmSettings.SelfCheck = false;
        // The Legacy rules, which these tests are of.
        _savedMode = IlEvmSettings.Mode;
        IlEvmSettings.Mode = IlMode.Legacy;
        _spec = Osaka.Instance;
        _container = new ContainerBuilder().AddModule(new TestNethermindModule(_spec)).Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        // Block processing's runner, as Scope=All attaches it.
        _scope = _container.BeginLifetimeScope(builder =>
        {
            builder.AddSingleton(scopeProvider);
            builder.RegisterInstance(IlEvmFrameRunner.Block).As<FrameCodeRunner<EthereumGasPolicy>>();
        });
        _state = _scope.Resolve<IWorldState>();
        _vm = _scope.Resolve<IVirtualMachine>();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(Caller, 1_000_000);
        _state.CreateAccount(Executing, 1_000);
        _state.CreateAccount(Callee, 1_000);
        _state.InsertCode(Callee, ArithmeticCode(), _spec);
        _state.Commit(_spec);
        _state.CommitTree(0);
        _vm.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.WithNumber(7).WithTimestamp(1234).TestObject, _spec));
        _vm.SetTxExecutionContext(new TxExecutionContext(Caller, _scope.Resolve<ICodeInfoRepository>(), null, 3));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        (IlEvmSettings.Enabled, IlEvmSettings.Threshold, IlEvmSettings.OpcodeThreshold, IlEvmSettings.CompileSynchronously,
            IlEvmSettings.SelfCheck, IlEvmSettings.Profile, IlEvmSettings.BlockRuntimeCompile, IlEvmSettings.BlockSelfCheck) = _saved;
        IlEvmSettings.Mode = _savedMode;
        IlEvm.ClearPrecompiled();
        IlEvmProfile.Reset();
        _stateScope?.Dispose();
        _scope?.Dispose();
        _container?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        IlEvm.ClearPrecompiled();
        IlEvmProfile.Reset();
        // Nothing becomes hot on its own here: whatever runs compiled was precompiled.
        IlEvmSettings.Threshold = int.MaxValue;
        IlEvmSettings.OpcodeThreshold = int.MaxValue;
        IlEvmSettings.CompileSynchronously = false;
        IlEvmSettings.Profile = false;
        IlEvmSettings.BlockRuntimeCompile = false;
        IlEvmSettings.BlockSelfCheck = false;
    }

    /// <summary>A loop that stores <c>calldata[0] &amp; 31</c> words, then returns the first: jumps, memory and storage.</summary>
    private static byte[] LoopCode() => Bytes.FromHexString("600035601f165b8015601657600190038080526006565b5060205ff3");

    /// <summary>ADD and MUL over calldata, SSTORE of the result, then RETURN of it.</summary>
    private static byte[] ArithmeticCode() => Bytes.FromHexString("6000356020350160403502806000556000526020" + "6000f3");

    private (bool Error, string Output, ulong GasLeft, string Storage) Run(CodeInfo codeInfo, byte[] input, bool enabled)
    {
        IlEvmSettings.Enabled = enabled;
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Executing, codeSource: Executing, caller: Caller,
            codeInfo: codeInfo, callDepth: 0, value: 0, inputData: input);
        using StackAccessTracker accessTracker = new();
        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(1_000_000), ExecutionType.TRANSACTION, environment, accessTracker, _state.TakeSnapshot());
        TransactionSubstate result = _vm.ExecuteTransaction<OffFlag>(state, _state, NullTxTracer.Instance);
        _state.Get(new StorageCell(Executing, 0), out UInt256 slot0);
        (bool, string, ulong, string) outcome = (result.IsError, result.Output.ToArray().ToHexString(), state.Gas.Value, slot0.ToString());
        _state.Reset();
        return outcome;
    }

    private OpcodeBodySet Bodies()
    {
        OpcodeBodySet? bodies = IlEvm.BodySetFor(_spec);
        Assert.That(bodies, Is.Not.Null, "the core's per-spec body sets must be reachable for the startup compile");
        return bodies!;
    }

    private void Ship(params byte[][] codes)
    {
        OpcodeBodySet bodies = Bodies();
        Dictionary<ValueHash256, IlEvm.IlContract> compiled = [];
        foreach (byte[] code in codes)
        {
            IlEvm.IlContract? contract = IlEvm.Precompile(code, bodies, out string? reason);
            Assert.That(contract, Is.Not.Null, reason);
            compiled[ValueKeccak.Compute(code)] = contract!;
        }

        IlEvm.PublishPrecompiled(compiled);
    }

    [Test]
    public void Precompiled_code_runs_compiled_from_its_first_frame_and_matches_the_interpreter()
    {
        Ship(LoopCode(), ArithmeticCode());
        Assert.That(IlEvm.PrecompiledCount, Is.EqualTo(2));
        byte[][] inputs = [new byte[32], Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000007"),
            Bytes.FromHexString("00000000000000000000000000000000000000000000000000000000000000ff" + "0000000000000000000000000000000000000000000000000000000000000003" + "0000000000000000000000000000000000000000000000000000000000000005")];
        foreach (byte[] code in new[] { LoopCode(), ArithmeticCode() })
        {
            foreach (byte[] input in inputs)
            {
                // A fresh code info, as a code cache would hand out, and not stamped: found by hashing its code.
                CodeInfo reference = new(code);
                CodeInfo shipped = new(code);
                var expected = Run(reference, input, enabled: false);
                var actual = Run(shipped, input, enabled: true);
                Assert.That(IlEvm.IsCompiled(shipped), Is.True, "a shipped contract runs compiled on its first frame");
                Assert.That(actual, Is.EqualTo(expected));
            }
        }
    }

    [Test]
    public void Block_runner_compiles_nothing_it_was_not_shipped_and_leaves_that_code_alone()
    {
        byte[] code = LoopCode();
        CodeInfo codeInfo = new(code);
        IlEvmSettings.Threshold = 1;
        IlEvmSettings.CompileSynchronously = true;
        for (int i = 0; i < 5; i++)
            Run(codeInfo, new byte[32], enabled: true);
        Assert.That(IlEvm.IsCompiled(codeInfo), Is.False, "block processing compiles nothing at run time unless asked to");
        Assert.That(IlEvm.FailureReason(codeInfo), Is.Null);

        // Shipped code, by contrast, runs compiled; other code still does not.
        Ship(ArithmeticCode());
        CodeInfo shipped = new(ArithmeticCode());
        CodeInfo other = new(code);
        Run(shipped, new byte[96], enabled: true);
        Run(other, new byte[32], enabled: true);
        Assert.That(IlEvm.IsCompiled(shipped), Is.True);
        Assert.That(IlEvm.IsCompiled(other), Is.False);
    }

    [Test]
    public void Block_runner_compiles_hot_code_when_asked_to()
    {
        IlEvmSettings.BlockRuntimeCompile = true;
        IlEvmSettings.Threshold = 1;
        IlEvmSettings.CompileSynchronously = true;
        CodeInfo codeInfo = new(LoopCode());
        var expected = Run(new CodeInfo(LoopCode()), new byte[32], enabled: false);
        var actual = Run(codeInfo, new byte[32], enabled: true);
        Assert.That(IlEvm.IsCompiled(codeInfo), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Profile_counts_frames_and_interpreted_opcodes_and_compiles_nothing()
    {
        IlEvmSettings.Profile = true;
        IlEvmSettings.Threshold = 1;
        IlEvmSettings.CompileSynchronously = true;
        byte[] code = ArithmeticCode();
        CodeInfo codeInfo = new(code);
        for (int i = 0; i < 3; i++)
            Run(codeInfo, new byte[96], enabled: true);
        long perFrame = _vm.OpCodeCount;
        IlEvmSettings.Profile = false;

        List<(ValueHash256 Hash, long Ops, long Frames, int Size)> profile = IlEvmProfile.Snapshot();
        Assert.That(IlEvm.IsCompiled(codeInfo), Is.False, "profiling compiles nothing");
        Assert.That(profile, Has.Count.EqualTo(1));
        Assert.That(profile[0].Hash, Is.EqualTo(ValueKeccak.Compute(code)));
        Assert.That(profile[0].Frames, Is.EqualTo(3));
        Assert.That(profile[0].Ops, Is.EqualTo(3 * perFrame));
        Assert.That(profile[0].Size, Is.EqualTo(code.Length));
    }

    [Test]
    public void Profile_is_logged_once_when_the_process_starts_to_exit()
    {
        IlEvmSettings.Profile = true;
        CodeInfo codeInfo = new(ArithmeticCode());
        Run(codeInfo, new byte[96], enabled: true);
        using System.Threading.CancellationTokenSource exit = new();
        Nethermind.Core.Test.TestLogger logger = new();
        IlEvmStartupStep step = new(new IlEvmConfig { Enabled = true, Profile = true, ProfileTop = 10 }, null!, null!, null!, null!, null!,
            new ExitSource(exit.Token), new OneLogger(logger));
        step.Execute(default).Wait();
        Assert.That(logger.LogList.Any(static l => l.StartsWith("IL-EVM profile ", StringComparison.Ordinal)), Is.False, "nothing before exit");
        exit.Cancel();
        List<string> lines = logger.LogList.Where(static l => l.StartsWith("IL-EVM profile", StringComparison.Ordinal)).ToList();
        Assert.That(lines[0], Does.StartWith("IL-EVM profile: 1 contracts, 1 frames"));
        Assert.That(lines[1], Does.StartWith($"IL-EVM profile 1 {ValueKeccak.Compute(ArithmeticCode())} ops="));
        Assert.That(lines[^1], Is.EqualTo("IL-EVM profile end"));
        IlEvmProfile.Dump(new Nethermind.Logging.ILogger(logger), 10);
        Assert.That(logger.LogList.Count(static l => l == "IL-EVM profile end"), Is.EqualTo(1), "logged once");
    }

    private sealed class ExitSource(System.Threading.CancellationToken token) : Nethermind.Config.IProcessExitSource
    {
        public void Exit(int exitCode) { }
        public System.Threading.CancellationToken Token => token;
    }

    private sealed class OneLogger(Nethermind.Core.Test.TestLogger logger) : Nethermind.Logging.ILogManager
    {
        public Nethermind.Logging.ILogger GetClassLogger<T>() => new(logger);
        public Nethermind.Logging.ILogger GetLogger(string loggerName) => new(logger);
    }

    /// <summary>Calls the callee with no data, so the caller's frame is offered again when the call returns, then stops.</summary>
    private static byte[] CallerCode() =>
        Bytes.FromHexString("60006000600060006000" + "73" + Callee.ToString(false).Substring(2) + "5af15000");

    [Test]
    public void A_frame_under_another_fork_does_not_keep_the_shipped_contract_away()
    {
        Ship(LoopCode());
        OpcodeBodySet osaka = Bodies();
        OpcodeBodySet? prague = IlEvm.BodySetFor(Prague.Instance);
        Assume.That(prague, Is.Not.SameAs(osaka), "Prague and Osaka must run different bodies for this test");
        CodeInfo codeInfo = new(LoopCode());
        Assert.That(IlEvm.FindPrecompiledOnly(codeInfo, prague!), Is.Null, "shipped for Osaka only");
        Assert.That(IlEvm.FindPrecompiledOnly(codeInfo, osaka), Is.Not.Null, "a later Osaka frame still finds it");
        Assert.That(IlEvm.FindPrecompiledOnly(codeInfo, prague!), Is.Null);
        Assert.That(IlEvm.FindPrecompiledOnly(codeInfo, osaka), Is.Not.Null);
    }

    [Test]
    public void Profile_counts_a_frame_once_however_often_it_resumes()
    {
        IlEvmSettings.Profile = true;
        CodeInfo caller = new(CallerCode());
        Run(caller, [], enabled: true);
        IlEvmSettings.Profile = false;
        List<(ValueHash256 Hash, long Ops, long Frames, int Size)> profile = IlEvmProfile.Snapshot();
        var callerEntry = profile.Single(e => e.Hash == ValueKeccak.Compute(CallerCode()));
        var calleeEntry = profile.Single(e => e.Hash == ValueKeccak.Compute(ArithmeticCode()));
        Assert.That(callerEntry.Frames, Is.EqualTo(1), "entered once, resumed once after the call");
        Assert.That(calleeEntry.Frames, Is.EqualTo(1));
        // PUSH1 x5, PUSH20, GAS, CALL before the call; POP, STOP after it.
        Assert.That(callerEntry.Ops, Is.EqualTo(10));
    }

    [Test]
    public void A_shipped_contract_that_loses_its_self_check_keeps_its_code()
    {
        double savedRatio = IlEvmSettings.SelfCheckKeepRatio;
        int savedDelay = IlEvmSettings.SelfCheckDelayMs, savedRecheck = IlEvmSettings.SelfCheckRecheckMs;
        int savedEarly = IlEvmSettings.SelfCheckEarlyRecheckMs, savedWarmUp = IlEvmSettings.SelfCheckWarmUpMs;
        try
        {
            IlEvmSettings.SelfCheck = true;
            IlEvmSettings.BlockSelfCheck = true;
            IlEvmSettings.SelfCheckDelayMs = 0;
            IlEvmSettings.SelfCheckRecheckMs = 0;
            IlEvmSettings.SelfCheckEarlyRecheckMs = 0;
            IlEvmSettings.SelfCheckWarmUpMs = 0;
            // Nothing is fast enough to keep at this ratio.
            IlEvmSettings.SelfCheckKeepRatio = 1e-6;
            Ship(LoopCode());
            CodeInfo codeInfo = new(LoopCode());
            byte[] input = Bytes.FromHexString("000000000000000000000000000000000000000000000000000000000000001f");
            var expected = Run(new CodeInfo(LoopCode()), input, enabled: false);
            for (int frame = 0; frame < 40_000 && IlEvm.SelfCheckOf(codeInfo)?.Verdict != "interpreted"; frame++)
                Assert.That(Run(codeInfo, input, enabled: true), Is.EqualTo(expected));
            Assert.That(IlEvm.SelfCheckOf(codeInfo)?.Verdict, Is.EqualTo("interpreted"), "the check gives the contract back");

            // Its code stays: with block processing's own self-check off, it runs compiled again.
            IlEvmSettings.BlockSelfCheck = false;
            IlEvmSettings.SelfCheck = false;
            Assert.That(Run(codeInfo, input, enabled: true), Is.EqualTo(expected));
            Assert.That(IlEvm.IsCompiled(codeInfo), Is.True);
            Assert.That(IlEvm.CompileStats(codeInfo)?.Chunks, Is.GreaterThan(0), "no chunk was released");
        }
        finally
        {
            IlEvmSettings.SelfCheckKeepRatio = savedRatio;
            IlEvmSettings.SelfCheckDelayMs = savedDelay;
            IlEvmSettings.SelfCheckRecheckMs = savedRecheck;
            IlEvmSettings.SelfCheckEarlyRecheckMs = savedEarly;
            IlEvmSettings.SelfCheckWarmUpMs = savedWarmUp;
            IlEvmSettings.SelfCheck = false;
            IlEvmSettings.BlockSelfCheck = false;
        }
    }

    [Test]
    public void Precompile_list_reads_profile_lines_as_they_are_logged()
    {
        string hash = "0x" + new string('c', 64);
        List<ValueHash256> list = IlEvmPrecompileList.Parse([
            $"2026-10-13 12:00:00.0000|IL-EVM profile 1 {hash} ops=5 frames=1 size=10 cum=0.5000",
            "IL-EVM profile end", "0x" + new string('z', 64)]);
        Assert.That(list, Is.EqualTo(new[] { new ValueHash256(hash) }));
    }

    [Test]
    public void Precompile_list_takes_the_first_token_of_each_line_once()
    {
        string a = "0x" + new string('a', 64), b = new string('B', 64);
        List<ValueHash256> list = IlEvmPrecompileList.Parse([
            "# comment", "", $"  {a} ops=5 frames=1", b, a, "0x1234", "not-a-hash"]);
        Assert.That(list, Is.EqualTo(new[] { new ValueHash256(a), new ValueHash256("0x" + b) }));
    }

    [Test]
    public void Shipped_list_resource_is_present()
    {
        using System.IO.Stream? stream = typeof(IlEvmPrecompileList).Assembly.GetManifestResourceStream(IlEvmPrecompileList.ResourceName);
        Assert.That(stream, Is.Not.Null);
        Assert.That(IlEvmPrecompileList.Load().All(static h => h != default), Is.True);
    }
}
