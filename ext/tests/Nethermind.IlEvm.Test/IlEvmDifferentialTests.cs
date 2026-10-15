// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>Runs random bytecode on the interpreter and on its IL compilation and requires identical outcomes.</summary>
/// <remarks>
/// Each program runs at several gas limits, including every limit just below the gas a successful run used, so
/// out-of-gas lands on every instruction of the final segments. The outcome compared is the fault, the revert flag,
/// the gas left, the output, the logs, the executing account's storage and the opcode count. The callee the programs
/// call into is compiled too.
/// </remarks>
[NonParallelizable]
public partial class IlEvmDifferentialTests
{
    private static readonly Address Executing = new("0x00000000000000000000000000000000000c0de0");
    private static readonly Address Callee = new("0x00000000000000000000000000000000000ca11e");
    private static readonly Address Caller = new("0x00000000000000000000000000000000000ca11a");

    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private IVirtualMachine _vm = null!;
    private IReleaseSpec _spec = null!;
    private bool _savedEnabled;
    private int _savedThreshold;
    private bool _savedSync;
    private bool _savedSelfCheck;
    private IlMode _savedMode;

    private readonly record struct Outcome(
        bool IsError, bool ShouldRevert, EvmExceptionType Exception, ulong GasLeft, string Output, string Logs, string Storage, int OpCodes, int Polls = 0)
    {
        public override string ToString() =>
            $"error={IsError} revert={ShouldRevert} ex={Exception} gasLeft={GasLeft} ops={OpCodes} polls={Polls} out={Output} logs={Logs} storage={Storage}";
    }

    /// <summary>
    /// An untraced, cancelable tracer, as eth_call runs under: it counts the machine's cancellation polls and reports
    /// cancellation from the <see cref="CancelAfter"/>th on, so both machines must poll at the same points.
    /// </summary>
    private sealed class PollCountingTracer : TxTracer, ITxTracer
    {
        public int CancelAfter = int.MaxValue;
        public int Polls;

        bool ITxTracer.IsCancelable => true;
        bool ITxTracer.IsCancelled => ++Polls > CancelAfter;
    }

    /// <summary>The same execution on both machines; under cancellation compiled code polls at nearly the same points.</summary>
    /// <remarks>
    /// Compiled code polls where the dispatch loop does. A run it hands back continues in a dispatch loop whose poll
    /// interval starts over, so the polls after a hand-back can shift by one interval; nothing else differs.
    /// </remarks>
    private bool Same(Outcome interpreter, Outcome compiled) =>
        !_cancelable
            ? interpreter == compiled
            : interpreter with { Polls = 0 } == compiled with { Polls = 0 } &&
              compiled.Polls >= interpreter.Polls - 1 && compiled.Polls <= 2 * interpreter.Polls + 2;

    /// <summary>Whether <see cref="Run"/> executes cancelable, as eth_call does, and from which poll it cancels.</summary>
    private bool _cancelable;
    private int _cancelAfter = int.MaxValue;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _savedEnabled = IlEvmSettings.Enabled;
        _savedThreshold = IlEvmSettings.Threshold;
        _savedSync = IlEvmSettings.CompileSynchronously;
        _savedSelfCheck = IlEvmSettings.SelfCheck;
        _savedMode = IlEvmSettings.Mode;
        // Compiled code runs every frame it is given here, whatever it would prove against the interpreter, and every
        // contract compiles on its first frame, by the Legacy rules.
        IlEvmSettings.SelfCheck = false;
        IlEvmSettings.Mode = IlMode.Legacy;

