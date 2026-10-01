// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Autofac;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// The per-depth child-frame cache of <see cref="VirtualMachine{TGasPolicy}"/>: a frame, its data stack and its
/// environment are reused at the same depth, and a reused frame observes exactly what a fresh one would - nothing of
/// its previous user's memory, stack, refunds, access-list warmth, static or continuation flag, or EIP-8037
/// bookkeeping. A frame still in use is never shared: frames unwound by an exception are disposed and kept, frames
/// orphaned by one are replaced. Call-heavy and seeded random programs match the uncached path. Disposing the VM hands
/// the kept data stacks back to the pool.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
public class CallFrameCacheTests(bool amsterdam) : VirtualMachineTestsBase
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDisposed")]
    private static extern ref bool IsDisposed(VmState<EthereumGasPolicy> state);

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => amsterdam ? MainnetSpecProvider.AmsterdamBlockTimestamp : MainnetSpecProvider.OsakaBlockTimestamp;

    private const ulong CallGas = 200_000;
    private const ulong TxGas = 7_000_000;
    private const int ProbeInputLength = 36;
    private const int ProbeWords = 11;
    private const int ProbeOutputLength = ProbeWords * 32;
    /// <summary>The level modulus <see cref="Recursive"/> is given by the fixed programs: every third level reverts.</summary>
    private const int RevertEvery = 3;

    private static readonly Address Probe = new("0x00000000000000000000000000000000000c0d01");
    private static readonly Address ProbeCaller = new("0x00000000000000000000000000000000000c0d02");
    private static readonly Address Underflow = new("0x00000000000000000000000000000000000c0d03");
    private static readonly Address Recursive = new("0x00000000000000000000000000000000000c0d04");
    private static readonly Address DirtyRevert = new("0x00000000000000000000000000000000000c0d05");
    private static readonly Address DirtyInvalid = new("0x00000000000000000000000000000000000c0d06");
    private static readonly Address DirtyOutOfGas = new("0x00000000000000000000000000000000000c0d07");
    private static readonly Address DirtyReturn = new("0x00000000000000000000000000000000000c0d08");
    private static readonly Address DirtyUnderflow = new("0x00000000000000000000000000000000000c0d09");
    private static readonly Address Eoa = new("0x00000000000000000000000000000000000c0d0b");
    private static readonly Address Warmer = new("0x00000000000000000000000000000000000c1e01");
    private static readonly Address Clearer = new("0x00000000000000000000000000000000000c1e02");
    private static readonly Address Stopper = new("0x00000000000000000000000000000000000c1e03");
    private static readonly Address Writer = new("0x00000000000000000000000000000000000c1e04");
    private static readonly Address Valued = new("0x00000000000000000000000000000000000c1e05");
    private static readonly Address Hopper = new("0x00000000000000000000000000000000000c1e06");
    private static readonly Address Chain = new("0x00000000000000000000000000000000000c1e07");
    private static readonly Address Missing = new("0x00000000000000000000000000000000000c1e0b");
    private static readonly Address ColdX = new("0x00000000000000000000000000000000000c1e0f");
    private static readonly Address ColdY = new("0x00000000000000000000000000000000000c1e0e");
    private static readonly Address DirtyStop = new("0x00000000000000000000000000000000000c1e14");

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
        Deploy(Chain, ChainCode());
        Deploy(DirtyRevert, DirtyPrefix().REVERT(0x200, 32).Done);
        Deploy(DirtyInvalid, DirtyPrefix().INVALID().Done);
        // Expanding memory to 1 GiB costs far more gas than any frame holds.
        Deploy(DirtyOutOfGas, DirtyPrefix().MLOAD(1 << 30).Done);
        Deploy(DirtyReturn, DirtyPrefix().RETURN(0x200, 32).Done);
        Deploy(DirtyStop, DirtyPrefix().Op(Instruction.STOP).Done);
        Deploy(DirtyUnderflow, DirtyPrefix().Op(Instruction.ADDMOD).Op(Instruction.ADDMOD).Op(Instruction.ADDMOD)
            .Op(Instruction.ADDMOD).Op(Instruction.ADDMOD).Op(Instruction.ADDMOD).Done);
        Deploy(Warmer, WarmerCode());
        Deploy(Clearer, Prepare.EvmCode.SSTORE(5, [0]).Op(Instruction.STOP).Done);
        TestState.Set(new StorageCell(Clearer, 5), (UInt256)1);
        Deploy(Stopper, [(byte)Instruction.STOP]);
        Deploy(Writer, Prepare.EvmCode.SSTORE(1, [1]).Op(Instruction.STOP).Done);
        Deploy(Valued, Prepare.EvmCode
            .Op(Instruction.SELFBALANCE).PushData(0).Op(Instruction.MSTORE)
            .Op(Instruction.CALLVALUE).PushData(32).Op(Instruction.MSTORE)
            .Op(Instruction.RETURNDATASIZE).PushData(64).Op(Instruction.MSTORE)
            .RETURN(0, 96).Done);
        Deploy(Hopper, Prepare.EvmCode.CALL(CallGas / 4, Valued, 0, 0, 0, 0x300, 96).Op(Instruction.POP).RETURN(0x300, 32).Done);
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

        RecordingTracer first = Run(code, traced);
        VmState<EthereumGasPolicy> depth1 = Machine.FrameCache[1]!;
        byte[]? depth1Stack = depth1.DataStack;
        RecordingTracer second = Run(code, traced);

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

        RecordingTracer tracer = Run(code, traced);

        UInt256[] expected =
        [
            0, // MSIZE: memory the sibling grew is gone
            0, // RETURNDATASIZE: nothing returned yet in this frame
            ProbeInputLength, // CALLDATASIZE
            0, // CALLVALUE
            AsWord(Recipient), // CALLER
            AsWord(Probe), // ADDRESS
            1.Ether, // SELFBALANCE
            new UInt256(DirtyWord, isBigEndian: true), // CALLDATALOAD(0): this call's input
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
            .MSTORE(0, ((UInt256)levels).ToBigEndian()).MSTORE(32, ((UInt256)RevertEvery).ToBigEndian())
            .CALL(CallGas * 10, Recursive, 0, 0, 64, 0x20, 32)
            .PushData(0x40).Op(Instruction.MSTORE)
            .RETURN(0x20, 64)
            .Done;

        RecordingTracer first = Run(code, traced);
        RecordingTracer second = Run(code, traced);

        (UInt256 value, bool success) = RecursiveResult(levels, RevertEvery);
        using (Assert.EnterMultipleScope())
        {
            foreach (RecordingTracer tracer in new[] { first, second })
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
        RecordingTracer throwing = new(Machine) { CancelWhenStagedAtDepth = 1 };
        Assert.Throws<OperationCanceledException>(() => Run(code, throwing));
        Assert.That(throwing.Orphan, Is.Not.Null, "the tracer cancelled with a child frame staged");
        VmState<EthereumGasPolicy> orphan = throwing.Orphan!;
        ExecutionEnvironment orphanEnv = throwing.OrphanEnv!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsDisposed(orphan), Is.False, "the staged frame was orphaned");
            Assert.That(Machine.FrameCache[1], Is.Null, "the unwind empties the orphan's slot");
            Assert.That(Machine.EnvironmentCache[1], Is.Null, "and its environment's");
            Assert.That(Machine.ReturnData, Is.Not.SameAs(orphan), "and drops the staged frame from the return data");
        }

        RecordingTracer second = Run(code, traced: false);
        RecordingTracer third = Run(code, traced: false);

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

    /// <summary>
    /// <see cref="VmState{TGasPolicy}.Dispose"/> marks a frame disposed before it resets anything. If a later step
    /// throws - here the access-journal restore, with the journals recycled under the frame - the frame is left with
    /// its memory still sized and written, and the cache must replace it rather than hand it to the next child.
    /// </summary>
    [Test]
    public void Frame_whose_disposal_throws_midway_is_replaced_not_reused()
    {
        VmState<EthereumGasPolicy>?[] frames = new VmState<EthereumGasPolicy>?[VirtualMachineStatics.MaxCachedFrameDepth + 1];
        ExecutionEnvironment?[] envs = new ExecutionEnvironment?[frames.Length];

        StackAccessTracker tracker = new();
        tracker.WarmUp(Probe);
        VmState<EthereumGasPolicy> broken = RentChild(frames, envs, in tracker);
        UInt256 location = 0x40;
        Assert.That(broken.Memory.TrySaveWord(in location, DirtyWord), Is.True);
        Assert.That(broken.Memory.Size, Is.EqualTo(0x60));

        // Clearing the journals the frame snapshotted makes its restore throw inside Dispose.
        tracker.Dispose();
        Assert.Throws<InvalidOperationException>(broken.Dispose);
        Assert.That(IsDisposed(broken), Is.True, "Dispose marked the frame before the step that threw");

        StackAccessTracker next = new();
        try
        {
            VmState<EthereumGasPolicy> child = RentChild(frames, envs, in next);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(child, Is.Not.SameAs(broken), "a frame whose disposal threw is not handed out again");
                Assert.That(child.Memory.Size, Is.Zero, "the next child starts with empty memory");
                Assert.That(frames[1], Is.SameAs(child), "the replacement takes the slot");
            }

            child.Dispose();
            VmState<EthereumGasPolicy> reused = RentChild(frames, envs, in next);
            Assert.That(reused, Is.SameAs(child), "a frame disposed to the end is reused");
            reused.Dispose();
        }
        finally
        {
            next.Dispose();
        }

        static VmState<EthereumGasPolicy> RentChild(VmState<EthereumGasPolicy>?[] frames, ExecutionEnvironment?[] envs, in StackAccessTracker tracker)
        {
            ExecutionEnvironment env = ExecutionEnvironment.Rent(
                envs, new CodeInfo(new byte[] { (byte)Instruction.STOP }), Probe, ProbeCaller, codeSource: null, callDepth: 1,
                UInt256.Zero, ReadOnlyMemory<byte>.Empty);
            return VmState<EthereumGasPolicy>.RentFrame(
                frames, EthereumGasPolicy.FromULong(CallGas), outputDestination: 0, outputLength: 0, ExecutionType.CALL,
                isStatic: false, isCreateOnPreExistingAccount: false, env, in tracker, Snapshot.Empty);
        }
    }

    [TestCaseSource(nameof(DifferentialPrograms))]
    public void Call_heavy_program_matches_the_uncached_path(string name, bool traced)
    {
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(BuildDifferentialProgram(name));

        string cold = Observe(transaction, context, traced, actions: false);
        string warm = Observe(transaction, context, traced, actions: false);
        string uncached = Uncached(() => Observe(transaction, context, traced, actions: false));
        string warmAgain = Observe(transaction, context, traced, actions: false);

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
                driver.Call(Instruction.CALL, Recursive, levels: VirtualMachineStatics.MaxCachedFrameDepth + 4, gas: CallGas * 10);
                driver.Call(Instruction.CALL, Probe);
                driver.Call(Instruction.CALL, Recursive, levels: 3);
                driver.Call(Instruction.CALL, Recursive, levels: 10, gas: CallGas * 10);
                driver.Call(Instruction.STATICCALL, Recursive, levels: 2);
                driver.Call(Instruction.CALL, ProbeCaller);
                break;
            case "kinds":
                driver.Call(Instruction.DELEGATECALL, Probe);
                driver.Call(Instruction.CALLCODE, Probe, value: 1);
                driver.Call(Instruction.STATICCALL, Probe);
                driver.Call(Instruction.CALL, Probe, value: 5);
                driver.Call(Instruction.STATICCALL, Writer);
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
                byte[] revertingInit = DirtyPrefix().CALL(CallGas, Probe, 0, 0, 0, 0, 0).Op(Instruction.POP).REVERT(0x200, 32).Done;
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

    /// <summary>
    /// A committed sibling's refund stays on its (now disposed) frame object; the next child that reuses the frame
    /// must start from zero or the refund is counted twice.
    /// </summary>
    [Test]
    public void Refund_of_a_committed_sibling_is_not_counted_again_by_the_frame_reusing_it()
    {
        byte[] code = Prepare.EvmCode
            .SSTORE(1, [1]).SSTORE(2, [1]) // raise gas used so the refund cap does not hide a doubled refund
            .CALL(CallGas, Clearer, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        string cached = Observe(code, traced: false);
        string warm = Observe(code, traced: false);
        string uncached = Uncached(() => Observe(code, traced: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cached, Does.StartWith("status=1"));
            Assert.That(cached, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
        }
    }

    /// <summary>
    /// EIP-2929: warmth added by a reverted call is rolled back, also when the reused frame's previous user
    /// committed (and so was marked as not restorable).
    /// </summary>
    [Test]
    public void Access_warmth_of_a_reverted_sibling_is_rolled_back_on_the_reused_frame([Values] bool traced)
    {
        byte[] code = Prepare.EvmCode
            .MSTORE(0, ((UInt256)10).ToBigEndian()).MSTORE(32, ColdY.Bytes.PadLeft(32)).MSTORE(64, UInt256.Zero.ToBigEndian())
            .CALL(CallGas, Warmer, 0, 0, 96, 0, 0).Op(Instruction.POP) // warms slot 10 and ColdY, commits
            .MSTORE(0, ((UInt256)9).ToBigEndian()).MSTORE(32, ColdX.Bytes.PadLeft(32)).MSTORE(64, UInt256.One.ToBigEndian())
            .CALL(CallGas, Warmer, 0, 0, 96, 0, 0).Op(Instruction.POP) // warms slot 9 and ColdX, reverts
            .CALL(CallGas, Warmer, 0, 0, 0, 0x100, 128).Op(Instruction.POP) // measures
            .RETURN(0x100, 128)
            .Done;

        UInt256[] first = Words(RunAndRestore(code, traced));
        UInt256[] second = Words(RunAndRestore(code, traced));
        UInt256[] uncached = Uncached(() => Words(RunAndRestore(code, traced)));

        using (Assert.EnterMultipleScope())
        {
            // slot 9 and ColdX were warmed by the reverted call only, slot 10 and ColdY by the committed one
            if (!amsterdam) Assert.That(first, Is.EqualTo(new UInt256[] { 2107, 2607, 107, 107 }));
            Assert.That(first[0], Is.GreaterThan(first[2]), "slot 9 is cold again");
            Assert.That(first[1], Is.GreaterThan(first[3]), "ColdX is cold again");
            Assert.That(second, Is.EqualTo(first));
            Assert.That(uncached, Is.EqualTo(first));
        }
    }

    [Test]
    public void Static_context_does_not_stick_to_the_reused_frame([Values] bool traced)
    {
        byte[] code = Prepare.EvmCode
            .STATICCALL(CallGas, Writer, 0, 0, 0, 0).PushData(0).Op(Instruction.MSTORE)
            .CALL(CallGas, Writer, 0, 0, 0, 0, 0).PushData(32).Op(Instruction.MSTORE)
            .STATICCALL(CallGas, Writer, 0, 0, 0, 0).PushData(64).Op(Instruction.MSTORE)
            .CALL(CallGas, Writer, 0, 0, 0, 0, 0).PushData(96).Op(Instruction.MSTORE)
            .RETURN(0, 128)
            .Done;

        UInt256[] first = Words(RunAndRestore(code, traced));
        UInt256[] second = Words(RunAndRestore(code, traced));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(new UInt256[] { 0, 1, 0, 1 }));
            Assert.That(second, Is.EqualTo(new UInt256[] { 0, 1, 0, 1 }));
        }
    }

    /// <summary>
    /// A frame that made a call ends as a continuation; the next child reusing it must be entered as a fresh
    /// frame: value transferred, return data cleared.
    /// </summary>
    [Test]
    public void Frame_left_as_a_continuation_is_entered_fresh_with_its_value_transfer([Values] bool traced)
    {
        byte[] code = Prepare.EvmCode
            .CALL(CallGas, Hopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Valued, 3, 0, 0, 0x100, 96).Op(Instruction.POP)
            .CALL(CallGas, Hopper, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, Valued, 4, 0, 0, 0x160, 96).Op(Instruction.POP)
            .RETURN(0x100, 192)
            .Done;

        UInt256 initial = 1.Ether;
        UInt256[] expected = [initial + 3, 3, 0, initial + 7, 4, 0];
        UInt256[] first = Words(RunAndRestore(code, traced));
        UInt256[] second = Words(RunAndRestore(code, traced));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(expected));
            Assert.That(second, Is.EqualTo(expected));
        }
    }

    /// <summary>
    /// An exception unwinding through entered frames (a cancelled tracer) goes through the frame cleanup scope: the
    /// cached frames are disposed, not orphaned, and serve the next transaction.
    /// </summary>
    [Test]
    public void Frames_unwound_by_a_cancellation_are_disposed_and_reused([Values("action", "instruction")] string where)
    {
        byte[] code = ChainDriver(5);
        Assert.That(RunAndRestore(code, traced: false), Is.Not.Null);
        VmState<EthereumGasPolicy>[] before = Enumerable.Range(1, 5).Select(d => Machine.FrameCache[d]!).ToArray();
        Assert.That(before, Has.None.Null, "the first run cached a frame at every depth");

        RecordingTracer throwing = new(Machine)
        {
            ThrowAtActionDepth = where == "action" ? 3 : -1,
            ThrowAtOpcodeDepth = where == "instruction" ? 3 : -1,
        };
        Assert.Throws<OperationCanceledException>(() => RunAndRestore(code, throwing));

        using (Assert.EnterMultipleScope())
        {
            for (int depth = 1; depth <= 5; depth++)
            {
                Assert.That(Machine.FrameCache[depth], Is.SameAs(before[depth - 1]), $"depth {depth} kept");
                Assert.That(IsDisposed(before[depth - 1]), Is.True, $"depth {depth} disposed by the unwind");
                Assert.That(Machine.EnvironmentCache[depth]!.ExecutingAccount, Is.Null, $"env at depth {depth} released");
            }
        }

        RecordingTracer normal = new(Machine);
        byte[]? output = RunAndRestore(code, normal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(normal.Error, Is.Null);
            Assert.That(output, Is.EqualTo(Uncached(() => RunAndRestore(code, traced: false))));
            for (int depth = 1; depth <= 5; depth++)
            {
                Assert.That(normal.Frames.Where(f => f.Depth == depth).Select(static f => f.Frame), Is.All.SameAs(before[depth - 1]));
            }
        }
    }

    /// <summary>
    /// A cancellation thrown after CALL staged a reused cached frame and before the loop entered it unwinds the
    /// transaction past a frame nothing disposes. The unwind empties that frame's slot and its environment's without
    /// disposing either, keeps the frames it did dispose, and the next transaction gets a fresh frame at that depth.
    /// </summary>
    [Test]
    public void Frame_orphaned_by_a_cancellation_leaves_the_cache_with_the_transaction([Values] bool traced)
    {
        const int orphanDepth = 3;
        byte[] code = ChainDriver(5);
        Assert.That(RunAndRestore(code, traced), Is.Not.Null);
        VmState<EthereumGasPolicy>[] before = Enumerable.Range(1, 6).Select(d => Machine.FrameCache[d]!).ToArray();
        Assert.That(before, Has.None.Null, "the first run cached a frame at every depth");

        RecordingTracer cancelling = new(Machine) { CancelWhenStagedAtDepth = orphanDepth, Traced = traced };
        Assert.Throws<OperationCanceledException>(() => RunAndRestore(code, cancelling));
        VmState<EthereumGasPolicy> orphan = cancelling.Orphan!;
        ExecutionEnvironment orphanEnv = cancelling.OrphanEnv!;
        Assert.That(orphan, Is.SameAs(before[orphanDepth - 1]), "the cached frame was staged again and orphaned");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(orphan.DataStack, Is.Not.Null, "with the data stack it kept from its previous use");
            Assert.That(IsDisposed(orphan), Is.False, "the orphan is not disposed");
            Assert.That(orphanEnv.ExecutingAccount, Is.EqualTo(Chain), "nor is its environment");
            Assert.That(Machine.FrameCache[orphanDepth], Is.Null, "the orphan's slot is emptied");
            Assert.That(Machine.EnvironmentCache[orphanDepth], Is.Null, "and its environment's");
            Assert.That(Machine.ReturnData, Is.Not.SameAs(orphan), "the VM no longer holds the staged frame");
            foreach (int depth in new[] { 1, 2, 4, 5, 6 })
            {
                Assert.That(Machine.FrameCache[depth], Is.SameAs(before[depth - 1]), $"depth {depth} kept");
                Assert.That(IsDisposed(before[depth - 1]), Is.True, $"depth {depth} released");
                Assert.That(Machine.EnvironmentCache[depth]!.ExecutingAccount, Is.Null, $"env at depth {depth} released");
            }
        }

        RecordingTracer normal = new(Machine) { Traced = traced };
        byte[]? output = RunAndRestore(code, normal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(normal.Error, Is.Null);
            Assert.That(output, Is.EqualTo(Uncached(() => RunAndRestore(code, traced))));
            Assert.That(Machine.FrameCache[orphanDepth], Is.Not.Null.And.Not.SameAs(orphan), "a fresh frame takes the depth");
            Assert.That(Machine.EnvironmentCache[orphanDepth], Is.Not.Null.And.Not.SameAs(orphanEnv));
            Assert.That(normal.Frames.Where(static f => f.Depth == orphanDepth).Select(static f => f.Frame),
                Is.All.SameAs(Machine.FrameCache[orphanDepth]));
            foreach (int depth in new[] { 1, 2, 4, 5, 6 })
            {
                Assert.That(normal.Frames.Where(f => f.Depth == depth).Select(static f => f.Frame), Is.All.SameAs(before[depth - 1]), $"depth {depth}");
            }
        }
    }

    /// <summary>
    /// An <see cref="EvmException"/> thrown after CALL staged a child and before it was entered halts the parent
    /// but not the transaction: the orphan must be replaced when its depth is reached again in the same transaction.
    /// </summary>
    [Test]
    public void Frame_orphaned_by_an_evm_exception_is_replaced_within_the_same_transaction()
    {
        byte[] code = Prepare.EvmCode
            .MSTORE(0, ((UInt256)3).ToBigEndian())
            .CALL(CallGas * 5, Chain, 0, 0, 32, 0, 0).PushData(0x100).Op(Instruction.MSTORE)
            .CALL(CallGas * 5, Chain, 0, 0, 32, 0, 0).PushData(0x120).Op(Instruction.MSTORE)
            .CALL(CallGas * 5, Chain, 0, 0, 32, 0, 0).PushData(0x140).Op(Instruction.MSTORE)
            .RETURN(0x100, 96)
            .Done;

        RecordingTracer throwing = new(Machine) { ThrowEvmExceptionWhenStagedAtDepth = 2 };
        byte[]? output = RunAndRestore(code, throwing);
        VmState<EthereumGasPolicy> orphan = throwing.Orphan!;

        RecordingTracer uncachedTracer = new(Machine) { ThrowEvmExceptionWhenStagedAtDepth = 2 };
        byte[]? uncached = Uncached(() => RunAndRestore(code, uncachedTracer));

        Assert.That(orphan, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(throwing.Error, Is.Null);
            Assert.That(Words(output), Is.EqualTo(new UInt256[] { 0, 1, 1 }), "the first call halted, the others succeeded");
            Assert.That(output, Is.EqualTo(uncached));
            Assert.That(IsDisposed(orphan), Is.False, "the orphan is left alone");
            Assert.That(Machine.FrameCache[2], Is.Not.SameAs(orphan), "and replaced");
            Assert.That(throwing.Frames.Where(static f => f.Depth == 2).Select(static f => f.Frame), Has.None.SameAs(orphan));
        }

        RecordingTracer after = new(Machine);
        byte[]? afterOutput = RunAndRestore(code, after);
        byte[]? afterUncached = Uncached(() => RunAndRestore(code, traced: false));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterOutput, Is.EqualTo(afterUncached));
            Assert.That(after.Error, Is.Null);
        }
    }

    /// <summary>
    /// The EIP-8037 per-frame bookkeeping (create state charge, pre-existing target, the state-gas floor) belongs to
    /// the frame's own entry: creates on fresh and on pre-funded (alive, codeless) targets that succeed, revert or fail
    /// their code deposit, interleaved with slot toggles that charge and refund state gas, on the same reused frames.
    /// </summary>
    [Test]
    public void Create_and_state_gas_bookkeeping_does_not_stick_to_the_reused_frame([Values] bool traced)
    {
        byte[] ok = Prepare.EvmCode.RETURN(0, 1).Done;
        byte[] reverting = Prepare.EvmCode.REVERT(0, 0).Done;
        byte[] badCode = Prepare.EvmCode.PushData(0xEF).PushData(0).Op(Instruction.MSTORE8).RETURN(0, 1).Done;
        byte[] toggler = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.ISZERO).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        Address toggle = new("0x00000000000000000000000000000000000c1e20");
        Address toggleThenRevert = new("0x00000000000000000000000000000000000c1e21");
        Address callToggleThenRevert = new("0x00000000000000000000000000000000000c1e22");
        Address creator = new("0x00000000000000000000000000000000000c1e23");
        Deploy(toggle, toggler);
        Deploy(toggleThenRevert, [.. toggler[..^1], .. Prepare.EvmCode.REVERT(0, 0).Done]);
        Deploy(callToggleThenRevert, Prepare.EvmCode.CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP).REVERT(0, 0).Done);
        // A failed code deposit burns all the gas the create frame got, so the creates run in a gas-limited creator.
        Deploy(creator, CreatorCode());

        List<(Instruction Kind, byte[] Init, int Salt)> creates =
        [
            (Instruction.CREATE, ok, 0),
            (Instruction.CREATE2, reverting, 1), // pre-funded target: not charged, pre-existing
            (Instruction.CREATE, badCode, 0), // fresh target, failed deposit
            (Instruction.CREATE2, badCode, 2), // pre-funded target, failed deposit
            (Instruction.CREATE, reverting, 0),
            (Instruction.CREATE2, ok, 3), // pre-funded target, succeeds
            (Instruction.CREATE, ok, 0),
            (Instruction.CREATE2, reverting, 4), // fresh target
        ];
        foreach ((Instruction kind, byte[] init, int salt) in creates)
        {
            if (kind == Instruction.CREATE2 && salt != 4) TestState.CreateAccount(ContractAddress.From(creator, ((UInt256)salt).ToBigEndian(), init), (UInt256)1);
        }

        TestState.Commit(SpecProvider.GenesisSpec);

        Prepare code = Prepare.EvmCode.SSTORE(9, [1]); // the driver has used state gas before its first child
        int slot = 0x1000;
        void Record()
        {
            code.PushData(slot).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).Op(Instruction.PUSH0).PushData(slot + 32).Op(Instruction.RETURNDATACOPY)
                .Op(Instruction.GAS).PushData(slot + 64).Op(Instruction.MSTORE);
            slot += 96;
        }

        foreach ((Instruction kind, byte[] init, int salt) in creates)
        {
            code.MSTORE(0, ((UInt256)(kind == Instruction.CREATE2 ? 1 : 0)).ToBigEndian()).MSTORE(32, ((UInt256)salt).ToBigEndian())
                .StoreDataInMemory(64, init)
                .CALL(CallGas, creator, 0, 0, (UInt256)(64 + init.Length), 0, 0);
            Record();
            code.CALL(CallGas, toggle, 0, 0, 0, 0, 0);
            Record();
            code.CALL(CallGas, kind == Instruction.CREATE ? toggleThenRevert : callToggleThenRevert, 0, 0, 0, 0, 0);
            Record();
        }

        // What exists at every CREATE2 target after the transaction.
        foreach ((Instruction kind, byte[] init, int salt) in creates)
        {
            if (kind == Instruction.CREATE2)
            {
                code.PushData(ContractAddress.From(creator, ((UInt256)salt).ToBigEndian(), init)).Op(Instruction.EXTCODEHASH);
                Record();
            }
        }

        byte[] program = code.RETURN(0x1000, (UInt256)(slot - 0x1000)).Done;
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(program);
        string cached = Observe(transaction, context, traced);
        string warm = Observe(transaction, context, traced);
        string uncached = Uncached(() => Observe(transaction, context, traced));
        UInt256[] words = Words(RunAndRestore(program, traced: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cached, Does.StartWith("status=1"));
            Assert.That(cached, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
            // The sixth create (CREATE2, salt 3) landed on its pre-funded target.
            Assert.That(words[5 * 9 + 1], Is.EqualTo(AsWord(ContractAddress.From(creator, ((UInt256)3).ToBigEndian(), ok))));
        }
    }

    /// <summary>
    /// EIP-8037: a child that clears a slot a sibling created is refunded more state gas than it used itself, so the
    /// refund is advanced against its entry floor. The floor must be the reused frame's own entry, whether the child
    /// stops, reverts or halts, and whether it clears the slot itself (DELEGATECALL) or through a grandchild.
    /// </summary>
    [Test]
    public void State_gas_floor_of_a_reused_frame_is_its_own_entry([Values("stop", "revert", "invalid")] string ending, [Values] bool viaGrandchild)
    {
        byte[] toggler = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.ISZERO).PushData(0).Op(Instruction.SSTORE).Done;
        byte[] end = ending switch
        {
            "stop" => [(byte)Instruction.STOP],
            "revert" => Prepare.EvmCode.REVERT(0, 0).Done,
            _ => Prepare.EvmCode.INVALID().Done,
        };
        Address toggle = new("0x00000000000000000000000000000000000c1e30");
        Address clearer = new("0x00000000000000000000000000000000000c1e31");
        Deploy(toggle, [.. toggler, (byte)Instruction.STOP]);
        Deploy(clearer, viaGrandchild
            ? [.. Prepare.EvmCode.CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP).Done, .. end]
            : [.. toggler, .. end]);
        TestState.Commit(SpecProvider.GenesisSpec);

        Prepare code = Prepare.EvmCode.SSTORE(9, [1]);
        // The sibling creates the slot the clearer then clears: toggle's own slot through a grandchild, or the driver's.
        code = viaGrandchild ? code.CALL(CallGas, toggle, 0, 0, 0, 0, 0) : code.DELEGATECODE(CallGas, toggle, 0, 0, 0, 0);
        code.PushData(0x100).Op(Instruction.MSTORE).Op(Instruction.GAS).PushData(0x120).Op(Instruction.MSTORE);
        code = viaGrandchild ? code.CALL(CallGas, clearer, 0, 0, 0, 0, 0) : code.DELEGATECODE(CallGas, clearer, 0, 0, 0, 0);
        code.PushData(0x140).Op(Instruction.MSTORE).Op(Instruction.GAS).PushData(0x160).Op(Instruction.MSTORE)
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).PushData(0x180).Op(Instruction.MSTORE).Op(Instruction.GAS).PushData(0x1a0).Op(Instruction.MSTORE)
            .RETURN(0x100, 0xc0);

        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code.Done);
        string cached = Observe(transaction, context, traced: false);
        string warm = Observe(transaction, context, traced: false);
        string uncached = Uncached(() => Observe(transaction, context, traced: false));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cached, Does.StartWith("status=1"));
            Assert.That(cached, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
        }
    }

    /// <summary>
    /// A frame holding an advanced state-gas refund when a cancellation unwinds it must not hand that refund to the
    /// next transaction's child reusing the frame.
    /// </summary>
    [Test]
    public void Advanced_refund_of_a_frame_unwound_by_a_cancellation_does_not_reach_its_next_user()
    {
        Address toggle = new("0x00000000000000000000000000000000000c1e32");
        Deploy(toggle, Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.ISZERO).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        TestState.Commit(SpecProvider.GenesisSpec);

        // The second toggle clears the slot the first created and is cancelled on its STOP, refund still advanced.
        byte[] cancelled = Prepare.EvmCode.SSTORE(9, [1])
            .CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .CALL(CallGas, toggle, 0, 0, 0, 0, 0).Op(Instruction.POP)
            .Op(Instruction.STOP).Done;
        byte[] next = Prepare.EvmCode.SSTORE(9, [1])
            .CALL(CallGas, Stopper, 0, 0, 0, 0, 0).PushData(0).Op(Instruction.MSTORE)
            .Op(Instruction.GAS).PushData(32).Op(Instruction.MSTORE)
            .RETURN(0, 64).Done;

        RecordingTracer cancelling = new(Machine) { ThrowAtOpcodeDepth = 1, ThrowAtOpcode = Instruction.STOP, ThrowAtOpcodeOccurrence = 2 };
        Assert.Throws<OperationCanceledException>(() => RunAndRestore(cancelled, cancelling));
        Assert.That(Machine.FrameCache[1]!.StateGasRefundAdvanced, Is.Zero, "the unwound frame keeps no advanced refund");
        // A throwing call is not restored: drop what the cancelled transaction left in the world state.
        TestState.Reset();

        (Transaction nextTransaction, BlockExecutionContext nextContext) = PrepareOnce(next);
        string expected = Uncached(() => Observe(nextTransaction, nextContext, traced: false));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Observe(nextTransaction, nextContext, traced: false), Is.EqualTo(expected));
            Assert.That(IsDisposed(Machine.FrameCache[1]!), Is.True);
        }
    }

    /// <summary>
    /// Disposing the VM hands the data stacks of its released cached frames to the shared tier of the stack pool, where
    /// another thread rents them, and only once. A frame that is not released - here one orphaned by an
    /// <see cref="EvmException"/>, left in its slot - keeps its stack. The disposed VM keeps no slot and still runs.
    /// </summary>
    [Test, NonParallelizable]
    public void Disposal_returns_the_stacks_of_released_frames_to_the_shared_pool_once()
    {
        byte[] code = Prepare.EvmCode
            .MSTORE(0, ((UInt256)2).ToBigEndian())
            .CALL(CallGas * 5, Chain, 0, 0, 32, 0, 0).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        byte[]? expected = RunAndRestore(code, traced: false);
        RecordingTracer orphaning = new(Machine) { ThrowEvmExceptionWhenStagedAtDepth = 3 };
        RunAndRestore(code, orphaning);
        VmState<EthereumGasPolicy>?[] frames = Machine.FrameCache;
        Assert.That(orphaning.Orphan, Is.SameAs(frames[3]), "the reused depth-3 frame was orphaned and kept its slot");
        byte[]?[] stacks = [frames[1]?.DataStack, frames[2]?.DataStack, frames[3]?.DataStack];
        Assert.That(stacks, Has.None.Null, "every cached frame holds a data stack");

        DrainSharedStacks(SharedStackCount());
        EvmObjectPoolTests.RunOnNewThread(Machine.Dispose);
        int returned = SharedStackCount();
        EvmObjectPoolTests.RunOnNewThread(Machine.Dispose);
        int returnedAgain = SharedStackCount();
        byte[][] rented = DrainSharedStacks(returned);
        byte[]? output = RunAndRestore(code, traced: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returned, Is.EqualTo(2), "the two released frames handed their stacks to the shared tier");
            Assert.That(returnedAgain, Is.EqualTo(returned), "a second disposal returns nothing");
            Assert.That(rented.Count(s => ReferenceEquals(s, stacks[0])), Is.EqualTo(1), "depth 1's stack, once");
            Assert.That(rented.Count(s => ReferenceEquals(s, stacks[1])), Is.EqualTo(1), "depth 2's stack, once");
            Assert.That(new[] { frames[1]!.DataStack, frames[2]!.DataStack }, Is.All.Null, "the released frames gave their stacks up");
            Assert.That(frames[3]!.DataStack, Is.SameAs(stacks[2]), "the orphan keeps its stack");
            Assert.That(output, Is.EqualTo(expected), "a disposed VM still runs, from the pools");
            Assert.That(Machine.FrameCache, Is.Empty, "the frame slots are dropped and not refilled");
            Assert.That(Machine.EnvironmentCache, Is.Empty, "and the environment slots");
        }
    }

    /// <summary>
    /// A disposal that finds a transaction running hands no stack back, as the transaction may still enter a cached
    /// frame it read before the swap. The transaction finishes on the pools and the frames keep their stacks.
    /// </summary>
    [Test]
    public void Disposal_during_a_transaction_returns_no_stack()
    {
        byte[] code = ChainDriver(5);
        byte[]? expected = RunAndRestore(code, traced: false);
        VmState<EthereumGasPolicy>[] frames = Enumerable.Range(1, 6).Select(d => Machine.FrameCache[d]!).ToArray();
        Assert.That(frames, Has.None.Null, "the first run cached a frame at every depth");

        RecordingTracer disposing = new(Machine) { DisposeMachineAtActionDepth = 3 };
        byte[]? output = RunAndRestore(code, disposing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disposing.Error, Is.Null);
            Assert.That(output, Is.EqualTo(expected));
            Assert.That(frames.Select(static f => f.DataStack), Has.None.Null, "every cached frame keeps its stack");
            Assert.That(disposing.Frames.Where(static f => f.Depth > 3).Select(static f => f.Frame).Intersect(frames), Is.Empty,
                "the frames entered after the disposal come from the pools");
            Assert.That(Machine.FrameCache, Is.Empty, "the slots are dropped all the same");
        }
    }

    [Test]
    public void Ending_the_scope_that_resolved_the_machine_disposes_it()
    {
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        VirtualMachine<EthereumGasPolicy> machine;
        using (ILifetimeScope scope = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState)))
        {
            machine = (VirtualMachine<EthereumGasPolicy>)scope.Resolve<IVirtualMachine>();
            Assert.That(machine.FrameCache, Is.Not.Empty);
        }

        Assert.That(machine.FrameCache, Is.Empty, "the scope disposed the machine");
    }

    private static IEnumerable<TestCaseData> Seeds()
    {
        for (int seed = 1; seed <= 48; seed++)
        {
            yield return new TestCaseData(seed, seed % 6 == 0).SetArgDisplayNames(seed.ToString(), seed % 6 == 0 ? "traced" : "untraced");
        }
    }

    /// <summary>
    /// Random call trees - every call kind, precompiles, CREATE/CREATE2, recursion past the cached depth, low gas
    /// that runs out at random depths, and orphaning exceptions at random staged frames - observed exactly as on
    /// the uncached path, with the cache warmed by the previous seeds.
    /// </summary>
    [TestCaseSource(nameof(Seeds))]
    public void Random_call_trees_match_the_uncached_path(int seed, bool traced)
    {
        Random random = new(seed);
        byte[] code = RandomProgram(random);
        int orphanAt = random.Next(4) == 0 ? random.Next(1, 12) : -1;

        // Warm the cache with an unrelated program first so a leak from it would show.
        Observe(RandomProgram(new Random(seed + 1000)), traced: false);

        // Prepared once: preparing credits the driver, whose balance the probes read under CALLCODE and DELEGATECALL.
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code);
        string cached = Observe(transaction, context, traced, orphanAt, memory: false);
        string warm = Observe(transaction, context, traced, orphanAt, memory: false);
        string uncached = Uncached(() => Observe(transaction, context, traced, orphanAt, memory: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cached, Is.EqualTo(uncached));
            Assert.That(warm, Is.EqualTo(uncached));
        }
    }

    private static byte[] RandomProgram(Random random)
    {
        DriverBuilder driver = new();
        int steps = random.Next(6, 18);
        Address[] targets = [Probe, Recursive, Recursive, Chain, Eoa, Missing, Writer, Hopper, Clearer,
            new("0x0000000000000000000000000000000000000001"), new("0x0000000000000000000000000000000000000002"),
            new("0x0000000000000000000000000000000000000004"), DirtyRevert, DirtyInvalid, DirtyOutOfGas, DirtyReturn, DirtyStop];
        ulong[] gases = [3_000, 30_000, 200_000, 1_000_000, 3_000_000];
        for (int step = 0; step < steps; step++)
        {
            int roll = random.Next(10);
            if (roll == 0)
            {
                byte[] init = random.Next(2) == 0
                    ? Prepare.EvmCode.CALL(CallGas, Probe, 0, 0, 0, 0, 0).Op(Instruction.POP).CALL(CallGas, DirtyRevert, 0, 0, 0, 0, 0).Op(Instruction.POP).RETURN(0, 1).Done
                    : DirtyPrefix().CALL(CallGas, Recursive, 0, 0, 0, 0, 0).Op(Instruction.POP).REVERT(0x200, 32).Done;
                bool create2 = random.Next(2) == 0;
                driver.Create(create2 ? Instruction.CREATE2 : Instruction.CREATE, init, value: random.Next(2));
                continue;
            }

            Prepare code = driver.Code;
            int kind = random.Next(4);
            Address target = targets[random.Next(targets.Length)];
            // Recursive and Chain call ADDRESS, which under CALLCODE or DELEGATECALL would re-enter this driver.
            if (kind is 1 or 2 && (target == Recursive || target == Chain)) kind = 0;
            int inLength = ProbeInputLength;
            if (target == Recursive || target == Chain)
            {
                code.MSTORE(0, ((UInt256)random.Next(0, 14)).ToBigEndian()).MSTORE(32, ((UInt256)random.Next(0, 6)).ToBigEndian());
                inLength = 64;
            }

            UInt256 gas = gases[random.Next(gases.Length)];
            (UInt256 outOffset, UInt256 outLength) = random.Next(3) == 0 ? ((UInt256)0x40, (UInt256)64) : (UInt256.Zero, UInt256.Zero);
            UInt256 value = (UInt256)random.Next(3);
            UInt256 inLen = (UInt256)inLength;
            _ = kind switch
            {
                0 => code.CALL(gas, target, value, 0, inLen, outOffset, outLength),
                1 => code.CALLCODE(gas, target, value, 0, inLen, outOffset, outLength),
                2 => code.DELEGATECODE(gas, target, 0, inLen, outOffset, outLength),
                _ => code.STATICCALL(gas, target, 0, inLen, outOffset, outLength),
            };
            if (random.Next(3) == 0) code.MSTORE(0, DirtyWord);
            driver.LogResult();
        }

        return driver.Done();
    }

    private static byte[] ChainDriver(int n) => Prepare.EvmCode
        .MSTORE(0, ((UInt256)n).ToBigEndian())
        .CALL(CallGas * 10, Chain, 0, 0, 32, 0, 32).Op(Instruction.POP)
        .RETURN(0, 32)
        .Done;

    private (Transaction Transaction, BlockExecutionContext Context) PrepareOnce(byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, TxGas, code, value: 0);
        return (transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)));
    }

    private string Observe(byte[] code, bool traced, int orphanAt = -1, bool memory = true)
    {
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code);
        return Observe(transaction, context, traced, orphanAt, memory);
    }

    /// <summary>
    /// Runs the transaction without committing and renders everything a consumer could observe of it: the full trace
    /// when <paramref name="traced"/>, otherwise status, gas, output and logs - with actions traced or not, and with
    /// the <paramref name="orphanAt"/>-th staged child orphaned by an <see cref="EvmException"/>.
    /// </summary>
    private string Observe(Transaction transaction, in BlockExecutionContext context, bool traced, int orphanAt = -1, bool memory = true, bool actions = true)
    {
        StringBuilder text = new();
        if (traced && orphanAt < 0)
        {
            GethLikeTxMemoryTracer tracer = new(transaction, GethTraceOptions.Default with { EnableMemory = memory });
            _processor.CallAndRestore(transaction, context, tracer);
            GethLikeTxTrace trace = tracer.BuildResult();
            text.Append($"status={(trace.Failed ? 0 : 1)} gas={trace.Gas} out={Convert.ToHexString(trace.ReturnValue)}\n");
            foreach (GethTxTraceEntry entry in trace.Entries)
            {
                text.Append($"{entry.Depth} {entry.ProgramCounter} {entry.Opcode} {entry.Gas} {entry.GasCost} {entry.Error} ");
                text.Append(entry.Stack is { } stack ? Convert.ToHexString(stack.Span) : "-").Append(' ');
                text.Append(entry.Memory is { } mem ? Convert.ToHexString(mem.Span) : "-").Append('\n');
            }

            return text.ToString();
        }

        RecordingTracer observer = new(Machine) { ThrowEvmExceptionAtStagedCount = orphanAt, Traced = traced, TraceActions = actions };
        _processor.CallAndRestore(transaction, context, observer);
        text.Append($"status={observer.StatusCode} gas={observer.GasSpent} error={observer.Error} out={Convert.ToHexString(observer.ReturnValue ?? [])}\n");
        foreach (LogEntry log in observer.Logs)
        {
            text.Append($"log {log.Address} {string.Join(",", log.Topics.Select(static t => t.ToString()))} {Convert.ToHexString(log.Data)}\n");
        }

        return text.ToString();
    }

    /// <summary>Executes and commits the transaction.</summary>
    private RecordingTracer Run(byte[] code, bool traced) => Run(code, new RecordingTracer(Machine) { Traced = traced });

    private RecordingTracer Run(byte[] code, RecordingTracer tracer)
    {
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code);
        _processor.Execute(transaction, context, tracer);
        return tracer;
    }

    /// <summary>Runs the transaction without committing and returns its output.</summary>
    private byte[]? RunAndRestore(byte[] code, bool traced) => RunAndRestore(code, new RecordingTracer(Machine) { Traced = traced });

    private byte[]? RunAndRestore(byte[] code, RecordingTracer tracer)
    {
        (Transaction transaction, BlockExecutionContext context) = PrepareOnce(code);
        _processor.CallAndRestore(transaction, context, tracer);
        return tracer.ReturnValue;
    }

    /// <summary>Runs <paramref name="run"/> with the frame and environment caches swapped for empty ones.</summary>
    private T Uncached<T>(Func<T> run)
    {
        VmState<EthereumGasPolicy>?[] cachedFrames = Machine.FrameCache;
        ExecutionEnvironment?[] cachedEnvs = Machine.EnvironmentCache;
        try
        {
            Machine.FrameCache = [];
            Machine.EnvironmentCache = [];
            return run();
        }
        finally
        {
            Machine.FrameCache = cachedFrames;
            Machine.EnvironmentCache = cachedEnvs;
        }
    }

    /// <summary>The number of data stacks in the shared tier of <see cref="StackPool"/>.</summary>
    private static int SharedStackCount()
    {
        object pool = typeof(StackPool).GetField("_stackPool", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        return (int)pool.GetType().GetField("_sharedCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
    }

    /// <summary>Rents <paramref name="count"/> data stacks on a new thread, whose own tier is empty, so all from the shared tier.</summary>
    private static byte[][] DrainSharedStacks(int count)
    {
        byte[][] stacks = new byte[count][];
        EvmObjectPoolTests.RunOnNewThread(() =>
        {
            for (int i = 0; i < count; i++) stacks[i] = StackPool.RentStacks();
        });
        return stacks;
    }

    private void Deploy(Address address, byte[] code)
    {
        TestState.CreateAccount(address, 1.Ether);
        TestState.InsertCode(address, code, SpecProvider.GenesisSpec);
    }

    private static UInt256 AsWord(Address address) => new(address.Bytes.PadLeft(32), isBigEndian: true);

    private static UInt256[] Words(byte[]? data)
    {
        Assert.That(data, Is.Not.Null);
        // Inside a multiple-assert scope a failure does not stop the test.
        if (data is null) return [];
        UInt256[] words = new UInt256[data.Length / 32];
        for (int i = 0; i < words.Length; i++) words[i] = new UInt256(data.AsSpan(i * 32, 32), isBigEndian: true);
        return words;
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
            .Op(Instruction.SELFBALANCE)
            .PushData(0).Op(Instruction.CALLDATALOAD)
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
    private static Prepare DirtyPrefix() => Prepare.EvmCode
        .CALL(CallGas / 4, Probe, 0, 0, 0, 0x300, 32).Op(Instruction.POP)
        .MSTORE(0x200, DirtyWord)
        .SSTORE(7, [0x55])
        .TSTORE(7, [0x66])
        .LOGx(0, 0x200, 32)
        .PushData(1).PushData(2).PushData(3).PushData(4).PushData(5);

    /// <summary>
    /// Input (n, m): writes storage, transient storage, memory and a log, calls itself with (n - 1, m), returns
    /// r = child + 1 + 1000 * childSucceeded, reverting with it when n % m == 0; returns 0 when n is 0.
    /// </summary>
    private static byte[] RecursiveCode()
    {
        Asm a = new();
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD, Instruction.DUP1, Instruction.ISZERO); int toZero = a.Jump(Instruction.JUMPI);
        a.Push1(1); a.Op(Instruction.DUP2, Instruction.SUB, Instruction.PUSH0, Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD); a.Push1(32); a.Op(Instruction.MSTORE);
        a.Op(Instruction.DUP1, Instruction.DUP1, Instruction.SSTORE);
        a.Op(Instruction.DUP1, Instruction.DUP1, Instruction.TSTORE);
        a.Op(Instruction.DUP1); a.Push2(0x300); a.Op(Instruction.MSTORE); a.Push1(32); a.Push2(0x300); a.Op(Instruction.LOG0);
        a.Push1(32); a.Push1(0x40); a.Push1(64); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.ADDRESS, Instruction.GAS, Instruction.CALL);
        a.Push2(1000); a.Op(Instruction.MUL); a.Push1(0x40); a.Op(Instruction.MLOAD, Instruction.ADD); a.Push1(1); a.Op(Instruction.ADD);
        a.Push1(0x80); a.Op(Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD, Instruction.SWAP1, Instruction.MOD); int toReturn = a.Jump(Instruction.JUMPI);
        a.Push1(32); a.Push1(0x80); a.Op(Instruction.REVERT);
        a.Label(toReturn); a.Push1(32); a.Push1(0x80); a.Op(Instruction.RETURN);
        a.Label(toZero); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.MSTORE); a.Push1(32); a.Op(Instruction.PUSH0, Instruction.RETURN);
        return a.Done();
    }

    /// <summary>What <see cref="Recursive"/> returns for input (n, m), and whether it succeeds.</summary>
    private static (UInt256 Value, bool Success) RecursiveResult(int n, int m)
    {
        if (n == 0) return (0, true);
        (UInt256 child, bool childSucceeded) = RecursiveResult(n - 1, m);
        return (child + 1 + (childSucceeded ? 1000u : 0u), n % m != 0);
    }

    /// <summary>Calls itself with n - 1 while n > 0, then stops.</summary>
    private static byte[] ChainCode()
    {
        Asm a = new();
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD, Instruction.DUP1, Instruction.ISZERO); int toEnd = a.Jump(Instruction.JUMPI);
        a.Push1(1); a.Op(Instruction.SWAP1, Instruction.SUB, Instruction.PUSH0, Instruction.MSTORE);
        a.Op(Instruction.PUSH0, Instruction.PUSH0); a.Push1(32); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.ADDRESS, Instruction.GAS, Instruction.CALL);
        a.Push1(0x20); a.Op(Instruction.MSTORE); a.Push1(32); a.Push1(0x20); a.Op(Instruction.RETURN);
        a.Label(toEnd); a.Op(Instruction.STOP);
        return a.Done();
    }

    /// <summary>Warms slot word0 and account word1 then reverts when word2 is set; with no input measures warmth.</summary>
    private static byte[] WarmerCode()
    {
        Asm a = new();
        a.Op(Instruction.CALLDATASIZE, Instruction.ISZERO); int toMeasure = a.Jump(Instruction.JUMPI);
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD, Instruction.SLOAD, Instruction.POP);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD, Instruction.BALANCE, Instruction.POP);
        a.Push1(64); a.Op(Instruction.CALLDATALOAD); int toRevert = a.Jump(Instruction.JUMPI);
        a.Op(Instruction.STOP);
        a.Label(toRevert); a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.REVERT);
        a.Label(toMeasure);
        a.Measure(static a => { a.Push1(9); a.Op(Instruction.SLOAD); }, 0);
        a.Measure(static a => a.Push(ColdX), 32, Instruction.BALANCE);
        a.Measure(static a => { a.Push1(10); a.Op(Instruction.SLOAD); }, 64);
        a.Measure(static a => a.Push(ColdY), 96, Instruction.BALANCE);
        a.Push1(128); a.Op(Instruction.PUSH0, Instruction.RETURN);
        return a.Done();
    }

    /// <summary>Input (kind, salt, init code): CREATE (kind 0) or CREATE2 of the init code; returns the result.</summary>
    private static byte[] CreatorCode()
    {
        Asm a = new();
        a.Push1(64); a.Op(Instruction.CALLDATASIZE, Instruction.SUB); // [len]
        a.Op(Instruction.DUP1); a.Push1(64); a.Op(Instruction.PUSH0, Instruction.CALLDATACOPY); // mem[0..len) = init
        a.Op(Instruction.PUSH0, Instruction.CALLDATALOAD); int toCreate2 = a.Jump(Instruction.JUMPI);
        a.Op(Instruction.PUSH0, Instruction.PUSH0, Instruction.CREATE, Instruction.PUSH0, Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.PUSH0, Instruction.RETURN);
        a.Label(toCreate2);
        a.Push1(32); a.Op(Instruction.CALLDATALOAD, Instruction.SWAP1, Instruction.PUSH0, Instruction.PUSH0, Instruction.CREATE2, Instruction.PUSH0, Instruction.MSTORE);
        a.Push1(32); a.Op(Instruction.PUSH0, Instruction.RETURN);
        return a.Done();
    }

    /// <summary>
    /// A driver program of calls and creates that each log their success flag, return data size and return data to
    /// memory, returned as a whole.
    /// </summary>
    private sealed class DriverBuilder
    {
        private const int LogStart = 0x1000;
        private const int EntrySize = 64 + ProbeOutputLength;
        private int _entries;

        /// <summary>The program so far; a call emitted on it directly is logged with <see cref="LogResult"/>.</summary>
        public Prepare Code { get; } = Prepare.EvmCode.MSTORE(0, DirtyWord).MSTORE(32, DirtyWord);

        /// <summary>
        /// A call with the probe's input, or for <see cref="Recursive"/> with input (<paramref name="levels"/>,
        /// <see cref="RevertEvery"/>).
        /// </summary>
        public void Call(Instruction kind, Address target, int value = 0, int? levels = null, ulong gas = CallGas)
        {
            if (levels is { } n) Code.MSTORE(0, ((UInt256)n).ToBigEndian()).MSTORE(32, ((UInt256)RevertEvery).ToBigEndian());
            UInt256 inputLength = levels is null ? (UInt256)ProbeInputLength : 64;
            UInt256 callValue = (UInt256)value;
            _ = kind switch
            {
                Instruction.CALL => Code.CALL(gas, target, callValue, 0, inputLength, 0, 0),
                Instruction.CALLCODE => Code.CALLCODE(gas, target, callValue, 0, inputLength, 0, 0),
                Instruction.DELEGATECALL => Code.DELEGATECODE(gas, target, 0, inputLength, 0, 0),
                Instruction.STATICCALL => Code.STATICCALL(gas, target, 0, inputLength, 0, 0),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            LogResult();
            if (levels is not null) Code.MSTORE(0, DirtyWord).MSTORE(32, DirtyWord);
        }

        /// <summary>A create of <paramref name="initCode"/>; a CREATE2 is salted with its entry index.</summary>
        public void Create(Instruction kind, byte[] initCode, int value = 0)
        {
            Code.StoreDataInMemory(0x800, initCode);
            if (kind == Instruction.CREATE2) Code.PushData(_entries);
            Code.PushData(initCode.Length).PushData(0x800).PushData(value).Op(kind);
            LogResult();
        }

        public void LogResult()
        {
            int entry = LogStart + _entries++ * EntrySize;
            Code.PushData(entry).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).PushData(entry + 32).Op(Instruction.MSTORE)
                .Op(Instruction.RETURNDATASIZE).Op(Instruction.PUSH0).PushData(entry + 64).Op(Instruction.RETURNDATACOPY);
        }

        public byte[] Done() => Code.RETURN(LogStart, (UInt256)(_entries * EntrySize)).Done;
    }

    private sealed class Asm
    {
        private readonly List<byte> _code = [];

        public void Op(params Instruction[] ops) => _code.AddRange(ops.Select(static o => (byte)o));
        public void Push1(byte value) { Op(Instruction.PUSH1); _code.Add(value); }
        public void Push2(int value) { Op(Instruction.PUSH2); _code.Add((byte)(value >> 8)); _code.Add((byte)value); }
        public void Push(Address address) { Op(Instruction.PUSH20); _code.AddRange(address.Bytes); }

        /// <summary>Emits a PUSH2 placeholder and <paramref name="jump"/>; returns the placeholder's position.</summary>
        public int Jump(Instruction jump)
        {
            int at = _code.Count;
            Push2(0);
            Op(jump);
            return at;
        }

        public void Label(int placeholder)
        {
            int destination = _code.Count;
            Op(Instruction.JUMPDEST);
            _code[placeholder + 1] = (byte)(destination >> 8);
            _code[placeholder + 2] = (byte)destination;
        }

        /// <summary>Stores at <paramref name="offset"/> the gas of: body, optional op, POP, GAS.</summary>
        public void Measure(Action<Asm> body, byte offset, Instruction? then = null)
        {
            Op(Instruction.GAS);
            body(this);
            if (then is { } op) Op(op);
            Op(Instruction.POP, Instruction.GAS, Instruction.SWAP1, Instruction.SUB);
            Push1(offset);
            Op(Instruction.MSTORE);
        }

        public byte[] Done() => [.. _code];
    }

    /// <summary>
    /// Records the frames entered (with their depth on entry) and the result. On request it throws: a cancellation
    /// when a frame at a given depth is entered, at an opcode, or once a child is staged; or an
    /// <see cref="EvmException"/> once a child is staged, which orphans that child. It can also dispose the machine
    /// when a frame at a given depth is entered.
    /// </summary>
    private sealed class RecordingTracer(EthereumVirtualMachine machine) : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public override bool IsTracingActions => TraceActions;
        public override bool IsTracingInstructions => Traced || ThrowAtOpcodeDepth >= 0;

        public bool Traced { get; init; }
        public bool TraceActions { get; init; } = true;
        public int ThrowAtActionDepth { get; init; } = -1;
        public int ThrowAtOpcodeDepth { get; init; } = -1;
        public Instruction ThrowAtOpcode { get; init; } = Instruction.CALL;
        public int ThrowAtOpcodeOccurrence { get; init; } = 1;
        private int _opcodeHits;
        public int ThrowEvmExceptionWhenStagedAtDepth { get; set; } = -1;
        public int ThrowEvmExceptionAtStagedCount { get; init; } = -1;
        public int CancelWhenStagedAtDepth { get; init; } = -1;
        public int DisposeMachineAtActionDepth { get; init; } = -1;
        private int _staged;

        public VmState<EthereumGasPolicy>? Orphan { get; private set; }
        public ExecutionEnvironment? OrphanEnv { get; private set; }
        /// <summary>The depth is taken on entry: a cached environment's <c>CallDepth</c> is reset when it is released.</summary>
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
            if (frame.Env.CallDepth == DisposeMachineAtActionDepth) machine.Dispose();
            if (frame.Env.CallDepth == ThrowAtActionDepth) throw new OperationCanceledException("cancelled in an entered frame");
        }

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            if (env.CallDepth == ThrowAtOpcodeDepth && opcode == ThrowAtOpcode && ++_opcodeHits == ThrowAtOpcodeOccurrence)
            {
                throw new OperationCanceledException("cancelled mid-frame");
            }
        }

        public override void ReportActionRemainingGas(ulong gas)
        {
            if (machine.ReturnData is not VmState<EthereumGasPolicy> staged || IsDisposed(staged) || !ReferenceEquals(staged, LastStaged())) return;

            _staged++;
            if (staged.Env.CallDepth == CancelWhenStagedAtDepth)
            {
                Orphan = staged;
                OrphanEnv = staged.Env;
                throw new OperationCanceledException("cancelled with a child frame staged");
            }

            if (staged.Env.CallDepth == ThrowEvmExceptionWhenStagedAtDepth || _staged == ThrowEvmExceptionAtStagedCount)
            {
                ThrowEvmExceptionWhenStagedAtDepth = -1;
                Orphan = staged;
                throw new OutOfGasException();
            }
        }

        // A staged frame is the machine's pending ReturnData that is not the frame being executed.
        private VmState<EthereumGasPolicy>? LastStaged() =>
            machine.ReturnData is VmState<EthereumGasPolicy> staged && !ReferenceEquals(staged, machine.VmState) ? staged : null;

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
