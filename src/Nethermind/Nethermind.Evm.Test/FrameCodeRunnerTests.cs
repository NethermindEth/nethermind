// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>The frame code runner contract: when frames are offered, and that a runner's hand-back leaves the interpreter's exact state.</summary>
[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class FrameCodeRunnerTests : VirtualMachineTestsBase
{
    private readonly SwitchingRunner _runner = new();

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;
    protected override FrameCodeRunner<EthereumGasPolicy> CreateFrameCodeRunner() => _runner;

    public override void Setup()
    {
        _runner.Inner = null;
        base.Setup();
    }

    /// <summary>Results and receipts without instruction tracing; optionally cancelable, counting the machine's polls.</summary>
    private sealed class UntracedTracer(bool cancelable = false, int cancelAtPoll = int.MaxValue) : TestAllTracerWithOutput, ITxTracer
    {
        public int PollCount { get; private set; }

        public override bool IsTracingInstructions => false;
        public override bool IsTracingStack => false;
        public override bool IsTracingMemory => false;
        public override bool IsTracingOpLevelStorage => false;

        bool ITxTracer.IsCancelable => cancelable;
        bool ITxTracer.IsCancelled => ++PollCount >= cancelAtPoll;
    }

    /// <summary>Traces instructions whatever the suite's untraced switch says.</summary>
    private sealed class InstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => true;
    }

    private readonly record struct Outcome(EvmExceptionType Exception, bool Revert, ulong GasLeft, string Output, int OpCodes, int Polls);

    /// <summary>Runs <paramref name="code"/> as a top-level frame on the fixture's machine and undoes its state changes.</summary>
    private Outcome Run(byte[] code, bool cancelable = false, ulong gas = 1_000_000, byte[]? input = null)
    {
        if (!TestState.AccountExists(Recipient)) TestState.CreateAccount(Recipient, UInt256.Zero);
        UntracedTracer tracer = new(cancelable);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(new CodeInfo(code), Recipient, Sender, Recipient, 0, UInt256.Zero, input ?? ReadOnlyMemory<byte>.Empty);
        using StackAccessTracker accessTracker = new();
        Snapshot snapshot = TestState.TakeSnapshot();
        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, environment, in accessTracker, in snapshot);
        Machine.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.WithNumber(BlockNumber).WithTimestamp(Timestamp).TestObject, Spec));
        Machine.SetTxExecutionContext(new TxExecutionContext(Sender, CodeInfoRepository, null, UInt256.Zero));
        TransactionSubstate result = Machine.ExecuteTransaction<OffFlag>(state, TestState, tracer);
        Outcome outcome = new(result.EvmExceptionType, result.ShouldRevert, state.Gas.Value, result.Output.ToArray().ToHexString(), Machine.OpCodeCount, tracer.PollCount);
        TestState.Restore(snapshot);
        return outcome;
    }

    private const string CalleeCode = "60006000f3"; // PUSH1 0 PUSH1 0 RETURN

    /// <summary>Five zero PUSHes, PUSH20 callee, PUSH3 50000, CALL, STOP: the frame resumes at the STOP, offset 36.</summary>
    private static byte[] CallerCode(Address callee) =>
        Bytes.FromHexString("6000600060006000600073" + callee.ToString(false, false) + "62" + "00c350" + "f1" + "00");

    private Address DeployCallee()
    {
        Address callee = TestItem.AddressC;
        TestState.CreateAccount(callee, UInt256.Zero);
        TestState.InsertCode(callee, Bytes.FromHexString(CalleeCode), SpecProvider.GenesisSpec);
        return callee;
    }

    [Test]
    public void Offers_each_untraced_frame_on_entry_and_on_resume_after_a_call()
    {
        CountingRunner counting = new();
        _runner.Inner = counting;
        byte[] code = CallerCode(DeployCallee());

        Outcome outcome = Run(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Exception, Is.EqualTo(EvmExceptionType.None), "precondition: the call succeeds");
            Assert.That(counting.Offers.Select(static o => o.ProgramCounter), Is.EqualTo(new nint[] { 0, 0, code.Length - 1 }),
                "the caller's entry, the callee's entry, and the caller's resume after CALL");
            Assert.That(counting.Offers.Select(static o => o.Flags), Is.All.EqualTo(FrameRunFlags.None), "a run without a cancelable tracer");
        }
    }

    [Test]
    public void Reports_the_opcodes_the_interpreter_ran_after_declining()
    {
        CountingRunner counting = new();
        _runner.Inner = counting;

        Outcome outcome = Run(CallerCode(DeployCallee()));

        Assert.That(counting.Interpreted, Is.EqualTo(outcome.OpCodes), "every opcode ran on the interpreter after an offer");
    }

    [Test]
    public void Offers_cancelable_runs_with_the_cancelable_flag()
    {
        CountingRunner counting = new();
        _runner.Inner = counting;

        Run(CallerCode(DeployCallee()), cancelable: true);

        Assert.That(counting.Offers.Select(static o => o.Flags), Is.All.EqualTo(FrameRunFlags.Cancelable));
    }

    [Test]
    public void Offers_nothing_under_instruction_tracing()
    {
        CountingRunner counting = new();
        _runner.Inner = counting;

        Execute(new InstructionTracer(), CallerCode(DeployCallee()));

        Assert.That(counting.Offers, Is.Empty, "an instruction tracer must see every opcode run on the interpreter");
    }

    /// <summary>Reads the frame through the public accessors on every offer, adds opcodes, and declines.</summary>
    private sealed class AccessorRunner(int addedOpCodes) : FrameCodeRunner<EthereumGasPolicy>
    {
        public readonly List<(nint ProgramCounter, nint Head, byte[] BottomWord, byte[] Input)> Seen = [];

        protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
            scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, FrameRunFlags flags)
        {
            byte[] bottom = stack.Head > 0
                ? MemoryMarshal.CreateReadOnlySpan(ref stack.StackBase, EvmStack.WordSize).ToArray()
                : [];
            byte[] input = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.AsRef(in stack.InputDataStart), (int)stack.InputDataSize).ToArray();
            Seen.Add((programCounter, stack.Head, bottom, input));
            vm.AddOpCodes(addedOpCodes);
            return Declined;
        }
    }

    [Test]
    public void A_runner_reads_the_stack_and_input_and_adds_opcodes_through_the_public_accessors()
    {
        byte[] code = CallerCode(DeployCallee());
        byte[] input = [1, 2, 3];
        Outcome interpreted = Run(code, input: input);
        AccessorRunner accessor = new(addedOpCodes: 5);
        _runner.Inner = accessor;

        Outcome outcome = Run(code, input: input);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameCodeRunner<EthereumGasPolicy>.ContractVersion, Is.EqualTo(1));
            Assert.That(accessor.Seen.Count, Is.EqualTo(3), "the caller's entry, the callee's entry, and the caller's resume");
            Assert.That(accessor.Seen[0].Input, Is.EqualTo(input), "the top-level frame's input is the call data");
            Assert.That(accessor.Seen[1].Input, Is.Empty, "the callee was called with no input");
            Assert.That(accessor.Seen[2].ProgramCounter, Is.EqualTo((nint)(code.Length - 1)));
            Assert.That(accessor.Seen[2].Head, Is.EqualTo((nint)1), "CALL leaves its success flag alone on the stack");
            Assert.That(accessor.Seen[2].BottomWord[0], Is.EqualTo(1), "the success flag is the bottom word, least significant byte first");
            Assert.That(outcome.OpCodes, Is.EqualTo(interpreted.OpCodes + 3 * 5), "each offer added its opcodes");
        }
    }

    private static readonly string[] Programs =
    [
        // Straight-line arithmetic and stack work, then MSTORE and RETURN of the word.
        "6002600301600402800390600a0a5f52602060" + "00f3",
        // A counting loop: 200 rounds of decrement and branch, then STOP.
        "60c8" + "5b" + "600190038060025700",
        // ADD on an empty stack: the interpreter's underflow.
        "01",
        // Ten DUPs past an empty stack after PUSHes: underflow mid-sequence.
        "600160026003" + "88",
        // KECCAK256 over memory, then the hash returned.
        "6020600020" + "5f526020" + "5ff3",
        // REVERT with data.
        "602a5f526020" + "5ffd",
    ];

    [Test]
    public void A_stepping_runner_hands_back_exactly([ValueSource(nameof(Programs))] string program, [Values(1, 3, 17)] int steps)
    {
        byte[] code = Bytes.FromHexString(program);
        Outcome interpreted = Run(code);
        CheckedSteppingRunner stepping = new(steps);
        _runner.Inner = stepping;

        Outcome stepped = Run(code);

        Assert.That(stepped, Is.EqualTo(interpreted), "a runner that ran opcodes and handed back must leave the interpreter's state");
    }

    [Test]
    public void A_stepping_runner_also_runs_called_frames_exactly([Values(1, 3, 17)] int steps)
    {
        byte[] code = CallerCode(DeployCallee());
        Outcome interpreted = Run(code);
        _runner.Inner = new CheckedSteppingRunner(steps);

        Assert.That(Run(code), Is.EqualTo(interpreted));
    }

    private static readonly string[] Loops =
    [
        "5b600056",         // JUMPDEST PUSH1 0 JUMP
        "5b6001600057",     // JUMPDEST PUSH1 1 PUSH1 0 JUMPI
        "5b61000056",       // JUMPDEST PUSH2 0 JUMP
    ];

    [Test]
    public void Polls_as_often_with_a_runner_as_without([ValueSource(nameof(Loops))] string loop, [Values(1, 3, 17)] int steps)
    {
        byte[] code = Bytes.FromHexString(loop);
        Outcome interpreted = Run(code, cancelable: true, gas: 200_000);
        _runner.Inner = new CheckedSteppingRunner(steps);

        Outcome stepped = Run(code, cancelable: true, gas: 200_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interpreted.Polls, Is.GreaterThan(1), "precondition: the loop passes the poll interval");
            Assert.That(stepped, Is.EqualTo(interpreted), "the same polls, gas and opcode count whoever ran the frame");
        }
    }
}
