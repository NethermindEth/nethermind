// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// A VM keeps its child call frames from one transaction to the next, with the access tracker and the inline memory
/// their last user left. Two transactions on one VM, under different tracers, must each see what fresh frames would.
/// <see cref="CallFrameCacheTests"/> covers what a reused frame observes within a transaction.
/// </summary>
public class CallFrameCacheTransactionTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

    private const ulong ChildGas = 20_000;
    /// <summary>
    /// A metered call to <see cref="Reverter"/> while its slot 9 and <see cref="ColdX"/> are cold: seven pushes (21),
    /// CALL to a warm account (100), the child (PUSH1, SLOAD 2100, POP, PUSH20, BALANCE 2600, POP, two PUSH1) and POP, GAS.
    /// </summary>
    private const ulong ColdReverterCall = 4841;
    private const ulong WarmReverterCall = ColdReverterCall - (2100 - 100) - (2600 - 100);
    private const int OutputLength = 160;

    private static readonly Address Committer = new("0x00000000000000000000000000000000000c2e01");
    private static readonly Address Reverter = new("0x00000000000000000000000000000000000c2e02");
    private static readonly Address Dirtier = new("0x00000000000000000000000000000000000c2e03");
    private static readonly Address Writer = new("0x00000000000000000000000000000000000c2e04");
    private static readonly Address ColdX = new("0x00000000000000000000000000000000000c2e0a");
    private static readonly Address ColdY = new("0x00000000000000000000000000000000000c2e0b");
    private static readonly Address Untouched = new("0x00000000000000000000000000000000000c2e0c");

    private static readonly byte[] DirtyWord = Bytes.FromHexString("0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
    private static readonly byte[] Word = Bytes.FromHexString("0x0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        Deploy(Committer, Prepare.EvmCode.SLOAD(10).Op(Instruction.POP).BALANCE(ColdY).Op(Instruction.POP).Op(Instruction.STOP).Done);
        Deploy(Reverter, Prepare.EvmCode.SLOAD(9).Op(Instruction.POP).BALANCE(ColdX).Op(Instruction.POP).REVERT(0, 0).Done);
        Deploy(Dirtier, Prepare.EvmCode
            .MSTORE(0, DirtyWord).MSTORE(32, DirtyWord).MSTORE(64, DirtyWord).MSTORE(96, DirtyWord).MSTORE(128, DirtyWord)
            .RETURN(0, OutputLength).Done);
        Deploy(Writer, Prepare.EvmCode
            .MSTORE8(0, [0x11]) // the first write to memory left dirty
            .MSTORE8(5, [0x22]) // leaves a gap inside word 0
            .MSTORE(64, Word) // a gap that starts inside word 0
            .MSTORE8(100, [0x44]) // word 3 is left partly written
            .MLOAD(96).PushData(128).Op(Instruction.MSTORE)
            .RETURN(0, OutputLength).Done);
        TestState.Commit(SpecProvider.GenesisSpec);
    }

    /// <summary>
    /// EIP-2929: a reverted call's accesses are rolled back, except under access tracing (<c>eth_createAccessList</c>
    /// with <c>optimize: false</c>), which keeps them for the list. The frame both transactions reuse must follow each
    /// one's tracing, and with <paramref name="sameTrackedState"/> off it must also take the tracked state of the second
    /// transaction.
    /// </summary>
    [Test]
    public void Reused_frame_follows_the_access_tracing_of_each_transaction([Values] bool accessListFirst, [Values] bool sameTrackedState)
    {
        Prepare program = Prepare.EvmCode
            .BALANCE(Reverter).Op(Instruction.POP) // both metered calls find their target warm
            .CALL(ChildGas, Committer, 0, 0, 0, 0, 0).Op(Instruction.POP); // warms its slot 10 and ColdY, commits
        // Slot 9 and ColdX are read only in the reverted calls, so nothing else can put them back on the list.
        Measure(program, 0, static p => p.CALL(ChildGas, Reverter, 0, 0, 0, 0, 0));
        Measure(program, 32, static p => p.CALL(ChildGas, Reverter, 0, 0, 0, 0, 0));
        Measure(program, 64, static p => p.BALANCE(ColdY));
        Measure(program, 96, static p => p.BALANCE(Untouched));
        byte[] code = program.RETURN(0, 128).Done;

        Run(accessListFirst);
        VmState<EthereumGasPolicy>? frame = Machine.FrameCache[1];
        object firstState = PooledTrackedState();
        // Deliberate lifetime control, the VM-level counterpart of VmStateTests: holding the state the first transaction
        // returned makes the pool hand the second another one, as for a VM that next runs on another thread.
        StackAccessTracker held = sameTrackedState ? default : new();
        object secondState;
        try
        {
            Run(!accessListFirst);
            secondState = PooledTrackedState();
        }
        finally
        {
            if (!sameTrackedState) held.Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondState, sameTrackedState ? Is.SameAs(firstState) : Is.Not.SameAs(firstState),
                "the tracked state the second transaction ran with");
            Assert.That(frame, Is.Not.Null);
            Assert.That(Machine.FrameCache[1], Is.SameAs(frame), "both transactions ran their calls on one cached frame");
        }

        void Run(bool accessList)
        {
            OutputTracer output = new();
            AccessTxTracer access = new();
            Execute<ITxTracer>(accessList ? new CompositeTxTracer(output, access) : output, code);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(output.StatusCode, Is.EqualTo(StatusCode.Success), output.Error);
                if (!accessList)
                {
                    // The second reverted call costs what the first did, and the committed child's account stays warm.
                    Assert.That(Words(output.ReturnValue), Is.EqualTo(new UInt256[] { ColdReverterCall, ColdReverterCall, 107, 2607 }));
                    return;
                }

                // Access tracing keeps what the reverted call read warm, so only the second reverted call finds it warm.
                Assert.That(Words(output.ReturnValue), Is.EqualTo(new UInt256[] { ColdReverterCall, WarmReverterCall, 107, 2607 }));
                Assert.That(access.AccessList!.Select(static e => $"{e.Address} [{string.Join(",", e.StorageKeys)}]"),
                    Is.SupersetOf(new[] { $"{Committer} [10]", $"{Reverter} [9]", $"{ColdX} []", $"{ColdY} []" }));
            }
        }
    }

    /// <summary>
    /// A frame's inline memory keeps the bytes its last user wrote, and is cleared lazily as accesses reach it.
    /// Without memory tracing nothing initializes memory between instructions, so byte stores leave words partly
    /// written; every byte the writer did not write must read as zero.
    /// </summary>
    [Test]
    public void Partly_written_words_of_a_reused_frame_read_as_zero([Values] bool traceInstructions, [Values] bool acrossTransactions)
    {
        // Each transaction returns the success flag of each call, then the output of the last.
        byte[][] transactions = acrossTransactions
            ? [CallEach(Dirtier), CallEach(Writer)]
            : [CallEach(Dirtier, Writer), CallEach(Dirtier, Writer)];
        byte[] dirty = [.. DirtyWord, .. DirtyWord, .. DirtyWord, .. DirtyWord, .. DirtyWord];
        byte[] written = new byte[OutputLength];
        written[0] = 0x11;
        written[5] = 0x22;
        Word.CopyTo(written, 64);
        written[100] = 0x44;
        written[128 + 4] = 0x44; // the word loaded from 96

        byte[] first = Run(transactions[0]);
        VmState<EthereumGasPolicy>? frame = Machine.FrameCache[1];
        byte[] second = Run(transactions[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(acrossTransactions ? Flags(1, dirty) : Flags(2, written)));
            Assert.That(second, Is.EqualTo(Flags(acrossTransactions ? 1 : 2, written)));
            Assert.That(frame, Is.Not.Null);
            Assert.That(Machine.FrameCache[1], Is.SameAs(frame), "the second transaction's calls ran on the frame the first left");
        }

        byte[] Run(byte[] code)
        {
            OutputTracer tracer = Execute(new OutputTracer(traceInstructions), code);
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), tracer.Error);
            return tracer.ReturnValue ?? [];
        }

        static byte[] Flags(int calls, byte[] output)
        {
            byte[] result = new byte[calls * 32 + output.Length];
            for (int i = 0; i < calls; i++) result[i * 32 + 31] = 1;
            output.CopyTo(result, calls * 32);
            return result;
        }
    }

    /// <summary>Stores at <paramref name="offset"/> the gas of <paramref name="body"/>, POP and GAS.</summary>
    private static void Measure(Prepare program, int offset, Func<Prepare, Prepare> body) =>
        body(program.Op(Instruction.GAS)).Op(Instruction.POP)
            .Op(Instruction.GAS).Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(offset).Op(Instruction.MSTORE);

    /// <summary>Calls each target with no input, every output into one window after the flags; returns the flags and the window.</summary>
    private static byte[] CallEach(params Address[] targets)
    {
        Prepare program = Prepare.EvmCode;
        ulong window = (ulong)targets.Length * 32;
        for (int i = 0; i < targets.Length; i++)
        {
            program.CALL(ChildGas, targets[i], 0, 0, 0, window, OutputLength).PushData(i * 32).Op(Instruction.MSTORE);
        }

        return program.RETURN(0, window + OutputLength).Done;
    }

    /// <summary>The tracked state this thread's pool hands out next: the one the last transaction returned.</summary>
    private static object PooledTrackedState()
    {
        using StackAccessTracker probe = new();
        return probe.AccessedAddresses;
    }

    private void Deploy(Address address, byte[] code)
    {
        TestState.CreateAccount(address, 1.Ether);
        TestState.InsertCode(address, code, SpecProvider.GenesisSpec);
    }

    private static UInt256[] Words(byte[]? data)
    {
        Assert.That(data, Is.Not.Null);
        // Inside a multiple-assert scope a failure does not stop the test.
        if (data is null) return [];
        UInt256[] words = new UInt256[data.Length / 32];
        for (int i = 0; i < words.Length; i++) words[i] = new UInt256(data.AsSpan(i * 32, 32), isBigEndian: true);
        return words;
    }

    /// <summary>Keeps the output, status and error; traces instructions on request, never memory.</summary>
    private sealed class OutputTracer(bool traceInstructions = false) : CallOutputTracer
    {
        public override bool IsTracingInstructions => traceInstructions;
    }
}
