// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>The frame code runner contract: when frames are offered, and that a runner's hand-back leaves the interpreter's exact state.</summary>
[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class FrameCodeRunnerTests : VirtualMachineTestsBase
{
    private readonly SwitchingRunner _runner = new();

    /// <summary>A machine constructed without a runner, for the baselines that must match master's behaviour.</summary>
    private EthereumVirtualMachine _bare = null!;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;
    protected override FrameCodeRunner<EthereumGasPolicy> CreateFrameCodeRunner() => _runner;

    public override void Setup()
    {
        _runner.Inner = null;
        base.Setup();
        _bare = new EthereumVirtualMachine(new TestBlockhashProvider(SpecProvider), SpecProvider, LimboLogs.Instance);
    }

    public override void TearDown()
    {
        _bare?.Dispose();
        base.TearDown();
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

    private readonly record struct Outcome(EvmExceptionType Exception, bool Revert, ulong GasLeft, string Output, int OpCodes, int Polls);

    /// <summary>Runs <paramref name="code"/> as a top-level frame on the fixture's machine and undoes its state changes.</summary>
    private Outcome Run(byte[] code, bool cancelable = false, ulong gas = 1_000_000, byte[]? input = null, bool bare = false) =>
        Run(code, new UntracedTracer(cancelable), gas, input, bare);

    /// <summary>Runs <paramref name="code"/> on a tracer that cancels at its <paramref name="cancelAtPoll"/>th poll, returning the polls taken.</summary>
    private int RunCancelled(byte[] code, int cancelAtPoll, bool bare = false)
    {
        UntracedTracer tracer = new(cancelable: true, cancelAtPoll);
        Assert.Throws<OperationCanceledException>(() => Run(code, tracer, 200_000, null, bare));
        return tracer.PollCount;
    }

    private Outcome Run(byte[] code, UntracedTracer tracer, ulong gas, byte[]? input, bool bare)
    {
        EthereumVirtualMachine machine = bare ? _bare : Machine;
        if (!TestState.AccountExists(Recipient)) TestState.CreateAccount(Recipient, UInt256.Zero);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(new CodeInfo(code), Recipient, Sender, Recipient, 0, UInt256.Zero, input ?? ReadOnlyMemory<byte>.Empty);
        using StackAccessTracker accessTracker = new();
        Snapshot snapshot = TestState.TakeSnapshot();
        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, environment, in accessTracker, in snapshot);
        machine.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.WithNumber(BlockNumber).WithTimestamp(Timestamp).TestObject, Spec));
        machine.SetTxExecutionContext(new TxExecutionContext(Sender, CodeInfoRepository, null, UInt256.Zero));
        try
        {
            TransactionSubstate result = machine.ExecuteTransaction<OffFlag>(state, TestState, tracer);
            return new(result.EvmExceptionType, result.ShouldRevert, state.Gas.Value, result.Output.ToArray().ToHexString(), machine.OpCodeCount, tracer.PollCount);
        }
        finally
        {
            TestState.Restore(snapshot);
        }
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

        Execute(new TestAllTracerWithOutput(), CallerCode(DeployCallee()));

        Assert.That(counting.Offers, Is.Empty, "an instruction tracer must see every opcode run on the interpreter");
    }

    /// <summary>Reads the frame through the public accessors on every offer, adds opcodes, and declines.</summary>
    private sealed class AccessorRunner(int addedOpCodes) : FrameCodeRunner<EthereumGasPolicy>
    {
        public readonly List<(nint ProgramCounter, nint Head, byte[] BottomWord, byte[] Input)> Seen = [];

        protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
            scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
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
        Outcome interpreted = Run(code, input: input, bare: true);
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
        Outcome interpreted = Run(code, bare: true);
        CheckedSteppingRunner stepping = new(steps);
        _runner.Inner = stepping;

        Outcome stepped = Run(code);

        Assert.That(stepped, Is.EqualTo(interpreted), "a runner that ran opcodes and handed back must leave the interpreter's state");
    }

    [Test]
    public void A_stepping_runner_also_runs_called_frames_exactly([Values(1, 3, 17)] int steps)
    {
        byte[] code = CallerCode(DeployCallee());
        Outcome interpreted = Run(code, bare: true);
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
        Outcome interpreted = Run(code, cancelable: true, gas: 200_000, bare: true);
        _runner.Inner = new CheckedSteppingRunner(steps);

        Outcome stepped = Run(code, cancelable: true, gas: 200_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interpreted.Polls, Is.GreaterThan(1), "precondition: the loop passes the poll interval");
            Assert.That(stepped, Is.EqualTo(interpreted), "the same polls, gas and opcode count whoever ran the frame");
        }
    }

    /// <summary>
    /// 1023 JUMPDESTs, PUSH2 0x0403, JUMP, JUMPDEST, STOP: the JUMP is taken as the 1025th opcode, past the poll interval,
    /// so the interpreter polls there as well as on entry.
    /// </summary>
    private static byte[] PollBoundaryCode() =>
        Bytes.FromHexString(string.Concat(Enumerable.Repeat("5b", 1023)) + "610403" + "56" + "5b" + "00");

    private static IEnumerable<TestCaseData> HandBackRunners()
    {
        yield return new TestCaseData(null).SetArgDisplayNames("no runner");
        yield return new TestCaseData((Func<FrameCodeRunner<EthereumGasPolicy>>)(static () => new DecliningRunner())).SetArgDisplayNames("declining");
        yield return new TestCaseData((Func<FrameCodeRunner<EthereumGasPolicy>>)(static () => new CountingRunner())).SetArgDisplayNames("declining, reporting opcodes");
        foreach (int steps in new[] { 1, 17, 1000, 1023, 1024, 2000 })
            yield return new TestCaseData((Func<FrameCodeRunner<EthereumGasPolicy>>)(() => new CheckedSteppingRunner(steps))).SetArgDisplayNames($"stepping {steps}");
        yield return new TestCaseData((Func<FrameCodeRunner<EthereumGasPolicy>>)(static () => new FinishingRunner())).SetArgDisplayNames("finishing");
    }

    [TestCaseSource(nameof(HandBackRunners))]
    public void A_hand_back_keeps_the_poll_interval_the_runner_spent(Func<FrameCodeRunner<EthereumGasPolicy>>? runner)
    {
        byte[] code = PollBoundaryCode();
        Outcome interpreted = Run(code, cancelable: true, bare: true);
        _runner.Inner = runner?.Invoke();

        Outcome run = Run(code, cancelable: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interpreted.Exception, Is.EqualTo(EvmExceptionType.None), "precondition: the frame runs to its STOP");
            Assert.That(interpreted.OpCodes, Is.EqualTo(1027), "precondition: the frame's opcodes, its STOP included");
            Assert.That(interpreted.Polls, Is.EqualTo(2), "precondition: the interpreter polls on entry and at the JUMP past the interval");
            Assert.That(run.Polls, Is.EqualTo(interpreted.Polls), "the same number of polls whoever ran the start of the frame");
            Assert.That(run, Is.EqualTo(interpreted), "and the same gas, output and opcode count");
        }
    }

    [TestCaseSource(nameof(HandBackRunners))]
    public void A_hand_back_cancels_at_the_poll_the_interpreter_cancels_at(Func<FrameCodeRunner<EthereumGasPolicy>>? runner)
    {
        byte[] code = PollBoundaryCode();
        int interpreted = RunCancelled(code, cancelAtPoll: 2, bare: true);
        _runner.Inner = runner?.Invoke();

        int run = RunCancelled(code, cancelAtPoll: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interpreted, Is.EqualTo(2), "precondition: the interpreter stops at the JUMP's poll");
            Assert.That(run, Is.EqualTo(interpreted), "a runner's opcodes before the hand-back cannot let the frame outrun the poll");
        }
    }

    [Test]
    public void A_runner_poll_restarts_the_interval_it_hands_back()
    {
        // The runner polls on the offer and reports none of the interval spent, so the loop polls 1024 opcodes later,
        // at the JUMP again: entry, the runner's own and the JUMP's.
        _runner.Inner = new PollingRunner();

        Outcome run = Run(PollBoundaryCode(), cancelable: true);

        Assert.That(run.Polls, Is.EqualTo(3));
    }

    [Test]
    public void Does_not_offer_a_frame_resumed_at_the_end_of_its_code([Values(false, true)] bool cancelable)
    {
        byte[] withStop = CallerCode(DeployCallee());
        byte[] code = withStop[..^1]; // CALL is the last byte: the caller resumes at the end of its code.
        Outcome interpreted = Run(code, cancelable, bare: true);
        CountingRunner counting = new();
        _runner.Inner = counting;

        Outcome outcome = Run(code, cancelable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counting.Offers.Select(static o => o.ProgramCounter), Is.EqualTo(new nint[] { 0, 0 }), "the caller's and the callee's entries only");
            Assert.That(outcome, Is.EqualTo(interpreted));
        }
    }

    [Test]
    public void Cancels_as_without_a_runner([ValueSource(nameof(Loops))] string loop, [Values(1, 2, 3)] int cancelAtPoll, [Values(0, 1, 3, 17)] int steps)
    {
        byte[] code = Bytes.FromHexString(loop);
        int interpreted = RunCancelled(code, cancelAtPoll, bare: true);
        if (steps > 0)
            _runner.Inner = new CheckedSteppingRunner(steps);

        int run = RunCancelled(code, cancelAtPoll);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interpreted, Is.EqualTo(cancelAtPoll), "precondition: the run stops at the poll that sees the cancellation");
            Assert.That(run, Is.EqualTo(interpreted), "the same poll stops the run whoever ran the frame");
        }
    }

    [Test]
    public void A_runner_poll_throws_the_loop_exception()
    {
        _runner.Inner = new PollingRunner();

        // Poll 1 is the machine's own before the offer; poll 2 is the runner's.
        int polls = RunCancelled(Bytes.FromHexString(Loops[0]), cancelAtPoll: 2);

        Assert.That(polls, Is.EqualTo(2));
    }

    private static readonly string[] Finishable =
    [
        // Arithmetic, then STOP.
        "600160020100",
        // RETURN of nothing.
        "60006000f3",
        // Arithmetic, then REVERT of nothing.
        "6001600201" + "60006000fd",
        // An undefined opcode after arithmetic: its bad-instruction body ends the frame with a fault, which the runner returns.
        "6001600201" + "0c",
    ];

    [Test]
    public void A_runner_that_finishes_frames_returns_the_interpreter_result([ValueSource(nameof(Finishable))] string program)
    {
        byte[] code = Bytes.FromHexString(program);
        Outcome interpreted = Run(code, bare: true);
        FinishingRunner finishing = new();
        _runner.Inner = finishing;

        Outcome finished = Run(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finishing.Finished, Is.EqualTo(1), "the runner returned the frame's result itself");
            Assert.That(finished, Is.EqualTo(interpreted));
        }
    }

    [Test]
    public void A_runner_finishes_called_and_resumed_frames_exactly()
    {
        byte[] code = CallerCode(DeployCallee());
        Outcome interpreted = Run(code, bare: true);
        FinishingRunner finishing = new();
        _runner.Inner = finishing;

        Outcome finished = Run(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(finishing.Finished, Is.EqualTo(2), "the callee's RETURN and the caller's STOP after resuming");
            Assert.That(finished, Is.EqualTo(interpreted));
        }
    }

    [Test]
    public void Keeps_runner_state_per_code_across_offers()
    {
        CodeStateRunner codeState = new();
        _runner.Inner = codeState;

        Run(CallerCode(DeployCallee()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeState.Seen.Count, Is.EqualTo(3), "the caller's entry, the callee's entry, and the caller's resume");
            Assert.That(codeState.Seen[0].Found, Is.Null, "a new code starts with an empty slot");
            Assert.That(codeState.Seen[2].Code, Is.SameAs(codeState.Seen[0].Code), "precondition: the resume runs the caller's code");
            Assert.That(codeState.Seen[2].Found, Is.SameAs(codeState.Seen[0].Kept), "the caller's resume finds what its entry kept");
            Assert.That(codeState.Seen[1].Kept, Is.Not.SameAs(codeState.Seen[0].Kept), "the callee's code has a slot of its own");
        }
    }
}