        _spec = Osaka.Instance;
        _container = new ContainerBuilder().AddModule(new TestNethermindModule(_spec)).Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _scope = _container.BeginLifetimeScope(builder =>
        {
            builder.AddSingleton(scopeProvider);
            builder.RegisterInstance(IlEvmFrameRunner.Instance).As<FrameCodeRunner<EthereumGasPolicy>>();
        });
        _state = _scope.Resolve<IWorldState>();
        _vm = _scope.Resolve<IVirtualMachine>();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(Caller, 1_000_000);
        _state.CreateAccount(Executing, 1_000);
        _state.CreateAccount(Callee, 1_000);
        for (int i = 0; i < 4; i++)
            _state.Set(new StorageCell(Executing, (UInt256)i), (UInt256)(i + 1));
        _state.InsertCode(Callee, CalleeCode(), _spec);
        _state.Commit(_spec);
        _state.CommitTree(0);
        _vm.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.WithNumber(7).WithTimestamp(1234).TestObject, _spec));
        _vm.SetTxExecutionContext(new TxExecutionContext(Caller, _scope.Resolve<ICodeInfoRepository>(), null, 3));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        IlEvmSettings.Enabled = _savedEnabled;
        IlEvmSettings.Threshold = _savedThreshold;
        IlEvmSettings.CompileSynchronously = _savedSync;
        IlEvmSettings.SelfCheck = _savedSelfCheck;
        IlEvmSettings.Mode = _savedMode;
        _stateScope?.Dispose();
        _scope?.Dispose();
        _container?.Dispose();
    }

    /// <summary>A callee that writes <c>calldata[0] &amp; 31</c> words of memory in a loop and returns the first one.</summary>
    /// <remarks>
    /// 00 PUSH1 0, CALLDATALOAD, PUSH1 1f, AND; 06 JUMPDEST, DUP1, ISZERO, PUSH1 16, JUMPI, PUSH1 1, SWAP1, SUB,
    /// DUP1, DUP1, MSTORE, PUSH1 6, JUMP; 16 JUMPDEST, POP, PUSH1 20, PUSH0, RETURN.
    /// </remarks>
    private static byte[] CalleeCode() => Bytes.FromHexString("600035601f165b8015601657600190038080526006565b5060205ff3");

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
    public void Random_programs_match_the_interpreter(int seed, int programs, bool memoryHeavy, int chunkInstructions = 1_400, bool cancelable = false)
    {
        Random random = new(seed);
        // Small chunks make most jumps and fall-throughs cross from one compiled method to another.
        int savedChunk = IlEvmSettings.ChunkInstructions;
        IlEvmSettings.ChunkInstructions = chunkInstructions;
        _cancelable = cancelable;
        try
        {
            RunPrograms(random, seed, programs, memoryHeavy);
        }
        finally
        {
            IlEvmSettings.ChunkInstructions = savedChunk;
            _cancelable = false;
            _cancelAfter = int.MaxValue;
        }
    }

    private void RunPrograms(Random random, int seed, int programs, bool memoryHeavy)
    {
        int compiled = 0;
        int runs = 0;
        Dictionary<string, int> failures = [];
        for (int p = 0; p < programs; p++)
        {
            byte[] code = Generate(random, random.Next(4, 160), memoryHeavy);
            CodeInfo codeInfo = new(code);
            byte[] input = new byte[random.Next(0, 70)];
            random.NextBytes(input);

            foreach (ulong gas in GasLimits(random))
            {
                _cancelAfter = int.MaxValue;
                Outcome expected = Run(codeInfo, input, gas, compiledCode: false);
                Outcome actual = Run(codeInfo, input, gas, compiledCode: true);
                runs++;
                if (!Same(expected, actual))
                {
                    Assert.Fail($"program {p} seed {seed} gas {gas}\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {expected}\ncompiled    {actual}\ncompile: {IlEvm.FailureReason(codeInfo) ?? "ok"}");
                }

                if (_cancelable && expected.Polls > 2)
                {
                    // Both machines reach this many polls, whichever ran a part of the frame.
                    _cancelAfter = random.Next(1, expected.Polls - 1);
                    Outcome e = Run(codeInfo, input, gas, compiledCode: false);
                    Outcome a = Run(codeInfo, input, gas, compiledCode: true);
                    runs++;
                    _cancelAfter = int.MaxValue;
                    if (!Same(e, a))
                        Assert.Fail($"program {p} seed {seed} gas {gas} (cancelled after {_cancelAfter} polls)\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {e}\ncompiled    {a}");
                }

                // Every limit just below a completed run's usage puts the out-of-gas point on each of the last instructions.
                if (!expected.IsError && gas == 400_000)
                {
                    ulong used = gas - expected.GasLeft;
                    for (ulong back = 1; back <= Math.Min(used, 48UL); back++)
                    {
                        ulong limit = used - back;
                        Outcome e = Run(codeInfo, input, limit, compiledCode: false);
                        Outcome a = Run(codeInfo, input, limit, compiledCode: true);
                        runs++;
                        if (!Same(e, a))
                            Assert.Fail($"program {p} seed {seed} gas {limit} (boundary)\ncode {code.ToHexString(true)}\ninput {input.ToHexString(true)}\ninterpreter {e}\ncompiled    {a}");
                    }
                }
            }

            if (IlEvm.IsCompiled(codeInfo))
                compiled++;
            else
            {
                string reason = IlEvm.FailureReason(codeInfo) ?? "not compiled";
                failures[reason] = failures.GetValueOrDefault(reason) + 1;
            }
        }

        TestContext.Out.WriteLine($"seed {seed}: {compiled}/{programs} programs compiled, {runs} paired runs");
        foreach ((string reason, int count) in failures)
            TestContext.Out.WriteLine($"  {count} not compiled: {reason}");
        Assert.That(compiled, Is.EqualTo(programs), "every random program should compile");
    }

    private static IEnumerable<ulong> GasLimits(Random random)
    {
        yield return 400_000;
        yield return 30_000;
        yield return (ulong)random.Next(21, 3_000);
        yield return (ulong)random.Next(3_000, 60_000);
    }

    /// <summary>Real contract bodies from recorded eth_call traffic, extracted to a local folder by C:/tmp/ilevm-tools/extract_codes.py.</summary>
    private const string CorpusDirectory = @"C:\tmp\ilevm-corpus";

    /// <summary>Compiles every corpus body and runs the recorded calldata of each call target on both machines.</summary>
    /// <remarks>The state is empty, so calls into mainnet contracts find no code; the bodies still run their real dispatch and decoding.</remarks>
    [Test, Explicit("Needs the local corpus extract")]
    public void Corpus_bodies_compile_and_match_the_interpreter()
    {
        string codes = System.IO.Path.Combine(CorpusDirectory, "codes");
        if (!System.IO.Directory.Exists(codes))
            Assert.Ignore("no corpus extract");

        IlEvmSettings.ChunkInstructions = 1_400;
        int compiledCount = 0;
        List<string> notCompiled = [];
        long ilBytes = 0;
        TimeSpan jit = TimeSpan.Zero;
        int maxChunks = 0;
        Dictionary<string, CodeInfo> byHash = [];
        // Every chunk of every body, not only those the empty-calldata frame reaches.
        bool savedEager = IlEvmSettings.EagerChunks;
        IlEvmSettings.EagerChunks = true;
        foreach (string file in System.IO.Directory.GetFiles(codes, "*.hex"))
        {
            CodeInfo codeInfo = new(Bytes.FromHexString(System.IO.File.ReadAllText(file).Trim()));
            byHash[System.IO.Path.GetFileNameWithoutExtension(file)] = codeInfo;
            Run(codeInfo, [], 1_000_000, compiledCode: true);
            if (IlEvm.CompileStats(codeInfo) is { } stats && IlEvm.FailureReason(codeInfo) is null)
            {
                compiledCount++;
                ilBytes += stats.IlBytes;
                jit += stats.JitTime;
                maxChunks = Math.Max(maxChunks, stats.Chunks);
            }
            else
            {
                notCompiled.Add($"{System.IO.Path.GetFileName(file)} ({codeInfo.CodeSpan.Length} bytes): {IlEvm.FailureReason(codeInfo)}");
            }
        }

        IlEvmSettings.EagerChunks = savedEager;
        TestContext.Out.WriteLine($"{compiledCount}/{byHash.Count} bodies compiled, {ilBytes / 1024} KiB IL, JIT {jit.TotalMilliseconds:F0} ms in all, at most {maxChunks} chunks");
        foreach (string line in notCompiled)
            TestContext.Out.WriteLine("  not compiled: " + line);

        // Each sampled call: its target body with the recorded calldata, on both machines.
        int compared = 0;
        foreach (string line in System.IO.File.ReadLines(System.IO.Path.Combine(CorpusDirectory, "target-samples.jsonl")))
        {
            using System.Text.Json.JsonDocument request = System.Text.Json.JsonDocument.Parse(line);
            System.Text.Json.JsonElement call = request.RootElement.GetProperty("params")[0];
            System.Text.Json.JsonElement overrides = request.RootElement.GetProperty("params")[2];
            string to = call.GetProperty("to").GetString()!;
            string code = overrides.EnumerateObject().First(o => string.Equals(o.Name, to, StringComparison.OrdinalIgnoreCase)).Value.GetProperty("code").GetString()!;
            byte[] input = Bytes.FromHexString(call.GetProperty("data").GetString()!);
            CodeInfo codeInfo = new(Bytes.FromHexString(code));
            foreach (ulong gas in new ulong[] { 30_000_000, 200_000, 25_000 })
            {
                Outcome expected = Run(codeInfo, input, gas, compiledCode: false);
                Outcome actual = Run(codeInfo, input, gas, compiledCode: true);
                compared++;
                Assert.That(actual, Is.EqualTo(expected), $"target {to} gas {gas}");
            }
        }

        TestContext.Out.WriteLine($"{compared} recorded calls matched");
        Assert.That(notCompiled, Is.Empty);
    }

    /// <summary>Times every recorded call target on both machines, alternating rounds so drift falls on both alike.</summary>
    [Test, Explicit("Timing; needs the local corpus extract")]
    public void Corpus_calls_timing()
    {
        string samples = System.IO.Path.Combine(CorpusDirectory, "target-samples.jsonl");
        if (!System.IO.File.Exists(samples))
            Assert.Ignore("no corpus extract");

        IlEvmSettings.ChunkInstructions = 1_400;
        List<(string To, CodeInfo Code, byte[] Input)> calls = [];
        foreach (string line in System.IO.File.ReadLines(samples))
        {
            using System.Text.Json.JsonDocument request = System.Text.Json.JsonDocument.Parse(line);
            System.Text.Json.JsonElement call = request.RootElement.GetProperty("params")[0];
            System.Text.Json.JsonElement overrides = request.RootElement.GetProperty("params")[2];
            string to = call.GetProperty("to").GetString()!;
            string code = overrides.EnumerateObject().First(o => string.Equals(o.Name, to, StringComparison.OrdinalIgnoreCase)).Value.GetProperty("code").GetString()!;
            calls.Add((to, new CodeInfo(Bytes.FromHexString(code)), Bytes.FromHexString(call.GetProperty("data").GetString()!)));
        }

        const ulong Gas = 30_000_000;
        const int Rounds = 5;
        // Every call on both machines first, so the runtime has tiered up the interpreter and the helpers before any timing.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach ((string _, CodeInfo code, byte[] input) in calls)
            {
                Run(code, input, Gas, compiledCode: false);
                Run(code, input, Gas, compiledCode: true);
            }
        }

        double logRatios = 0;
        long totalInterpreter = 0, totalCompiled = 0;
        foreach ((string to, CodeInfo code, byte[] input) in calls)
        {
            // Warm both (the compiled one compiles on its first run), then size rounds to about 50 ms each.
            for (int i = 0; i < 3; i++)
            {
                Run(code, input, Gas, compiledCode: false);
                Run(code, input, Gas, compiledCode: true);
            }

            long single = System.Diagnostics.Stopwatch.GetTimestamp();
            Run(code, input, Gas, compiledCode: false);
            single = Math.Max(1, System.Diagnostics.Stopwatch.GetTimestamp() - single);
            int PerRound = (int)Math.Clamp(System.Diagnostics.Stopwatch.Frequency / 20 / single, 1, 5_000);

            long interpreter = long.MaxValue, compiled = long.MaxValue;
            for (int round = 0; round < Rounds; round++)
            {
                foreach (bool compiledCode in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (int i = 0; i < PerRound; i++)
                        Run(code, input, Gas, compiledCode);
                    long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                    if (compiledCode) compiled = Math.Min(compiled, elapsed);
                    else interpreter = Math.Min(interpreter, elapsed);
                }
            }

            totalInterpreter += interpreter;
            totalCompiled += compiled;
            logRatios += Math.Log((double)interpreter / compiled);
            Outcome outcome = Run(code, input, Gas, compiledCode: true);
            TestContext.Progress.WriteLine($"{to} {code.CodeSpan.Length,6} B  interpreter {interpreter * 1e9 / System.Diagnostics.Stopwatch.Frequency / PerRound,9:F0} ns  compiled {compiled * 1e9 / System.Diagnostics.Stopwatch.Frequency / PerRound,9:F0} ns  x{(double)interpreter / compiled:F2}  ops {outcome.OpCodes} {outcome.Exception}");
        }

        TestContext.Out.WriteLine($"{calls.Count} calls: geometric-mean speedup x{Math.Exp(logRatios / calls.Count):F3}, total x{(double)totalInterpreter / totalCompiled:F3}");
    }

    /// <param name="compileColdChunks">Whether a chunk not compiled yet is compiled when a frame reaches it, else left to hand the frame back.</param>
    private Outcome Run(CodeInfo codeInfo, byte[] input, ulong gas, bool compiledCode, bool compileColdChunks = true)
    {
        IlEvmSettings.Enabled = compiledCode;
        IlEvmSettings.Threshold = 1;
        IlEvmSettings.CompileSynchronously = compileColdChunks;
        try
        {
            using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                executingAccount: Executing, codeSource: Executing, caller: Caller,
                codeInfo: codeInfo, callDepth: 0, value: 0, inputData: input);
            using StackAccessTracker accessTracker = new();
            using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, environment,
                accessTracker, _state.TakeSnapshot());
            TransactionSubstate result;
            PollCountingTracer? polls = _cancelable ? new PollCountingTracer { CancelAfter = _cancelAfter } : null;
            try
            {
                result = _vm.ExecuteTransaction<OffFlag>(state, _state, polls ?? (ITxTracer)NullTxTracer.Instance);
            }
            catch (Exception exception)
            {
                // Both machines must throw the same way, e.g. BLOCKHASH past what the test provider holds, or a cancellation
                // at the same poll.
                _state.Reset();
                return new Outcome(true, false, EvmExceptionType.Other, 0, exception.GetType().Name, "", "", 0, polls?.Polls ?? 0);
            }

            StringBuilder storage = new();
            for (int i = 0; i < 8; i++)
            {
                _state.Get(new StorageCell(Executing, (UInt256)i), out UInt256 value);
                storage.Append(value.ToString()).Append(',');
            }
            string logs = string.Join(";", result.LogsToArray().Select(static l =>
                $"{l.Address}:{string.Join("/", l.Topics.Select(static t => t.ToString()))}:{l.Data.ToHexString()}"));
            Outcome outcome = new(result.IsError, result.ShouldRevert, result.EvmExceptionType, state.Gas.Value,
                result.Output.ToArray().ToHexString(), logs, storage.ToString(), _vm.OpCodeCount, polls?.Polls ?? 0);
            _state.Reset();
            return outcome;
        }
        finally
        {
            IlEvmSettings.Enabled = false;
        }
    }

    private static readonly Instruction[] Arithmetic =
    [
        Instruction.ADD, Instruction.MUL, Instruction.SUB, Instruction.DIV, Instruction.SDIV, Instruction.MOD, Instruction.SMOD,
        Instruction.ADDMOD, Instruction.MULMOD, Instruction.EXP, Instruction.SIGNEXTEND, Instruction.LT, Instruction.GT,
        Instruction.SLT, Instruction.SGT, Instruction.EQ, Instruction.ISZERO, Instruction.AND, Instruction.OR, Instruction.XOR,
        Instruction.NOT, Instruction.BYTE, Instruction.SHL, Instruction.SHR, Instruction.SAR, Instruction.CLZ,
    ];

    private static readonly Instruction[] EnvironmentOps =
    [
        Instruction.ADDRESS, Instruction.BALANCE, Instruction.ORIGIN, Instruction.CALLER, Instruction.CALLVALUE,
        Instruction.CALLDATALOAD, Instruction.CALLDATASIZE, Instruction.CODESIZE, Instruction.GASPRICE,
        Instruction.EXTCODESIZE, Instruction.RETURNDATASIZE, Instruction.EXTCODEHASH, Instruction.BLOCKHASH,
        Instruction.COINBASE, Instruction.TIMESTAMP, Instruction.NUMBER, Instruction.PREVRANDAO, Instruction.GASLIMIT,
        Instruction.CHAINID, Instruction.SELFBALANCE, Instruction.BASEFEE, Instruction.BLOBHASH, Instruction.BLOBBASEFEE,
        Instruction.MSIZE, Instruction.PC, Instruction.GAS,
    ];

    /// <summary>A random program: small pushes, valid and invalid jumps, memory, storage, logs and calls.</summary>
    /// <param name="memoryHeavy">Mostly memory words at offsets from in-place through growing to past 2^32 and 2^64.</param>
    private static byte[] Generate(Random random, int length, bool memoryHeavy)
    {
        List<byte> code = [];
        List<int> jumpDests = [];
        List<int> targetFixups = [];

        void Push1(int value)
        {
            code.Add((byte)Instruction.PUSH1);
            code.Add((byte)value);
        }

        void PushOffset()
        {
            switch (random.Next(10))
            {
                case < 5:
                    Push1(random.Next(0, 160));
                    break;
                case < 8:
                    code.Add((byte)Instruction.PUSH2);
                    code.Add((byte)random.Next(0, 0x10));
                    code.Add((byte)random.Next(256));
                    break;
                case 8:
                    // Just below and past 2^32, where the in-place paths stop.
                    code.Add((byte)Instruction.PUSH5);
                    code.Add((byte)random.Next(0, 2));
                    for (int b = 0; b < 4; b++) code.Add(random.Next(2) == 0 ? (byte)0xff : (byte)random.Next(256));
                    break;
                default:
                    code.Add((byte)Instruction.PUSH9);
                    for (int b = 0; b < 9; b++) code.Add((byte)random.Next(256));
                    break;
            }
        }

        for (int i = 0; i < length; i++)
        {
            int roll = random.Next(100);
            if (memoryHeavy && roll < 45)
            {
                // A value and an offset, or a length, an offset and a destination for the copies.
                if (random.Next(3) == 0) Push1(random.Next(0, 64)); else code.Add((byte)Instruction.DUP1);
                PushOffset();
                code.Add(random.Next(10) switch
                {
                    < 3 => (byte)Instruction.MLOAD,
                    < 6 => (byte)Instruction.MSTORE,
                    6 => (byte)Instruction.MSTORE8,
                    7 => (byte)Instruction.KECCAK256,
                    8 => (byte)Instruction.MCOPY,
                    _ => (byte)Instruction.MSIZE,
                });
                continue;
            }

            if (roll < 7)
            {
                jumpDests.Add(code.Count);
                code.Add((byte)Instruction.JUMPDEST);
            }
            else if (roll < 26)
            {
                if (random.Next(5) == 0) code.Add((byte)Instruction.PUSH0);
                else Push1(random.Next(0, 70));
            }
            else if (roll < 31)
            {
                // A jump target, patched once the JUMPDESTs are known, or garbage now and then.
                code.Add((byte)Instruction.PUSH2);
                targetFixups.Add(code.Count);
                code.Add(0);
                code.Add(0);
                if (random.Next(3) > 0) code.Add(random.Next(2) == 0 ? (byte)Instruction.JUMP : (byte)Instruction.JUMPI);
            }
            else if (roll < 34)
            {
                int width = random.Next(1, 33);
                code.Add((byte)(Instruction.PUSH1 + (byte)(width - 1)));
                for (int b = 0; b < width; b++) code.Add((byte)random.Next(256));
            }
            else if (roll < 46)
            {
                int pick = random.Next(3);
                code.Add(pick switch
                {
                    0 => (byte)(Instruction.DUP1 + (byte)random.Next(0, 6)),
                    1 => (byte)(Instruction.SWAP1 + (byte)random.Next(0, 5)),
                    _ => (byte)Instruction.POP,
                });
            }
            else if (roll < 62)
            {
                code.Add((byte)Arithmetic[random.Next(Arithmetic.Length)]);
            }
            else if (roll < 67)
            {
                code.Add(random.Next(2) == 0 ? (byte)Instruction.JUMP : (byte)Instruction.JUMPI);
            }
            else if (roll < 76)
            {
                Push1(random.Next(0, 96));
                code.Add(random.Next(6) switch
                {
                    0 => (byte)Instruction.MLOAD,
                    1 => (byte)Instruction.MSTORE,
                    2 => (byte)Instruction.MSTORE8,
                    3 => (byte)Instruction.KECCAK256,
                    4 => (byte)Instruction.MCOPY,
                    _ => (byte)Instruction.CALLDATACOPY,
                });
            }
            else if (roll < 82)
            {
                Push1(random.Next(0, 8));
                code.Add(random.Next(4) switch
                {
                    0 => (byte)Instruction.SLOAD,
                    1 => (byte)Instruction.SSTORE,
                    2 => (byte)Instruction.TLOAD,
                    _ => (byte)Instruction.TSTORE,
                });
            }
            else if (roll < 89)
            {
                if (random.Next(4) == 0)
                {
                    // EXTCODESIZE's peephole: it runs a following ISZERO, or a GT or EQ when the word under the address is zero.
                    if (random.Next(2) == 0) code.Add((byte)Instruction.PUSH0);
                    if (random.Next(3) == 0)
                    {
                        code.Add((byte)Instruction.PUSH20);
                        code.AddRange(Callee.Bytes);
                    }
                    else Push1(random.Next(0, 12));
                    code.Add((byte)Instruction.EXTCODESIZE);
                    code.Add(random.Next(3) switch
                    {
                        0 => (byte)Instruction.ISZERO,
                        1 => (byte)Instruction.GT,
                        _ => (byte)Instruction.EQ,
                    });
                }
                else
                {
                    code.Add((byte)EnvironmentOps[random.Next(EnvironmentOps.Length)]);
                }
            }
            else if (roll < 91)
            {
                Push1(random.Next(0, 40));
                Push1(random.Next(0, 40));
                code.Add((byte)(Instruction.LOG0 + (byte)random.Next(0, 5)));
            }
            else if (roll < 95)
            {
                // CALL family into the callee or a precompile, with small windows.
                bool hasValue = random.Next(2) == 0;
                Push1(random.Next(0, 64)); // retSize
                Push1(random.Next(0, 64)); // retOffset
                Push1(random.Next(0, 64)); // argsSize
                Push1(random.Next(0, 64)); // argsOffset
                if (hasValue) Push1(random.Next(0, 3));
                if (random.Next(3) == 0) Push1(random.Next(1, 11));
                else
                {
                    code.Add((byte)Instruction.PUSH20);
                    code.AddRange(Callee.Bytes);
                }
                if (random.Next(2) == 0) code.Add((byte)Instruction.GAS);
                else
                {
                    code.Add((byte)Instruction.PUSH2);
                    code.Add((byte)random.Next(0, 0x40));
                    code.Add((byte)random.Next(256));
                }
                code.Add(hasValue
                    ? random.Next(2) == 0 ? (byte)Instruction.CALL : (byte)Instruction.CALLCODE
                    : random.Next(2) == 0 ? (byte)Instruction.STATICCALL : (byte)Instruction.DELEGATECALL);
            }
            else if (roll < 97)
            {
                Push1(random.Next(0, 48));
                Push1(random.Next(0, 48));
                code.Add(random.Next(2) == 0 ? (byte)Instruction.RETURN : (byte)Instruction.REVERT);
            }
            else
            {
                code.Add(random.Next(4) switch
                {
                    0 => (byte)Instruction.STOP,
                    1 => (byte)Instruction.INVALID,
                    2 => (byte)Instruction.RETURNDATACOPY,
                    _ => (byte)random.Next(256),
                });
            }
        }

        byte[] bytes = code.ToArray();
        foreach (int at in targetFixups)
        {
            int target = jumpDests.Count > 0 && random.Next(8) > 0 ? jumpDests[random.Next(jumpDests.Count)] : random.Next(0, bytes.Length + 4);
            bytes[at] = (byte)(target >> 8);
            bytes[at + 1] = (byte)target;
        }

        return bytes;
    }
}
