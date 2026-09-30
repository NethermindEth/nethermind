// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// The per-depth child-frame cache of <see cref="VirtualMachine{TGasPolicy}"/>: a frame, its data stack and its
/// environment are reused at the same depth, a reused frame observes exactly what a fresh one would, and a frame
/// still in use is never shared.
/// </summary>
public class CallFrameCacheTests : VirtualMachineTestsBase
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDisposed")]
    private static extern ref bool IsDisposed(VmState<EthereumGasPolicy> state);

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

    private const ulong CallGas = 200_000;
    private const int ProbeInputLength = 36;
    private const int ProbeWords = 9;
    private const int ProbeOutputLength = ProbeWords * 32;

    private static readonly Address Probe = new("0x00000000000000000000000000000000000c0d01");
    private static readonly Address ProbeCaller = new("0x00000000000000000000000000000000000c0d02");
    private static readonly Address Underflow = new("0x00000000000000000000000000000000000c0d03");
    private static readonly Address Recursive = new("0x00000000000000000000000000000000000c0d04");
    private static readonly Address DirtyRevert = new("0x00000000000000000000000000000000000c0d05");
    private static readonly Address DirtyInvalid = new("0x00000000000000000000000000000000000c0d06");
    private static readonly Address DirtyOutOfGas = new("0x00000000000000000000000000000000000c0d07");
    private static readonly Address DirtyReturn = new("0x00000000000000000000000000000000000c0d08");
    private static readonly Address DirtyUnderflow = new("0x00000000000000000000000000000000000c0d09");
    private static readonly Address StaticWriter = new("0x00000000000000000000000000000000000c0d0a");
    private static readonly Address Eoa = new("0x00000000000000000000000000000000000c0d0b");

    private static readonly byte[] DirtyWord = Bytes.FromHexString("0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        Deploy(Probe, ProbeCode());
        Deploy(ProbeCaller, Prepare.EvmCode
            .CALLDATACOPY(0, 0, ProbeInputLength)
            .CALL(CallGas, Probe, 0, 0, ProbeInputLength, 0, ProbeOutputLength)
            .Op(Instruction.POP)
            .RETURN(0, ProbeOutputLength)
            .Done);
        Deploy(Underflow, [(byte)Instruction.POP, (byte)Instruction.STOP]);
        Deploy(Recursive, RecursiveCode());
        Deploy(DirtyRevert, Dirty().REVERT(0x200, 32).Done);
        Deploy(DirtyInvalid, Dirty().INVALID().Done);
        // Expanding memory to 1 GiB costs far more gas than any frame holds.
        Deploy(DirtyOutOfGas, Dirty().MLOAD(1 << 30).Done);
        Deploy(DirtyReturn, Dirty().RETURN(0x200, 32).Done);
        Deploy(DirtyUnderflow, Dirty().Op(Instruction.ADDMOD).Op(Instruction.ADDMOD).Op(Instruction.ADDMOD)
            .Op(Instruction.ADDMOD).Op(Instruction.ADDMOD).Op(Instruction.ADDMOD).Done);
        Deploy(StaticWriter, Prepare.EvmCode.SSTORE(1, [1]).Op(Instruction.STOP).Done);
        TestState.CreateAccount(Eoa, 1.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);
    }

    [Test]
    public void Child_frame_is_reused_per_depth_with_its_stack_and_environment([Values] bool traced)
    {
        byte[] code = Prepare.EvmCode
            .CALL(CallGas, Probe, 0, 0, ProbeInputLength, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Probe, 0, 0, ProbeInputLength, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, ProbeCaller, 0, 0, ProbeInputLength, 0, 0).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        FrameCaptureTracer first = Run(code, traced);
        VmState<EthereumGasPolicy> depth1 = Machine.FrameCache[1]!;
        byte[]? depth1Stack = depth1.DataStack;
        FrameCaptureTracer second = Run(code, traced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Error, Is.Null);
            Assert.That(first.Frames.Select(static f => f.Depth), Is.EqualTo(new[] { 1, 1, 1, 2 }));
            Assert.That(first.Frames.Where(static f => f.Depth == 1).Select(static f => f.Frame), Is.All.SameAs(depth1));
            Assert.That(first.Frames.Where(static f => f.Depth == 1).Select(static f => f.Env), Is.All.SameAs(Machine.EnvironmentCache[1]));
            Assert.That(first.Frames.Single(static f => f.Depth == 2).Frame, Is.SameAs(Machine.FrameCache[2]));
            Assert.That(first.Frames.Single(static f => f.Depth == 2).Env, Is.SameAs(Machine.EnvironmentCache[2]));
            Assert.That(Machine.FrameCache[0], Is.Null, "the top-level frame is not cached");

            // Across transactions: the same frame, still holding the same data stack.
            Assert.That(second.Error, Is.Null);
            Assert.That(second.Frames.Where(static f => f.Depth == 1).Select(static f => f.Frame), Is.All.SameAs(depth1));
            Assert.That(depth1Stack, Is.Not.Null);
            Assert.That(Machine.FrameCache[1]!.DataStack, Is.SameAs(depth1Stack));
            Assert.That(IsDisposed(depth1), Is.True, "a cached frame is disposed between uses");
            Assert.That(Machine.EnvironmentCache[1]!.ExecutingAccount, Is.Null, "a cached environment is cleared between uses");
        }
    }

    [Test]
    public void Frame_reused_after_a_failed_or_finished_sibling_starts_clean(
        [Values] bool traced,
        [Values("revert", "invalid", "out-of-gas", "stack-underflow", "return")] string ending)
    {
        Address dirty = ending switch
        {
            "revert" => DirtyRevert,
            "invalid" => DirtyInvalid,
            "out-of-gas" => DirtyOutOfGas,
            "stack-underflow" => DirtyUnderflow,
            _ => DirtyReturn,
        };
        const int probeOutput = 0x1000;
        const int driverWords = probeOutput + ProbeOutputLength;
        byte[] code = Prepare.EvmCode
            .MSTORE(0, DirtyWord)
            // Commits storage, transient storage and a log in the driver's context...
            .DELEGATECODE(CallGas, DirtyReturn, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Probe, 0, 0, ProbeInputLength, probeOutput, ProbeOutputLength).Op(Instruction.POP)
            // ...which a failing sibling on the same frame must not roll back: it restores to its own snapshot.
            .DELEGATECODE(CallGas, dirty, 0, 0, 0, 0).Op(Instruction.POP)
            // A stale stack head would let the POP succeed.
            .CALL(CallGas, Underflow, 0, 0, 0, 0, 0).PushData(driverWords).Op(Instruction.MSTORE)
            .SLOAD(7).PushData(driverWords + 32).Op(Instruction.MSTORE)
            .TLOAD(7).PushData(driverWords + 64).Op(Instruction.MSTORE)
            .RETURN(probeOutput, ProbeOutputLength + 96)
            .Done;

        FrameCaptureTracer tracer = Run(code, traced);

        UInt256[] expected =
        [
            0, // MSIZE: memory the sibling grew is gone
            0, // RETURNDATASIZE: nothing returned yet in this frame
            ProbeInputLength, // CALLDATASIZE
            0, // CALLVALUE
            AsWord(Recipient), // CALLER
            AsWord(Probe), // ADDRESS
            0, // MLOAD(0x200): the sibling's memory word does not show through
            0, // SLOAD(7) of the probe
            0, // TLOAD(7) of the probe
            0, // the underflow probe failed
            0x55, // the committed sibling's storage write
            0x66, // and its transient write
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null);
            Assert.That(Words(tracer.ReturnValue), Is.EqualTo(expected));
            Assert.That(tracer.Logs, Has.Length.EqualTo(ending == "return" ? 2 : 1));
            Assert.That(tracer.Frames.Where(static f => f.Depth == 1).Select(static f => f.Frame), Is.All.SameAs(Machine.FrameCache[1]),
                "every call from the driver reused the depth-1 frame");
            Assert.That(tracer.Frames.Where(static f => f.Depth == 2).Select(static f => f.Frame), Is.All.SameAs(Machine.FrameCache[2]));
        }
    }

    [Test]
    public void Frames_beyond_the_cached_depth_use_the_pool([Values] bool traced)
    {
        const int levels = VirtualMachineStatics.MaxCachedFrameDepth + 5;
        byte[] code = Prepare.EvmCode
            .MSTORE(0, ((UInt256)levels).ToBigEndian())
            .CALL(CallGas * 10, Recursive, 0, 0, 32, 0x20, 32)
            .PushData(0x40).Op(Instruction.MSTORE)
            .RETURN(0x20, 64)
            .Done;

        FrameCaptureTracer first = Run(code, traced, gasLimit: 5_000_000);
        FrameCaptureTracer second = Run(code, traced, gasLimit: 5_000_000);

        (UInt256 value, bool success) = RecursiveResult(levels);
        using (Assert.EnterMultipleScope())
        {
            foreach (FrameCaptureTracer tracer in new[] { first, second })
            {
                Assert.That(tracer.Error, Is.Null);
                Assert.That(Words(tracer.ReturnValue), Is.EqualTo(new[] { value, success ? UInt256.One : UInt256.Zero }));
                Assert.That(tracer.Frames.Select(static f => f.Depth), Is.EqualTo(Enumerable.Range(1, levels + 1)));
                foreach ((int depth, VmState<EthereumGasPolicy> frame, ExecutionEnvironment env) in tracer.Frames)
                {
                    if (depth <= VirtualMachineStatics.MaxCachedFrameDepth)
                    {
                        Assert.That(frame, Is.SameAs(Machine.FrameCache[depth]), $"depth {depth}");
                        Assert.That(env, Is.SameAs(Machine.EnvironmentCache[depth]), $"depth {depth}");
                    }
                    else
                    {
                        Assert.That(Machine.FrameCache, Has.None.SameAs(frame), $"depth {depth}");
                        Assert.That(Machine.EnvironmentCache, Has.None.SameAs(env), $"depth {depth}");
                    }
                }
            }

            Assert.That(Machine.FrameCache, Has.Length.EqualTo(VirtualMachineStatics.MaxCachedFrameDepth + 1));
        }
    }

    [Test]
    public void Frame_orphaned_by_an_exception_is_replaced_not_shared()
    {
        byte[] code = Prepare.EvmCode
            .CALLDATACOPY(0, 0, ProbeInputLength)
            .CALL(CallGas, Probe, 0, 0, ProbeInputLength, 0, ProbeOutputLength).Op(Instruction.POP)
            .RETURN(0, ProbeOutputLength)
            .Done;

        // Throws once CALL has staged the child frame and before the loop enters it, as a cancelled
        // CancellationTxTracer does from ReportActionRemainingGas: nothing disposes the staged frame.
        FrameCaptureTracer throwing = new(Machine, traced: false) { ThrowWhenFrameIsStaged = true };
        Assert.Throws<OperationCanceledException>(() => Run(code, throwing));
        VmState<EthereumGasPolicy> orphan = Machine.FrameCache[1]!;
        ExecutionEnvironment orphanEnv = Machine.EnvironmentCache[1]!;
        Assert.That(IsDisposed(orphan), Is.False, "the staged frame was orphaned");

        FrameCaptureTracer second = Run(code, traced: false);
        FrameCaptureTracer third = Run(code, traced: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Error, Is.Null);
            Assert.That(Words(second.ReturnValue)[5], Is.EqualTo(AsWord(Probe)));
            Assert.That(second.Frames.Single().Frame, Is.Not.SameAs(orphan));
            Assert.That(second.Frames.Single().Env, Is.Not.SameAs(orphanEnv));
            Assert.That(IsDisposed(orphan), Is.False, "the orphan is left alone rather than reset under its holder");
            Assert.That(orphanEnv.ExecutingAccount, Is.EqualTo(Probe));
            Assert.That(third.Frames.Single().Frame, Is.SameAs(second.Frames.Single().Frame), "the replacement is reused");
            Assert.That(third.ReturnValue, Is.EqualTo(second.ReturnValue));
        }
    }

    [TestCaseSource(nameof(DifferentialPrograms))]
    public void Call_heavy_program_matches_the_uncached_path(string name, bool traced)
    {
        byte[] code = BuildDifferentialProgram(name);
        (Block block, Transaction transaction) = PrepareTx(Activation, 7_000_000, code, value: 0);
        BlockExecutionContext context = new(block.Header, SpecProvider.GetSpec(block.Header));

        string cold = Describe(transaction, context, traced);
        string warm = Describe(transaction, context, traced);

        FieldInfo frames = typeof(VirtualMachine<EthereumGasPolicy>).GetField(nameof(VirtualMachine<EthereumGasPolicy>.FrameCache), BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo envs = typeof(VirtualMachine<EthereumGasPolicy>).GetField(nameof(VirtualMachine<EthereumGasPolicy>.EnvironmentCache), BindingFlags.Instance | BindingFlags.NonPublic)!;
        object cachedFrames = frames.GetValue(Machine)!;
        object cachedEnvs = envs.GetValue(Machine)!;
        string uncached;
        try
        {
            frames.SetValue(Machine, Array.Empty<VmState<EthereumGasPolicy>?>());
            envs.SetValue(Machine, Array.Empty<ExecutionEnvironment?>());
            uncached = Describe(transaction, context, traced);
        }
        finally
        {
            frames.SetValue(Machine, cachedFrames);
            envs.SetValue(Machine, cachedEnvs);
        }

        string warmAgain = Describe(transaction, context, traced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cold, Does.Contain("status=1"), "the program is expected to succeed as a whole");
            Assert.That(cold, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
            Assert.That(warmAgain, Is.EqualTo(uncached));
            Assert.That(Machine.FrameCache[1], Is.Not.Null, "the cached path was exercised");
            Assert.That(Machine.FrameCache[2], Is.Not.Null, "the cached path was exercised");
        }
    }

    private static IEnumerable<TestCaseData> DifferentialPrograms()
    {
        foreach (string name in new[] { "siblings", "recursion", "kinds", "create" })
        {
            yield return new TestCaseData(name, false).SetArgDisplayNames(name, "untraced");
            yield return new TestCaseData(name, true).SetArgDisplayNames(name, "traced");
        }
    }

    private static byte[] BuildDifferentialProgram(string name)
    {
        DriverBuilder driver = new();
        switch (name)
        {
            case "siblings":
                foreach (Address dirty in new[] { DirtyRevert, DirtyInvalid, DirtyOutOfGas, DirtyUnderflow, DirtyReturn })
                {
                    driver.Call(Instruction.CALL, dirty);
                    driver.Call(Instruction.CALL, Probe);
                    driver.Call(Instruction.CALL, dirty);
                    driver.Call(Instruction.CALL, Underflow);
                    driver.Call(Instruction.CALL, ProbeCaller);
                }
                break;
            case "recursion":
                driver.Call(Instruction.CALL, Recursive, input: VirtualMachineStatics.MaxCachedFrameDepth + 4, gas: CallGas * 10);
                driver.Call(Instruction.CALL, Probe);
                driver.Call(Instruction.CALL, Recursive, input: 3);
                driver.Call(Instruction.CALL, Recursive, input: 10, gas: CallGas * 10);
                driver.Call(Instruction.STATICCALL, Recursive, input: 2);
                driver.Call(Instruction.CALL, ProbeCaller);
                break;
            case "kinds":
                driver.Call(Instruction.DELEGATECALL, Probe);
                driver.Call(Instruction.CALLCODE, Probe, value: 1);
                driver.Call(Instruction.STATICCALL, Probe);
                driver.Call(Instruction.CALL, Probe, value: 5);
                driver.Call(Instruction.STATICCALL, StaticWriter);
                driver.Call(Instruction.CALL, new Address("0x0000000000000000000000000000000000000004"));
                driver.Call(Instruction.STATICCALL, new Address("0x0000000000000000000000000000000000000002"));
                driver.Call(Instruction.CALL, new Address("0x0000000000000000000000000000000000000001"));
                driver.Call(Instruction.CALL, Eoa, value: 7);
                driver.Call(Instruction.DELEGATECALL, DirtyRevert);
                driver.Call(Instruction.CALLCODE, ProbeCaller);
                driver.Call(Instruction.CALL, Probe);
                break;
            case "create":
                byte[] probingInit = Prepare.EvmCode
                    .CALL(CallGas, Probe, 0, 0, 0, 0, ProbeOutputLength).Op(Instruction.POP)
                    .CALL(CallGas, DirtyRevert, 0, 0, 0, 0, 0).Op(Instruction.POP)
                    .RETURN(0, 1)
                    .Done;
                byte[] revertingInit = Dirty().CALL(CallGas, Probe, 0, 0, 0, 0, 0).Op(Instruction.POP).REVERT(0x200, 32).Done;
                driver.Create(Instruction.CREATE, probingInit);
                driver.Create(Instruction.CREATE2, revertingInit);
                driver.Call(Instruction.CALL, Probe);
                driver.Create(Instruction.CREATE2, probingInit);
                driver.Call(Instruction.CALL, ProbeCaller);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }

        return driver.Done();
    }

    /// <summary>Runs the transaction without committing and renders everything a consumer could observe of it.</summary>
    private string Describe(Transaction transaction, in BlockExecutionContext context, bool traced)
    {
        StringBuilder text = new();
        if (traced)
        {
            GethLikeTxMemoryTracer tracer = new(transaction, GethTraceOptions.Default with { EnableMemory = true });
            _processor.CallAndRestore(transaction, context, tracer);
            GethLikeTxTrace trace = tracer.BuildResult();
            text.Append($"status={(trace.Failed ? 0 : 1)} gas={trace.Gas} out={Convert.ToHexString(trace.ReturnValue)}\n");
            foreach (GethTxTraceEntry entry in trace.Entries)
            {
                text.Append($"{entry.Depth} {entry.ProgramCounter} {entry.Opcode} {entry.Gas} {entry.GasCost} {entry.Error} ");
                text.Append(entry.Stack is { } stack ? Convert.ToHexString(stack.Span) : "-").Append(' ');
                text.Append(entry.Memory is { } memory ? Convert.ToHexString(memory.Span) : "-").Append('\n');
            }
        }
        else
        {
            FrameCaptureTracer tracer = new(Machine, traced: false) { CaptureFrames = false };
            _processor.CallAndRestore(transaction, context, tracer);
            text.Append($"status={tracer.StatusCode} gas={tracer.GasSpent} error={tracer.Error} out={Convert.ToHexString(tracer.ReturnValue ?? [])}\n");
            foreach (LogEntry log in tracer.Logs)
            {
                text.Append($"log {log.Address} {string.Join(",", log.Topics.Select(static t => t.ToString()))} {Convert.ToHexString(log.Data)}\n");
            }
        }

        return text.ToString();
    }

    private FrameCaptureTracer Run(byte[] code, bool traced, ulong gasLimit = 3_000_000) =>
        Run(code, new FrameCaptureTracer(Machine, traced), gasLimit);

    private FrameCaptureTracer Run(byte[] code, FrameCaptureTracer tracer, ulong gasLimit = 3_000_000)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code, value: 0);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer;
    }

    private void Deploy(Address address, byte[] code)
    {
        TestState.CreateAccount(address, 1.Ether);
        TestState.InsertCode(address, code, SpecProvider.GenesisSpec);
    }

    /// <summary>Observes the frame on entry, before touching memory, and returns <see cref="ProbeWords"/> words.</summary>
    private static byte[] ProbeCode()
    {
        Prepare probe = Prepare.EvmCode
            .Op(Instruction.MSIZE)
            .Op(Instruction.RETURNDATASIZE)
            .Op(Instruction.CALLDATASIZE)
            .Op(Instruction.CALLVALUE)
            .Op(Instruction.CALLER)
            .Op(Instruction.ADDRESS)
            .MLOAD(0x200)
            .SLOAD(7)
            .TLOAD(7);
        for (int word = ProbeWords - 1; word >= 0; word--)
        {
            probe.PushData(word * 32).Op(Instruction.MSTORE);
        }

        return probe.RETURN(0, ProbeOutputLength).Done;
    }

    /// <summary>Leaves memory, storage, transient storage, a log, return data and stack items behind.</summary>
    private static Prepare Dirty() => Prepare.EvmCode
        .CALL(CallGas / 4, Probe, 0, 0, 0, 0x300, 32).Op(Instruction.POP)
        .MSTORE(0x200, DirtyWord)
        .SSTORE(7, [0x55])
        .TSTORE(7, [0x66])
        .LOGx(0, 0x200, 32)
        .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5);

    /// <summary>
    /// Calls itself with n - 1 until n is 0, returning r = child + 1 + 1000 * childSucceeded, and reverts with r when
    /// n is a multiple of 3; every level writes memory, storage and a log first.
    /// </summary>
    private static byte[] RecursiveCode()
    {
        List<byte> code = [];
        void Op(params Instruction[] ops) => code.AddRange(ops.Select(static o => (byte)o));
        void Push1(byte value) { Op(Instruction.PUSH1); code.Add(value); }
        void Push2(int value) { Op(Instruction.PUSH2); code.Add((byte)(value >> 8)); code.Add((byte)value); }

        Op(Instruction.PUSH0, Instruction.CALLDATALOAD); // [n]
        Op(Instruction.DUP1, Instruction.ISZERO);
        int zeroJump = code.Count; Push2(0); Op(Instruction.JUMPI);
        Push1(1); Op(Instruction.DUP2, Instruction.SUB, Instruction.PUSH0, Instruction.MSTORE); // mem[0] = n - 1
        Op(Instruction.DUP1); Push2(0x300); Op(Instruction.MSTORE); // mem[0x300] = n
        Op(Instruction.DUP1, Instruction.DUP1, Instruction.SSTORE); // storage[n] = n
        Push1(32); Push2(0x300); Op(Instruction.LOG0);
        Push1(32); Push1(0x20); Push1(32); Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.ADDRESS, Instruction.GAS, Instruction.CALL);
        Push2(1000); Op(Instruction.MUL); Push1(0x20); Op(Instruction.MLOAD, Instruction.ADD); Push1(1); Op(Instruction.ADD);
        Push1(0x40); Op(Instruction.MSTORE); // mem[0x40] = r
        Push1(3); Op(Instruction.SWAP1, Instruction.MOD);
        int returnJump = code.Count; Push2(0); Op(Instruction.JUMPI);
        Push1(32); Push1(0x40); Op(Instruction.REVERT);
        int returnDest = code.Count; Op(Instruction.JUMPDEST); Push1(32); Push1(0x40); Op(Instruction.RETURN);
        int zeroDest = code.Count; Op(Instruction.JUMPDEST, Instruction.PUSH0, Instruction.PUSH0, Instruction.MSTORE); Push1(32); Op(Instruction.PUSH0, Instruction.RETURN);

        byte[] bytes = [.. code];
        bytes[zeroJump + 1] = (byte)(zeroDest >> 8); bytes[zeroJump + 2] = (byte)zeroDest;
        bytes[returnJump + 1] = (byte)(returnDest >> 8); bytes[returnJump + 2] = (byte)returnDest;
        return bytes;
    }

    private static (UInt256 Value, bool Success) RecursiveResult(int n)
    {
        if (n == 0) return (0, true);
        (UInt256 child, bool childSucceeded) = RecursiveResult(n - 1);
        return (child + 1 + (childSucceeded ? 1000u : 0u), n % 3 != 0);
    }

    private static UInt256 AsWord(Address address) => new(address.Bytes.PadLeft(32), isBigEndian: true);

    private static UInt256[] Words(byte[]? data)
    {
        Assert.That(data, Is.Not.Null);
        UInt256[] words = new UInt256[data!.Length / 32];
        for (int i = 0; i < words.Length; i++) words[i] = new UInt256(data.AsSpan(i * 32, 32), isBigEndian: true);
        return words;
    }

    /// <summary>Emits calls that each log their success flag, return data size and return data to memory.</summary>
    private sealed class DriverBuilder
    {
        private const int LogStart = 0x1000;
        private const int EntrySize = 64 + ProbeOutputLength;
        private readonly Prepare _code = Prepare.EvmCode.MSTORE(0, DirtyWord).MSTORE(32, DirtyWord);
        private int _entries;

        public void Call(Instruction kind, Address target, int value = 0, int? input = null, ulong gas = CallGas)
        {
            if (input is { } word) _code.MSTORE(0, ((UInt256)word).ToBigEndian());
            UInt256 inputLength = input is null ? (UInt256)ProbeInputLength : 32;
            UInt256 callValue = (UInt256)value;
            _ = kind switch
            {
                Instruction.CALL => _code.CALL(gas, target, callValue, 0, inputLength, 0, 0),
                Instruction.CALLCODE => _code.CALLCODE(gas, target, callValue, 0, inputLength, 0, 0),
                Instruction.DELEGATECALL => _code.DELEGATECODE(gas, target, 0, inputLength, 0, 0),
                Instruction.STATICCALL => _code.STATICCALL(gas, target, 0, inputLength, 0, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            LogResult();
            if (input is not null) _code.MSTORE(0, DirtyWord);
        }

        public void Create(Instruction kind, byte[] initCode)
        {
            _code.StoreDataInMemory(0x800, initCode);
            if (kind == Instruction.CREATE2) _code.PushData(_entries);
            _code.PushData(initCode.Length).PushData(0x800).PushData(0).Op(kind);
            LogResult();
        }

        private void LogResult()
        {
            int entry = LogStart + _entries++ * EntrySize;
            _code.PushData(entry).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).PushData(entry + 32).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).Op(Instruction.PUSH0).PushData(entry + 64).Op(Instruction.RETURNDATACOPY);
        }

        public byte[] Done() => _code.RETURN(LogStart, (UInt256)(_entries * EntrySize)).Done;
    }

    private sealed class FrameCaptureTracer(EthereumVirtualMachine machine, bool traced) : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public override bool IsTracingActions => CaptureFrames;
        public override bool IsTracingInstructions => traced;

        public bool CaptureFrames { get; init; } = true;
        public bool ThrowWhenFrameIsStaged { get; init; }

        public List<(int Depth, VmState<EthereumGasPolicy> Frame, ExecutionEnvironment Env)> Frames { get; } = [];
        public byte[]? ReturnValue { get; private set; }
        public ulong GasSpent { get; private set; }
        public string? Error { get; private set; }
        public byte StatusCode { get; private set; }
        public LogEntry[] Logs { get; private set; } = [];

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to,
            ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            if (callType == ExecutionType.TRANSACTION) return;
            VmState<EthereumGasPolicy> frame = machine.VmState;
            Frames.Add((frame.Env.CallDepth, frame, frame.Env));
        }

        public override void ReportActionRemainingGas(ulong gas)
        {
            if (ThrowWhenFrameIsStaged && machine.ReturnData is VmState<EthereumGasPolicy>)
            {
                throw new OperationCanceledException("Cancelled with a child frame staged.");
            }
        }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            ReturnValue = output;
            Logs = logs;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            ReturnValue = output;
            Error = error;
            StatusCode = Evm.StatusCode.Failure;
        }
    }
}
