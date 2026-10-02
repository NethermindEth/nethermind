// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Test.Tracing;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class VirtualMachineTests : VirtualMachineTestsBase
{
    private static readonly TestCaseData[] JumpCompletionCases =
    [
        new TestCaseData("600456005b00", 21012UL, 4).SetName("Jump_taken"),
        new TestCaseData("6001600657005b00", 21017UL, 5).SetName("JumpI_taken"),
        new TestCaseData("6000600657005b00", 21016UL, 4).SetName("JumpI_not_taken"),
        new TestCaseData("6003565b00", 21012UL, 4).SetName("Jump_to_next_instruction"),
        new TestCaseData("600456fe5b5b00", 21013UL, 5).SetName("Jump_to_consecutive_markers"),
        new TestCaseData("6003565b", 21012UL, 3).SetName("Jump_to_final_byte"),
        new TestCaseData("610004565b", 21012UL, 3).SetName("Push2_jump_to_final_byte"),
        new TestCaseData("60016005575b", 21017UL, 4).SetName("JumpI_taken_to_final_byte"),
        new TestCaseData("6000600057", 21016UL, 3).SetName("JumpI_not_taken_at_end"),
        // PUSH2 fuses with the following jump.
        new TestCaseData("61000556005b00", 21012UL, 4).SetName("Push2_Jump_taken"),
        new TestCaseData("60006100005700", 21016UL, 4).SetName("Push2_JumpI_not_taken_to_invalid_destination"),
    ];

    // Untraced dispatch runs off the end into the zero padding that follows the code.
    private static readonly TestCaseData[] EndOfCodeCases =
    [
        new TestCaseData("7f", 21003UL, 1).SetName("Push32_without_immediate"),
        new TestCaseData("7f01", 21003UL, 1).SetName("Push32_truncated"),
        new TestCaseData("61ff", 21003UL, 1).SetName("Push2_truncated"),
        new TestCaseData("6000600001", 21009UL, 3).SetName("Add_at_end"),
        new TestCaseData("600060000100", 21009UL, 4).SetName("Explicit_stop_at_end"),
        new TestCaseData("60003b", 21703UL, 2).SetName("ExtCodeSize_at_end"),
        new TestCaseData("60003b15", 21706UL, 3).SetName("ExtCodeSize_IsZero_at_end"),
    ];

    // Each case runs under both untraced drivers, which adjust a halt in the padding separately.
    private static IEnumerable<TestCaseData> UntracedCompletionCases()
    {
        foreach (TestCaseData data in JumpCompletionCases.Concat(EndOfCodeCases))
        {
            foreach (bool cancelable in (bool[])[false, true])
            {
                yield return new TestCaseData([.. data.Arguments, cancelable])
                    .SetName($"{data.TestName}{(cancelable ? "_cancelable" : string.Empty)}");
            }
        }
    }

    private static readonly TestCaseData[] JumpFailureCases =
    [
        new TestCaseData("56", 100000UL, 1).SetName("Jump_stack_underflow"),
        new TestCaseData("600056", 100000UL, 2).SetName("Jump_invalid_destination"),
        new TestCaseData("6003565b", 21010UL, 2).SetName("Jump_charge_out_of_gas"),
        new TestCaseData("6003565b", 21011UL, 3).SetName("JumpDest_charge_out_of_gas_after_Jump"),
        new TestCaseData("60016005575b", 21015UL, 3).SetName("JumpI_charge_out_of_gas"),
        new TestCaseData("60016005575b", 21016UL, 4).SetName("JumpDest_charge_out_of_gas_after_JumpI"),
        new TestCaseData("600161000057", 100000UL, 3).SetName("Push2_JumpI_taken_to_invalid_destination"),
        new TestCaseData("61000556605b00", 100000UL, 2).SetName("Push2_Jump_into_push_data"),
    ];

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    private sealed class StackPushTracer : TestAllTracerWithOutput
    {
        public List<byte[]> Pushes { get; } = [];

        public override void ReportStackPush(in ReadOnlySpan<byte> stackItem) => Pushes.Add(stackItem.ToArray());
    }

    private sealed class CountingCancellationTracer(int cancelAtPoll = int.MaxValue, bool traceInstructions = false) : TestAllTracerWithOutput, ITxTracer
    {
        public int PollCount { get; private set; }

        public override bool IsTracingInstructions => traceInstructions;

        bool ITxTracer.IsCancelable => true;

        bool ITxTracer.IsCancelled => ++PollCount >= cancelAtPoll;
    }

    [Test]
    public void Stop()
    {
        TestAllTracerWithOutput receipt = Execute((byte)Instruction.STOP);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction));
    }

    [Test]
    public void Explicit_fork_activation_does_not_leak_into_later_PrepareTx_calls()
    {
        ForkActivation explicitActivation = MainnetSpecProvider.CancunActivation;
        (Block explicitBlock, _) = PrepareTx(explicitActivation, 100000UL);

        (Block block, _) = PrepareTx(Activation, 100000UL);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explicitBlock.Header.Number, Is.EqualTo(explicitActivation.BlockNumber));
            Assert.That(explicitBlock.Header.Timestamp, Is.EqualTo(explicitActivation.Timestamp));
            Assert.That(block.Header.Number, Is.EqualTo(DefaultBlockNumber));
            Assert.That(block.Header.Timestamp, Is.EqualTo(DefaultTimestamp));
        }
    }

    [Test]
    public void Opcode_refresh_recaptures_frame_handlers()
    {
        Type tableType = (typeof(VirtualMachine<>).GetNestedType("OpcodeTable", BindingFlags.NonPublic)
            ?? throw new AssertionException("OpcodeTable was renamed or removed."))
            .MakeGenericType(typeof(EthereumGasPolicy));
        object table = Activator.CreateInstance(tableType, nonPublic: true)!;
        MethodInfo getHandlers = tableType.GetMethod("GetExecutionHandlers")
            ?? throw new AssertionException("GetExecutionHandlers was renamed or removed.");
        MethodInfo refresh = tableType.GetMethod("RefreshNonTraced")
            ?? throw new AssertionException("RefreshNonTraced was renamed or removed.");
        object[] arguments = [SpecProvider.GenesisSpec];
        object before = getHandlers.Invoke(table, arguments)!;

        refresh.Invoke(table, arguments);
        object after = getHandlers.Invoke(table, arguments)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after, Is.Not.SameAs(before), "refresh must recapture the frame function pointers");
            Assert.That(getHandlers.Invoke(table, arguments), Is.SameAs(after), "subsequent transactions reuse the refreshed handlers");
        }
    }

    [Test]
    public void Original_opcode_factories_are_removed()
    {
        Type vmType = typeof(VirtualMachine<EthereumGasPolicy>);
        foreach (MethodInfo method in vmType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            Assert.That(method.Name, Is.Not.AnyOf("OpcodeHandler", "TerminatingOpcodeHandler", "JumpIfOpcodeHandler", "GetCallHandler", "GetCreateHandler"));
    }

    [Test]
    public void Named_opcode_handlers_are_emitted([Values] Instruction opcode)
    {
        // The handlers must stay in a type named RawCalliHelper: that name is what exempts their table calls
        // from NativeAOT's fat-pointer guard.
        Type dispatchType = typeof(VirtualMachine<EthereumGasPolicy>).GetNestedType("RawCalliHelper", BindingFlags.NonPublic)!
            .MakeGenericType(typeof(EthereumGasPolicy));
        MethodInfo template = dispatchType.GetMethod(opcode == Instruction.JUMPI ? "ExecuteJumpIfOpcode" : "ExecuteOpcode",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo handler = Array.Find(dispatchType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic), method =>
            method.Name.Equals("Op" + opcode, StringComparison.OrdinalIgnoreCase)
            && method.GetGenericArguments().Length == template.GetGenericArguments().Length
            && method.GetParameters().Length == template.GetParameters().Length);
        Assert.That(handler, Is.Not.Null, "the opcode naming weaver must run after InlineIL");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.GetMethodBody()!.GetILAsByteArray()!.Length, Is.EqualTo(template.GetMethodBody()!.GetILAsByteArray()!.Length),
                "the named entry point must contain the dispatch body rather than a forwarding wrapper");
            Assert.That(handler.GetMethodImplementationFlags(), Is.EqualTo(template.GetMethodImplementationFlags()));
            Assert.That(handler.GetCustomAttributesData().Select(a => a.AttributeType), Is.EquivalentTo(template.GetCustomAttributesData().Select(a => a.AttributeType)));
        }
    }

    [TestCase(0x1000, false, TestName = "Thin handler is accepted")]
    [TestCase(0x1002, true, TestName = "Fat handler is rejected")]
    public void Opcode_table_rejects_fat_handlers(long handler, bool rejected)
    {
        Action ensure = () => VirtualMachine<EthereumGasPolicy>.EnsureThinHandler((nint)handler);
        Assert.That(ensure, rejected ? Throws.TypeOf<NotSupportedException>() : Throws.Nothing);
    }

    [Test]
    public void Frame_handlers_are_reused_across_blocks_and_reselected_across_forks()
    {
        Execute((0UL, 0UL), (byte)Instruction.STOP);
        Type vmType = typeof(VirtualMachine<EthereumGasPolicy>);
        object frontier = ReadWarmedOpcodeField(vmType, "_executionHandlers", Machine);
        Execute((1UL, 0UL), (byte)Instruction.STOP);
        Assert.That(ReadWarmedOpcodeField(vmType, "_executionHandlers", Machine), Is.SameAs(frontier));

        Execute((MainnetSpecProvider.SpuriousDragonBlockNumber, 0UL), (byte)Instruction.STOP);
        object spuriousDragon = ReadWarmedOpcodeField(vmType, "_executionHandlers", Machine);
        Assert.That(spuriousDragon, Is.Not.SameAs(frontier));

        Execute((0UL, 0UL), (byte)Instruction.STOP);
        Assert.That(ReadWarmedOpcodeField(vmType, "_executionHandlers", Machine), Is.SameAs(frontier));
    }

    [Test]
    public void Warm_up_opcode_handlers_returns_the_pooled_access_tracker()
    {
        object trackingState;
        using (StackAccessTracker tracker = new())
            trackingState = ReadWarmedOpcodeField(typeof(StackAccessTracker), "_trackingState", tracker);

        Assert.That(
            () => EthereumVirtualMachine.WarmUpEvmInstructions(TestState, CodeInfoRepository),
            Throws.Nothing);

        using StackAccessTracker reused = new();
        Assert.That(ReadWarmedOpcodeField(typeof(StackAccessTracker), "_trackingState", reused), Is.SameAs(trackingState));
    }

    [Test]
    public void Warm_up_code_preserves_each_opcodes_jump_bitmap([Values(Instruction.JUMP, Instruction.JUMPI)] Instruction instruction)
    {
        MethodInfo factory = typeof(VirtualMachine<EthereumGasPolicy>).GetMethod("CreateWarmUpCodeInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
        CodeInfo jump = (CodeInfo)factory.Invoke(null, [instruction])!;
        Assert.That(jump.ValidateJump(1), Is.True);

        CodeInfo push = (CodeInfo)factory.Invoke(null, [Instruction.PUSH32])!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.ValidateJump(1), Is.False);
            Assert.That(jump.ValidateJump(1), Is.True);
            Assert.That(jump.CodeSpan[0], Is.EqualTo((byte)instruction));
            Assert.That(push.CodeSpan[0], Is.EqualTo((byte)Instruction.PUSH32));
        }
    }

    [TestCase(0UL, 0UL)]
    [TestCase(MainnetSpecProvider.ByzantiumBlockNumber, 0UL)]
    [TestCase(20_000_000UL, MainnetSpecProvider.ShanghaiBlockTimestamp - 1)]
    [TestCase(20_000_000UL, MainnetSpecProvider.ShanghaiBlockTimestamp)]
    [TestCase(23_000_000UL, MainnetSpecProvider.PragueBlockTimestamp)]
    [TestCase(25_000_000UL, MainnetSpecProvider.OsakaBlockTimestamp)]
    [TestCase(25_000_000UL, MainnetSpecProvider.BPO2BlockTimestamp)]
    [TestCase(20_000_000UL, 99UL, true)]
    [TestCase(20_000_000UL, 100UL, true)]
    public unsafe void Warm_up_populates_the_processing_specs_opcode_tables(ulong number, ulong timestamp, bool customSchedule = false)
    {
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/foundation.json");
        if (customSchedule)
        {
            chainSpec.ChainId = 12345;
            chainSpec.Parameters.Eip3855TransitionTimestamp = 100;
        }
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(), chainSpec, useTestSpecProvider: false))
            .Build();
        ISpecProvider provider = container.Resolve<ISpecProvider>();
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithTimestamp(timestamp).WithGasLimit(30_000_000).TestObject;
        IReleaseSpec spec = provider.GetSpec(header);
        EthereumVirtualMachine.WarmUpEvmInstructions(TestState, CodeInfoRepository, provider, (number, timestamp));

        object cache = ReadWarmedOpcodeField(typeof(VirtualMachine<EthereumGasPolicy>), "_opcodeTablesBySpec");
        object[] arguments = [spec, null!];
        Assert.That(cache.GetType().GetMethod(nameof(ConditionalWeakTable<object, object>.TryGetValue))!.Invoke(cache, arguments), Is.True,
            "warmup must populate the entry keyed by the chain provider's spec instance");
        object table = arguments[1];
        object warmedExecutionHandlers = ReadWarmedOpcodeField(table.GetType(), "_executionHandlers", table);
        string[] tableNames = ["NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable"];
        object[] warmedTables = new object[tableNames.Length];
        for (int i = 0; i < tableNames.Length; i++)
        {
            warmedTables[i] = ReadWarmedOpcodeField(table.GetType(), tableNames[i], table);
        }
        Machine.SetBlockExecutionContext(new BlockExecutionContext(header, provider.GetSpec(header)));
        object[] processingTables =
        [
            Machine.GetOpcodeHandlers<OffFlag, OffFlag>(),
            Machine.GetOpcodeHandlers<OffFlag, OnFlag>(),
            Machine.GetOpcodeHandlers<OnFlag, OffFlag>(),
            Machine.GetOpcodeHandlers<OnFlag, OnFlag>()
        ];
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < tableNames.Length; i++)
                Assert.That(processingTables[i], Is.SameAs(warmedTables[i]), tableNames[i]);
        }

        TestAllTracerWithOutput tracer = new();
        Transaction tx = new()
        {
            IsServiceTransaction = true,
            GasLimit = 30_000_000,
            SenderAddress = Address.SystemUser,
            To = Address.FromNumber(0x10000)
        };
        _processor.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
        _processor.CallAndRestore(tx, tracer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadWarmedOpcodeField(typeof(VirtualMachine<EthereumGasPolicy>), "_executionHandlers", Machine), Is.SameAs(warmedExecutionHandlers));
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "the warmup contract must be valid for the selected fork");
            Assert.That(tracer.ReportedActionErrors, Is.Empty, "the selected fork's precompile gas cost must be covered");
            if (spec.IsEip196Enabled)
                Assert.That(tracer.Actions, Has.Some.Matches<TestAllTracerWithOutput.ActionTrace>(action =>
                    action.IsPrecompileCall && action.To == BN254AddPrecompile.Address));
        }
    }

    private static object ReadWarmedOpcodeField(Type type, string name, object? instance = null)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | (instance is null ? BindingFlags.Static : BindingFlags.Instance);
        FieldInfo field = type.GetField(name, flags)
            ?? throw new AssertionException($"Opcode cache field {type.Name}.{name} was renamed or removed.");
        return field.GetValue(instance)
            ?? throw new AssertionException($"Opcode cache field {type.Name}.{name} was not populated by warmup.");
    }

    [Test]
    public void Tail_call_opcode_table_dispatch_executes_maximum_length_code_without_growing_the_managed_stack()
    {
        byte[] code = new byte[CodeSizeConstants.MaxCodeSizeEip170];
        Array.Fill(code, (byte)Instruction.JUMPDEST);
        code[^1] = (byte)Instruction.STOP;

        TestAllTracerWithOutput receipt = ExecuteUntraced(100_000UL, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "status");
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + (ulong)code.Length - 1), "gas");
            Assert.That(Machine.OpCodeCount, Is.EqualTo(code.Length), "opcode count");
        }
    }

    [Test]
    public void Tail_call_jumpi_dispatch_executes_a_deep_counted_loop_without_growing_the_managed_stack()
    {
        const int loopIterations = 2_000_000;
        byte[] code = Prepare.EvmCode
            .PushData(loopIterations)
            .Op(Instruction.JUMPDEST)
            .PushData(1)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SUB)
            .Op(Instruction.DUP1)
            .PushData(4)
            .Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .Done;

        const ulong gasLimit = 200_000_000UL;
        TestAllTracerWithOutput receipt = ExecuteUntraced(gasLimit, code, blockGasLimit: gasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "status");
            Assert.That(Machine.OpCodeCount, Is.EqualTo(7 * loopIterations + 2), "opcode count");
        }
    }

    [TestCase(1023, true, Instruction.JUMPDEST)]
    [TestCase(1024, false, Instruction.JUMPDEST)]
    [TestCase(1024, true, Instruction.JUMPDEST)]
    [TestCase(2048, true, Instruction.JUMPDEST)]
    [TestCase(1023, true, Instruction.RETURNDATASIZE)]
    [TestCase(1024, false, Instruction.RETURNDATASIZE)]
    [TestCase(1024, true, Instruction.RETURNDATASIZE)]
    [TestCase(2048, true, Instruction.RETURNDATASIZE)]
    public void Cancellation_is_polled_only_before_the_first_opcode_when_code_takes_no_jump(
        int continuingOpcodeCount,
        bool appendStop,
        Instruction opcode)
    {
        byte[] code = CreateCancellationCode(continuingOpcodeCount, appendStop, opcode);
        CountingCancellationTracer tracer = new();

        Execute(tracer, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null, "straight-line code runs to its end");
            Assert.That(tracer.PollCount, Is.EqualTo(1), "code without a jump only moves forward, so the entry poll bounds it");
            Assert.That(Machine.OpCodeCount, Is.EqualTo(continuingOpcodeCount + (appendStop ? 1 : 0)), "every opcode ran");
        }
    }

    private static readonly string[] EndlessLoops =
    [
        "5b600056",         // JUMPDEST PUSH1 0 JUMP
        "5b6001600057",     // JUMPDEST PUSH1 1 PUSH1 0 JUMPI
        "5b61000056",       // JUMPDEST PUSH2 0 JUMP
        "5b600161000057",   // JUMPDEST PUSH1 1 PUSH2 0 JUMPI
        "5b60001561000057", // JUMPDEST PUSH1 0 ISZERO PUSH2 0 JUMPI
        "5b600160001061000057", // JUMPDEST PUSH1 1 PUSH1 0 LT PUSH2 0 JUMPI
        "5b60026001111561000057", // JUMPDEST PUSH1 2 PUSH1 1 GT ISZERO PUSH2 0 JUMPI
    ];

    [Test]
    public void Cancellation_is_polled_at_a_taken_jump_once_the_loop_passes_the_interval(
        [ValueSource(nameof(EndlessLoops))] string loop, [Values] bool traceInstructions)
    {
        CountingCancellationTracer tracer = new(cancelAtPoll: 2, traceInstructions);

        Assert.Throws<OperationCanceledException>(() => Execute(tracer, Bytes.FromHexString(loop)), "an endless loop must reach a cancellation poll");
        Assert.That(tracer.PollCount, Is.EqualTo(2), "the first poll is at frame entry and the second at a taken jump");
    }

    [Test]
    public void Cancellation_is_polled_at_most_once_per_interval_in_an_uncancelled_loop(
        [ValueSource(nameof(EndlessLoops))] string loop, [Values] bool traceInstructions)
    {
        CountingCancellationTracer tracer = new(traceInstructions: traceInstructions);

        Execute(tracer, Bytes.FromHexString(loop));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.OutOfGas)), "precondition: the loop only ends by running out of gas");
            Assert.That(tracer.PollCount, Is.GreaterThan(1), "a loop past the interval is polled at a taken jump");
            Assert.That(tracer.PollCount, Is.LessThanOrEqualTo(Machine.OpCodeCount / 1024 + 1), "taken jumps poll only once the interval has elapsed");
        }
    }

    private static byte[] CreateCancellationCode(int continuingOpcodeCount, bool appendStop, Instruction opcode)
    {
        byte[] code = new byte[continuingOpcodeCount + (appendStop ? 1 : 0)];
        for (int i = 0; i < continuingOpcodeCount; i++)
            code[i] = (byte)(opcode == Instruction.RETURNDATASIZE && (i & 1) == 0 ? Instruction.POP : opcode);
        if (opcode == Instruction.RETURNDATASIZE)
            code[0] = (byte)opcode;
        if (appendStop)
            code[^1] = (byte)Instruction.STOP;
        return code;
    }

    private static IEnumerable<TestCaseData> FixedCostOpcodeGasCases()
    {
        (Instruction Opcode, int Depth, ulong Cost)[] operations =
        [
            (Instruction.ADD, 2, 3), (Instruction.MUL, 2, 5), (Instruction.SUB, 2, 3),
            (Instruction.ADDMOD, 3, 8), (Instruction.MULMOD, 3, 8),
            (Instruction.DIV, 2, 5), (Instruction.SDIV, 2, 5), (Instruction.MOD, 2, 5),
            (Instruction.SMOD, 2, 5), (Instruction.SIGNEXTEND, 2, 5), (Instruction.LT, 2, 3), (Instruction.GT, 2, 3),
            (Instruction.SLT, 2, 3), (Instruction.SGT, 2, 3), (Instruction.EQ, 2, 3),
            (Instruction.ISZERO, 1, 3), (Instruction.NOT, 1, 3),
            (Instruction.POP, 1, 2), (Instruction.JUMPDEST, 0, 1),
            (Instruction.CALLDATALOAD, 1, 3),
            (Instruction.BYTE, 2, 3), (Instruction.CLZ, 1, 5),
            (Instruction.SHL, 2, 3), (Instruction.SHR, 2, 3), (Instruction.SAR, 2, 3),
            (Instruction.DUP1, 1, 3), (Instruction.DUP2, 2, 3), (Instruction.DUP3, 3, 3), (Instruction.DUP4, 4, 3),
            (Instruction.DUP5, 5, 3), (Instruction.DUP6, 6, 3), (Instruction.DUP7, 7, 3), (Instruction.DUP8, 8, 3),
            (Instruction.DUP9, 9, 3), (Instruction.DUP10, 10, 3), (Instruction.DUP11, 11, 3), (Instruction.DUP12, 12, 3),
            (Instruction.DUP13, 13, 3), (Instruction.DUP14, 14, 3), (Instruction.DUP15, 15, 3), (Instruction.DUP16, 16, 3),
            (Instruction.AND, 2, 3), (Instruction.OR, 2, 3), (Instruction.XOR, 2, 3),
            (Instruction.SWAP1, 2, 3), (Instruction.SWAP2, 3, 3), (Instruction.SWAP3, 4, 3), (Instruction.SWAP4, 5, 3),
            (Instruction.SWAP5, 6, 3), (Instruction.SWAP6, 7, 3), (Instruction.SWAP7, 8, 3), (Instruction.SWAP8, 9, 3),
            (Instruction.SWAP9, 10, 3), (Instruction.SWAP10, 11, 3), (Instruction.SWAP11, 12, 3), (Instruction.SWAP12, 13, 3),
            (Instruction.SWAP13, 14, 3), (Instruction.SWAP14, 15, 3), (Instruction.SWAP15, 16, 3), (Instruction.SWAP16, 17, 3)
        ];
        foreach ((Instruction opcode, int depth, ulong cost) in operations)
        {
            if (opcode is not (>= Instruction.DUP1 and <= Instruction.DUP16))
                foreach (int fullDepth in new[] { 1023, 1024 })
                    foreach (int tracerMode in new[] { 0, 1, 2 })
                        foreach (bool sufficientGas in new[] { false, true })
                            yield return new TestCaseData(opcode, fullDepth, cost, tracerMode, true, sufficientGas, false)
                                .SetName($"Fixed_cost_full_stack_{opcode}_tracer_{tracerMode}_depth_{fullDepth}_gas_{sufficientGas}");
            foreach (int tracerMode in new[] { 0, 1, 2 })
            {
                foreach (bool sufficientStack in depth == 0 ? new[] { true } : new[] { false, true })
                {
                    foreach (bool sufficientGas in new[] { false, true })
                    {
                        foreach (bool appendStop in new[] { false, true })
                        {
                            yield return new TestCaseData(opcode, depth, cost, tracerMode, sufficientStack, sufficientGas, appendStop)
                                .SetName($"Fixed_cost_gas_{opcode}_tracer_{tracerMode}_stack_{sufficientStack}_gas_{sufficientGas}_stop_{appendStop}");
                        }
                    }
                }
            }
        }
    }

    [TestCaseSource(nameof(FixedCostOpcodeGasCases))]
    public void Fixed_cost_opcode_gas_status_preserves_failure_precedence(
        Instruction opcode, int depth, ulong cost, int tracerMode, bool sufficientStack, bool sufficientGas, bool appendStop)
    {
        int pushes = sufficientStack ? depth : depth - 1;
        byte[] code = new byte[pushes * 2 + 1 + (appendStop ? 1 : 0)];
        for (int i = 0; i < pushes; i++)
        {
            code[i * 2] = (byte)Instruction.PUSH1;
            code[i * 2 + 1] = 1;
        }
        code[pushes * 2] = (byte)opcode;
        ulong gasLimit = GasCostOf.Transaction + (ulong)pushes * GasCostOf.VeryLow + cost - (sufficientGas ? 0UL : 1UL);
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);
        block.Header.Number = MainnetSpecProvider.OsakaActivation.BlockNumber;
        block.Header.Timestamp = MainnetSpecProvider.OsakaBlockTimestamp;
        TestAllTracerWithOutput tracer = tracerMode switch
        {
            0 => new NoInstructionTracer(),
            1 => new TestAllTracerWithOutput(),
            _ => new CountingCancellationTracer()
        };

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        string expectedError = !sufficientGas ? nameof(EvmExceptionType.OutOfGas)
            : !sufficientStack ? nameof(EvmExceptionType.StackUnderflow) : null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.EqualTo(expectedError));
            Assert.That(tracer.StatusCode, Is.EqualTo(expectedError is null ? StatusCode.Success : StatusCode.Failure));
            Assert.That(tracer.GasSpent, Is.EqualTo(gasLimit));
            Assert.That(Machine.OpCodeCount, Is.EqualTo(pushes + 1 + (appendStop && expectedError is null ? 1 : 0)));
        }
    }

    /// <remarks>
    /// Reflection discovers checked-body families; opcode mappings are maintained by hand.
    /// Update the mapping and boundary cases when registering another opcode to an existing family.
    /// </remarks>
    [Test]
    public void Checked_opcode_bodies_have_boundary_cases()
    {
        Dictionary<string, Instruction[]> coveredBodies = CheckedBodyOpcodes;
        HashSet<Instruction> coveredOpcodes = [.. StackGrowingOpcodes()];
        foreach (TestCaseData testCase in FixedCostOpcodeGasCases())
            coveredOpcodes.Add((Instruction)testCase.Arguments[0]);

        using (Assert.EnterMultipleScope())
        {
            foreach (Type body in typeof(VirtualMachine<>).GetNestedTypes(BindingFlags.NonPublic))
            {
                if (!body.IsValueType || body.GetProperty("HasCheckedBody", BindingFlags.Public | BindingFlags.Static) is null)
                    continue;

                // Include conditional checked bodies even when this host disables their fast path.
                string name = body.Name.Split('`')[0];
                bool covered = coveredBodies.TryGetValue(name, out Instruction[] opcodes);
                Assert.That(covered, Is.True, $"Add boundary cases for {name}.");
                if (covered)
                    foreach (Instruction opcode in opcodes)
                        Assert.That(coveredOpcodes, Does.Contain(opcode), $"Missing {name}: {opcode}.");
            }
        }
    }

    /// <summary>The opcodes each checked-body family serves; maintained by hand.</summary>
    private static readonly Dictionary<string, Instruction[]> CheckedBodyOpcodes = new()
    {
        ["Math2Opcode"] = [Instruction.ADD, Instruction.MUL, Instruction.SUB, Instruction.DIV, Instruction.SDIV, Instruction.MOD, Instruction.SMOD, Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT],
        ["Math3Opcode"] = [Instruction.ADDMOD, Instruction.MULMOD],
        ["Math1Opcode"] = [Instruction.ISZERO, Instruction.NOT],
        ["BitwiseOpcode"] = [Instruction.EQ, Instruction.AND, Instruction.OR, Instruction.XOR],
        ["CountLeadingZerosOpcode"] = [Instruction.CLZ],
        ["SignExtendOpcode"] = [Instruction.SIGNEXTEND],
        ["ByteOpcode"] = [Instruction.BYTE],
        ["ShiftOpcode"] = [Instruction.SHL, Instruction.SHR],
        ["SarOpcode"] = [Instruction.SAR],
        ["EnvAddressOpcode"] = [Instruction.ADDRESS, Instruction.CALLER],
        ["Env32BytesOpcode"] = [Instruction.ORIGIN, Instruction.CHAINID],
        ["EnvUInt256Opcode"] = [Instruction.CALLVALUE],
        ["EnvUInt32Opcode"] = [Instruction.CALLDATASIZE],
        ["EnvUInt64Opcode"] = [Instruction.MSIZE],
        ["BlkAddressOpcode"] = [Instruction.COINBASE],
        ["BlkUInt256Opcode"] = [Instruction.GASPRICE, Instruction.BASEFEE],
        ["BlkUInt64Opcode"] = [Instruction.TIMESTAMP, Instruction.NUMBER, Instruction.GASLIMIT],
        ["CallDataLoadOpcode"] = [Instruction.CALLDATALOAD],
        ["CodeSizeOpcode"] = [Instruction.CODESIZE],
        ["ReturnDataSizeOpcode"] = [Instruction.RETURNDATASIZE],
        ["PrevRandaoOpcode"] = [Instruction.PREVRANDAO],
        ["SelfBalanceOpcode"] = [Instruction.SELFBALANCE],
        ["PopOpcode"] = [Instruction.POP],
        ["ProgramCounterOpcode"] = [Instruction.PC],
        ["JumpDestOpcode"] = [Instruction.JUMPDEST],
        ["GasOpcode"] = [Instruction.GAS],
        ["Push0Opcode"] = [Instruction.PUSH0],
        ["PushOpcode"] = OpcodeRange(Instruction.PUSH1, Instruction.PUSH32),
        ["DupOpcode"] = OpcodeRange(Instruction.DUP1, Instruction.DUP16),
        ["SwapOpcode"] = OpcodeRange(Instruction.SWAP1, Instruction.SWAP16),
    };

    /// <remarks>
    /// The guest carries the stack head in a register and moves it by a checked body's declared <c>StackGrowth</c>
    /// instead of reading it back, so a declaration the opcode disagrees with would corrupt only the guest's stack.
    /// Each family's first opcode runs over a stack deep enough for any opcode's inputs, and ahead of enough zero
    /// bytes that a STOP follows it even past a PUSH's immediates.
    /// </remarks>
    [Test]
    public void Checked_opcode_bodies_declare_their_net_stack_change()
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (Type body in typeof(VirtualMachine<>).GetNestedTypes(BindingFlags.NonPublic))
            {
                if (!body.IsValueType || body.GetProperty("HasCheckedBody", BindingFlags.Public | BindingFlags.Static) is null)
                    continue;

                int growth = (int?)CloseOverEthereumGasPolicy(body).GetProperty("StackGrowth", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? 0;
                Instruction opcode = CheckedBodyOpcodes[body.Name.Split('`')[0]][0];
                const int inputs = 17;

                byte[] code = new byte[inputs * 2 + 1 + EvmStack.WordSize + 1];
                for (int i = 0; i < inputs; i++)
                {
                    code[i * 2] = (byte)Instruction.PUSH1;
                    code[i * 2 + 1] = 1;
                }
                code[inputs * 2] = (byte)opcode;
                (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code);
                block.Header.Number = MainnetSpecProvider.OsakaActivation.BlockNumber;
                block.Header.Timestamp = MainnetSpecProvider.OsakaBlockTimestamp;
                GethLikeTxMemoryTracer tracer = new(transaction, GethTraceOptions.Default);
                _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

                GethTxTraceEntry stop = tracer.BuildResult().Entries[^1];
                Assert.That(stop.Opcode, Is.EqualTo(nameof(Instruction.STOP)), $"{body.Name} ({opcode})");
                Assert.That(stop.Stack!.Value.Length / EvmStack.WordSize - inputs, Is.EqualTo(growth), $"{body.Name} ({opcode})");
            }
        }
    }

    /// <summary>Closes a nested body type over <see cref="EthereumGasPolicy"/> and the first types that satisfy its other parameters.</summary>
    private static Type CloseOverEthereumGasPolicy(Type body)
    {
        Type[] parameters = body.GetGenericArguments();
        Type[] arguments = new Type[parameters.Length];
        arguments[0] = typeof(EthereumGasPolicy);
        Type[] candidates = [.. typeof(EvmInstructions).Assembly.GetTypes().Concat(typeof(OffFlag).Assembly.GetTypes())
            .Where(static type => type.IsValueType && (!type.IsGenericTypeDefinition || type.GetGenericArguments().Length == 1))
            .Select(static type => type.IsGenericTypeDefinition ? TryClose(type, typeof(EthereumGasPolicy)) : type)
            .OfType<Type>()];
        for (int i = 1; i < parameters.Length; i++)
        {
            Type[] constraints = [.. parameters[i].GetGenericParameterConstraints()
                .Select(constraint => constraint.ContainsGenericParameters ? constraint.GetGenericTypeDefinition().MakeGenericType(typeof(EthereumGasPolicy)) : constraint)];
            arguments[i] = candidates.First(candidate => constraints.All(constraint => constraint.IsAssignableFrom(candidate)));
        }

        return body.MakeGenericType(arguments);

        static Type? TryClose(Type definition, Type argument)
        {
            try { return definition.MakeGenericType(argument); }
            catch (ArgumentException) { return null; }
        }
    }

    private static Instruction[] OpcodeRange(Instruction first, Instruction last)
    {
        Instruction[] opcodes = new Instruction[last - first + 1];
        for (int i = 0; i < opcodes.Length; i++) opcodes[i] = (Instruction)((int)first + i);
        return opcodes;
    }

    private static IEnumerable<TestCaseData> StackGrowthCases()
    {
        foreach (Instruction opcode in StackGrowingOpcodes())
        {
            int width = opcode is >= Instruction.PUSH0 and <= Instruction.PUSH32 ? opcode - Instruction.PUSH0 : 0;
            int[] lengths = width == 0 ? [0] : width == 1 ? [0, 1] : [0, width / 2, width];
            foreach (int depth in new[] { 1023, 1024 })
                foreach (bool sufficientGas in new[] { false, true })
                    foreach (int tracerMode in new[] { 0, 1, 2 })
                        foreach (int immediateLength in lengths)
                            yield return new TestCaseData(opcode, depth, sufficientGas, tracerMode, immediateLength);
        }
    }

    private static IEnumerable<Instruction> StackGrowingOpcodes()
    {
        for (Instruction opcode = Instruction.PUSH0; opcode <= Instruction.DUP16; opcode++) yield return opcode;
        yield return Instruction.PC;
        yield return Instruction.GAS;
        yield return Instruction.CODESIZE;
        yield return Instruction.ADDRESS;
        yield return Instruction.ORIGIN;
        yield return Instruction.CALLER;
        yield return Instruction.CALLVALUE;
        yield return Instruction.CALLDATASIZE;
        yield return Instruction.PREVRANDAO;
        yield return Instruction.RETURNDATASIZE;
        yield return Instruction.SELFBALANCE;
        yield return Instruction.GASPRICE;
        yield return Instruction.COINBASE;
        yield return Instruction.TIMESTAMP;
        yield return Instruction.NUMBER;
        yield return Instruction.GASLIMIT;
        yield return Instruction.CHAINID;
        yield return Instruction.BASEFEE;
        yield return Instruction.MSIZE;
    }

    [Test]
    public void Push_immediate_consumes_only_declared_width([Range(1, 32)] int width, [Values] bool traced)
    {
        byte[] code = new byte[width + 1];
        code[0] = (byte)((byte)Instruction.PUSH1 + width - 1);
        byte[] expected = new byte[32];
        for (int i = 0; i < width; i++)
            expected[32 - width + i] = code[1 + i] = (byte)(0xa0 + i);
        AssertStackValue(code, expected, traced);
    }

    [Test]
    public void Environment_value_is_pushed_after_charging_gas(
        [Values(Instruction.PC, Instruction.GAS, Instruction.CODESIZE)] Instruction opcode, [Values] bool traced)
    {
        UInt256 expected = opcode switch
        {
            Instruction.PC => 0,
            Instruction.GAS => 100000UL - GasCostOf.Transaction - GasCostOf.Base,
            _ => 9
        };
        AssertStackValue([(byte)opcode], expected.ToBigEndian(), traced);
    }

    [Test]
    public void Modular_arithmetic_preserves_full_width_operands(
        [Values(Instruction.ADDMOD, Instruction.MULMOD)] Instruction opcode,
        [Values(0, 1, 251, -1)] int modulusValue, [Values] bool traced)
    {
        BigInteger a = (BigInteger.One << 256) - 1;
        BigInteger b = a - 1;
        BigInteger modulus = modulusValue == -1 ? a - 2 : modulusValue;
        BigInteger expected = modulus.IsZero ? BigInteger.Zero
            : (opcode == Instruction.ADDMOD ? a + b : a * b) % modulus;
        byte[] code = [(byte)Instruction.PUSH32, .. ((UInt256)modulus).ToBigEndian(),
            (byte)Instruction.PUSH32, .. ((UInt256)b).ToBigEndian(),
            (byte)Instruction.PUSH32, .. ((UInt256)a).ToBigEndian(), (byte)opcode];

        AssertStackValue(code, ((UInt256)expected).ToBigEndian(), traced, 9);
    }

    private void AssertStackValue(byte[] prefix, byte[] expected, bool traced, int expectedOpcodeCount = 6)
    {
        byte[] code = [.. prefix, (byte)Instruction.PUSH1, 0, (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH1, 0, (byte)Instruction.RETURN];
        TestAllTracerWithOutput tracer = traced ? new TestAllTracerWithOutput() : new NoInstructionTracer();

        Execute(tracer, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.ReturnValue, Is.EqualTo(expected));
            Assert.That(Machine.OpCodeCount, Is.EqualTo(expectedOpcodeCount));
        }
    }

    [TestCaseSource(nameof(StackGrowthCases))]
    public void Stack_growth_preserves_limit_and_gas_precedence(Instruction opcode, int depth, bool sufficientGas, int tracerMode, int immediateLength)
    {
        byte[] code = new byte[depth * 2 + 1 + immediateLength];
        for (int i = 0; i < depth; i++)
        {
            code[i * 2] = (byte)Instruction.PUSH1;
            code[i * 2 + 1] = 1;
        }
        code[depth * 2] = (byte)opcode;
        code.AsSpan(depth * 2 + 1).Fill(0xa5);
        ulong cost = opcode == Instruction.SELFBALANCE ? GasCostOf.SelfBalance
            : opcode is >= Instruction.PUSH1 and <= Instruction.DUP16 ? GasCostOf.VeryLow : GasCostOf.Base;
        ulong gasLimit = GasCostOf.Transaction + (ulong)depth * GasCostOf.VeryLow + cost - (sufficientGas ? 0UL : 1UL);
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);
        // PrepareTx retains the activation on this shared fixture; change only this execution's header.
        block.Header.Number = MainnetSpecProvider.CancunActivation.BlockNumber;
        block.Header.Timestamp = MainnetSpecProvider.CancunBlockTimestamp;
        TestAllTracerWithOutput tracer = tracerMode switch
        {
            0 => new NoInstructionTracer(),
            1 => new TestAllTracerWithOutput(),
            _ => new CountingCancellationTracer()
        };

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        string expectedError = !sufficientGas ? nameof(EvmExceptionType.OutOfGas)
            : depth == 1024 ? nameof(EvmExceptionType.StackOverflow) : null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.EqualTo(expectedError));
            Assert.That(tracer.StatusCode, Is.EqualTo(expectedError is null ? StatusCode.Success : StatusCode.Failure));
            Assert.That(tracer.GasSpent, Is.EqualTo(gasLimit));
            Assert.That(Machine.OpCodeCount, Is.EqualTo(depth + 1));
        }
    }

    [TestCaseSource(nameof(UntracedCompletionCases))]
    public void Untraced_completion_preserves_semantics(string bytecode, ulong expectedGas, int expectedOpCodeCount, bool cancelable)
    {
        TestAllTracerWithOutput receipt = ExecuteUntraced(100000UL, Bytes.FromHexString(bytecode), cancelable: cancelable);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "status");
            Assert.That(receipt.GasSpent, Is.EqualTo(expectedGas), "gas");
            Assert.That(Machine.OpCodeCount, Is.EqualTo(expectedOpCodeCount), "opcode count");
        }
    }

    [TestCaseSource(nameof(JumpFailureCases))]
    public void Untraced_jump_completion_preserves_failure_ordering(string bytecode, ulong gasLimit, int expectedOpCodeCount)
    {
        TestAllTracerWithOutput receipt = ExecuteUntraced(gasLimit, Bytes.FromHexString(bytecode));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure), "status");
            Assert.That(receipt.GasSpent, Is.EqualTo(gasLimit), "gas");
            Assert.That(Machine.OpCodeCount, Is.EqualTo(expectedOpCodeCount), "opcode count");
        }
    }

    [TestCase(Instruction.JUMP, "600456005b00", 4)]
    [TestCase(Instruction.JUMPI, "6001600657005b00", 6)]
    public void Traced_taken_jump_keeps_jumpdest_visible(Instruction instruction, string bytecode, int target)
    {
        GethLikeTxTrace trace = ExecuteAndTrace(Bytes.FromHexString(bytecode));
        GethTxTraceEntry jumpDest = trace.Entries.Single(static entry => entry.Opcode == nameof(Instruction.JUMPDEST));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(jumpDest.ProgramCounter, Is.EqualTo(target), $"{instruction} target");
            Assert.That(jumpDest.GasCost, Is.EqualTo(GasCostOf.JumpDest), $"{instruction} gas");
        }
    }

    private TestAllTracerWithOutput ExecuteUntraced(ulong gasLimit, byte[] code, ulong blockGasLimit = DefaultBlockGasLimit, bool cancelable = false)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code, blockGasLimit: blockGasLimit);
        TestAllTracerWithOutput tracer = cancelable ? new CountingCancellationTracer() : new NoInstructionTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer;
    }

    [Test]
    public void Trace()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        AssertFirstPushTrace(trace);
    }

    [Test]
    public void Trace_vm_errors()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
    }

    [Test]
    public void Trace_memory_out_of_gas_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData((UInt256)(10 * 1000 * 1000))
            .Op(Instruction.MLOAD)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
    }

    [Test]
    public void Trace_invalid_jump_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData(255)
            .Op(Instruction.JUMP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
    }

    [Test]
    public void Trace_invalid_jumpi_exception()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(255)
            .Op(Instruction.JUMPI)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

        Assert.That(trace.Entries.Any(static e => e.Error is not null), Is.True);
    }

    [Test(Description = "Test a case where the trace is created for one transaction and subsequent untraced transactions keep adding entries to the first trace created.")]
    public void Trace_each_tx_separate()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);

        AssertFirstPushTrace(trace);
    }

    private static void AssertFirstPushTrace(GethLikeTxTrace trace)
    {
        Assert.That(trace.Entries.Count, Is.EqualTo(6), "number of entries");
        GethTxTraceEntry entry = trace.Entries[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Depth, Is.EqualTo(1), nameof(entry.Depth));
            Assert.That(entry.Gas, Is.EqualTo(79000 - GasCostOf.VeryLow), nameof(entry.Gas));
            Assert.That(entry.GasCost, Is.EqualTo(GasCostOf.VeryLow), nameof(entry.GasCost));
            Assert.That(entry.MemoryWordCount(), Is.EqualTo(0), nameof(entry.Memory));
            Assert.That(entry.StackWordCount(), Is.EqualTo(1), nameof(entry.Stack));
            Assert.That(entry.Storage, Is.Null, nameof(entry.Storage));
            Assert.That(trace.Entries[4].Opcode, Is.EqualTo("SSTORE"), "SSTORE opcode");
            Assert.That(trace.Entries[5].Opcode, Is.EqualTo("STOP"), "implicit STOP opcode");
            Assert.That(entry.ProgramCounter, Is.EqualTo(2), nameof(entry.ProgramCounter));
            Assert.That(entry.Opcode, Is.EqualTo("PUSH1"), nameof(entry.Opcode));
        }

        // Storage is populated lazily during serialization; verify via JSON.
        using JsonDocument doc = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(trace));
        JsonElement sstoreEntry = doc.RootElement.GetProperty("structLogs")[4];
        JsonElement storage = sstoreEntry.GetProperty("storage");
        const string zero32 = "0x0000000000000000000000000000000000000000000000000000000000000000";
        Assert.That(storage.EnumerateObject().Count(), Is.EqualTo(1), "SSTORE storage has one slot");
        Assert.That(storage.GetProperty(zero32).GetString(), Is.EqualTo(zero32), "SSTORE storage[0x0]=0x0");
    }

    [Test]
    public void Create_instruction_callbacks_are_paired(
        [Values(Instruction.CREATE, Instruction.CREATE2)] Instruction instruction)
    {
        byte[] code = instruction switch
        {
            Instruction.CREATE => Prepare.EvmCode.Create([], UInt256.Zero).Op(Instruction.STOP).Done,
            Instruction.CREATE2 => Prepare.EvmCode.Create2([], [1], UInt256.Zero).Op(Instruction.STOP).Done,
            _ => throw new ArgumentOutOfRangeException(nameof(instruction), instruction, null)
        };
        InstructionCallbackTracer tracer = new();

        ExecuteAmsterdam(tracer, code);

        ulong[] expectedGas = instruction switch
        {
            Instruction.CREATE => [978_997, 978_994, 978_991, 783_391, 783_391],
            Instruction.CREATE2 => [978_997, 978_994, 978_991, 978_988, 783_388, 783_388],
            _ => throw new ArgumentOutOfRangeException(nameof(instruction), instruction, null)
        };
        AssertCallbacksPaired(tracer, expectedGas.Length);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.GetStarts(), Is.EqualTo(instruction switch
            {
                Instruction.CREATE => new (Instruction, int, ulong)[]
                {
                    (Instruction.PUSH1, 0, 979_000),
                    (Instruction.PUSH1, 0, 978_997),
                    (Instruction.PUSH1, 0, 978_994),
                    (Instruction.CREATE, 0, 978_991),
                    (Instruction.STOP, 0, 783_391)
                },
                Instruction.CREATE2 => new (Instruction, int, ulong)[]
                {
                    (Instruction.PUSH1, 0, 979_000),
                    (Instruction.PUSH1, 0, 978_997),
                    (Instruction.PUSH1, 0, 978_994),
                    (Instruction.PUSH1, 0, 978_991),
                    (Instruction.CREATE2, 0, 978_988),
                    (Instruction.STOP, 0, 783_388)
                },
                _ => throw new ArgumentOutOfRangeException(nameof(instruction), instruction, null)
            }));
            Assert.That(tracer.GetCompletedGas(), Is.EqualTo(expectedGas));
        }
    }

    [Test]
    public void Create2_collision_instruction_callbacks_are_paired()
    {
        byte[] code = Prepare.EvmCode
            .Create2([], [1], UInt256.Zero)
            .Create2([], [1], UInt256.Zero)
            .Op(Instruction.STOP)
            .Done;
        InstructionCallbackTracer tracer = new();

        ExecuteAmsterdam(tracer, code);

        AssertCallbacksPaired(tracer, 11);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.GetStarts(), Is.EqualTo(new (Instruction, int, ulong)[]
            {
                (Instruction.PUSH1, 0, 979_000),
                (Instruction.PUSH1, 0, 978_997),
                (Instruction.PUSH1, 0, 978_994),
                (Instruction.PUSH1, 0, 978_991),
                (Instruction.CREATE2, 0, 978_988),
                (Instruction.PUSH1, 0, 783_388),
                (Instruction.PUSH1, 0, 783_385),
                (Instruction.PUSH1, 0, 783_382),
                (Instruction.PUSH1, 0, 783_379),
                (Instruction.CREATE2, 0, 783_376),
                (Instruction.STOP, 0, 12_052)
            }));
            Assert.That(tracer.GetCompletedGas(), Is.EqualTo(new ulong[]
            {
                978_997, 978_994, 978_991, 978_988, 783_388,
                783_385, 783_382, 783_379, 783_376, 771_376, 12_052
            }));
        }
    }

    [Test]
    public void Create_child_completion_callbacks_are_paired(
        [Values(Instruction.RETURN, Instruction.REVERT, Instruction.INVALID)] Instruction halt)
    {
        byte[] initCode = halt switch
        {
            Instruction.RETURN => Prepare.EvmCode.Return(0, 0).Done,
            Instruction.REVERT => Prepare.EvmCode.Revert(0, 0).Done,
            Instruction.INVALID => Prepare.EvmCode.Op(Instruction.INVALID).Done,
            _ => throw new ArgumentOutOfRangeException(nameof(halt), halt, null)
        };
        byte[] code = Prepare.EvmCode.Create(initCode, UInt256.Zero).Op(Instruction.STOP).Done;
        InstructionCallbackTracer tracer = new();

        ExecuteAmsterdam(tracer, code);

        AssertCallbacksPaired(tracer, halt == Instruction.INVALID ? 9 : 11,
            halt == Instruction.INVALID
                ? [(Instruction.INVALID, 1, EvmExceptionType.BadInstruction)]
                : []);
        Assert.That(tracer.GetCompletedGas(), Is.EqualTo(halt switch
        {
            Instruction.RETURN => new ulong[]
            {
                978_997, 978_994, 978_988, 978_985, 978_982, 978_979, 783_377,
                771_134, 771_131, 771_131, 783_371
            },
            Instruction.REVERT => new ulong[]
            {
                978_997, 978_994, 978_988, 978_985, 978_982, 978_979, 783_377,
                771_134, 771_131, 771_131, 966_971
            },
            Instruction.INVALID => new ulong[]
            {
                978_997, 978_994, 978_988, 978_985, 978_982, 978_979, 783_377,
                771_127, 195_840
            },
            _ => throw new ArgumentOutOfRangeException(nameof(halt), halt, null)
        }));
    }

    [Test]
    public void Call_child_completion_callbacks_are_paired(
        [Values(Instruction.RETURN, Instruction.REVERT, Instruction.INVALID)] Instruction halt)
    {
        byte[] childCode = halt switch
        {
            Instruction.RETURN => Prepare.EvmCode.Return(0, 0).Done,
            Instruction.REVERT => Prepare.EvmCode.Revert(0, 0).Done,
            Instruction.INVALID => Prepare.EvmCode.Op(Instruction.INVALID).Done,
            _ => throw new ArgumentOutOfRangeException(nameof(halt), halt, null)
        };
        TestState.CreateAccount(TestItem.AddressC, UInt256.Zero);
        TestState.InsertCode(TestItem.AddressC, childCode, SpecProvider.GenesisSpec);
        byte[] code = Prepare.EvmCode.Call(TestItem.AddressC, 50_000).Op(Instruction.STOP).Done;
        InstructionCallbackTracer tracer = new();

        ExecuteAmsterdam(tracer, code);

        AssertCallbacksPaired(tracer, halt == Instruction.INVALID ? 10 : 12,
            halt == Instruction.INVALID
                ? [(Instruction.INVALID, 1, EvmExceptionType.BadInstruction)]
                : []);
        Assert.That(tracer.GetCompletedGas(), Is.EqualTo(halt == Instruction.INVALID
            ? new ulong[] { 978_997, 978_994, 978_991, 978_988, 978_985, 978_982, 978_979, 925_979, 49_990, 925_979 }
            : new ulong[] { 978_997, 978_994, 978_991, 978_988, 978_985, 978_982, 978_979, 925_979, 49_997, 49_994, 49_994, 975_973 }));
    }

    [Test]
    public void Create_insufficient_balance_callbacks_are_paired()
    {
        byte[] code = Prepare.EvmCode.Create([], 200.Ether).Op(Instruction.STOP).Done;
        InstructionCallbackTracer tracer = new();

        ExecuteAmsterdam(tracer, code);

        AssertCallbacksPaired(tracer, 5);
        Assert.That(tracer.GetCompletedGas(), Is.EqualTo(new ulong[]
        {
            978_997, 978_994, 978_991, 966_991, 966_991
        }));
    }

    [Test]
    public void Call_insufficient_balance_callbacks_are_paired()
    {
        byte[] code = Prepare.EvmCode.CallWithValue(TestItem.AddressC, 50_000, 200.Ether).Op(Instruction.STOP).Done;
        InstructionCallbackTracer tracer = new();

        ExecuteAmsterdam(tracer, code);

        AssertCallbacksPaired(tracer, 9, (Instruction.CALL, 0, EvmExceptionType.NotEnoughBalance));
        Assert.That(tracer.GetCompletedGas(), Is.EqualTo(new ulong[]
        {
            978_997, 978_994, 978_991, 978_988, 978_985, 978_982, 978_979, 731_079, 966_979
        }));
    }

    [Test]
    public void Call_at_max_depth_reports_depth_error()
    {
        // Calls itself with all but 512 gas, then, once the call fails, calls the identity precompile with no value and
        // with 1 wei, and creates. Before EIP-150 a call forwards what it asks for, so 2M gas reaches the depth limit.
        byte[] code = Bytes.FromHexString("0x60006000600060006000306102005a03f1603c576000600060006000600060046000f1506000600060006000600160046000f150600060006000f0505b00");
        (Block block, Transaction transaction) = PrepareTx((ForkActivation)MainnetSpecProvider.HomesteadBlockNumber, 2_000_000, code);
        InstructionCallbackTracer tracer = new();

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.GetErrors(), Is.EqualTo(Enumerable.Repeat((Instruction.CALL, VirtualMachineStatics.MaxCallDepth, EvmExceptionType.CallDepthExceeded), 3)));
    }

    [Test]
    public void Create_nonce_overflow_callbacks_are_paired()
    {
        byte[] code = Prepare.EvmCode.Create([], UInt256.Zero).Op(Instruction.STOP).Done;
        InstructionCallbackTracer tracer = new();

        ExecuteAmsterdam(tracer, code, static state => state.SetNonce(Recipient, ulong.MaxValue));

        AssertCallbacksPaired(tracer, 5);
        Assert.That(tracer.GetCompletedGas(), Is.EqualTo(new ulong[]
        {
            978_997, 978_994, 978_991, 966_991, 966_991
        }));
    }

    [Test]
    public void Create_depth_limit_callbacks_are_paired()
    {
        byte[] code = Prepare.EvmCode.Create([], UInt256.Zero).Op(Instruction.STOP).Done;
        CodeInfo codeInfo = new(code);
        ExecutionEnvironment env = ExecutionEnvironment.Rent(
            codeInfo,
            executingAccount: Recipient,
            caller: Sender,
            codeSource: Recipient,
            callDepth: VirtualMachineStatics.MaxCallDepth,
            value: UInt256.Zero,
            inputData: ReadOnlyMemory<byte>.Empty);
        EthereumGasPolicy gas = new()
        {
            Value = 1_000_000,
            StateReservoir = GasCostOf.CreateState,
        };
        StackAccessTracker accessTracker = new();
        using VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            gas,
            ExecutionType.CALL,
            env,
            in accessTracker,
            Snapshot.Empty);
        InstructionCallbackTracer tracer = new();
        Machine.SetBlockExecutionContext(new BlockExecutionContext(Build.A.Block.TestObject.Header, Amsterdam.Instance));
        Machine.SetTxExecutionContext(new TxExecutionContext(Sender, CodeInfoRepository, null, UInt256.Zero));

        Machine.ExecuteTransaction<OnFlag>(vmState, TestState, tracer);

        AssertCallbacksPaired(tracer, 5);
        Assert.That(tracer.GetCompletedGas(), Is.EqualTo(new ulong[]
        {
            999_997, 999_994, 999_991, 987_991, 987_991
        }));
    }

    [Test]
    public void Implicit_stop_is_only_traced_by_opted_in_tracers([Values(1, 0x01020304)] int value)
    {
        byte[] code = Prepare.EvmCode.PushData(value).Done;
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code);
        GethLikeTxMemoryTracer gethTracer = new(transaction, GethTraceOptions.Default);
        ParityLikeTxTracer parityTracer = new(block, transaction, ParityTraceTypes.VmTrace);
        CountingGethLikeTxTracer countingTracer = new();
        CompositeTxTracer tracer = new(gethTracer, parityTracer, countingTracer);

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        GethLikeTxTrace gethTrace = gethTracer.BuildResult();
        ParityLikeTxTrace parityTrace = parityTracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gethTrace.Entries[^1].Opcode, Is.EqualTo(nameof(Instruction.STOP)));
            Assert.That(gethTrace.Entries[^1].ProgramCounter, Is.EqualTo(code.Length));
            Assert.That(gethTrace.Entries[^1].GetStackWord(0), Is.EqualTo((UInt256)value));
            Assert.That(parityTrace.VmTrace.Operations, Has.Count.EqualTo(1));
            Assert.That(countingTracer.StartedOperations, Is.EqualTo(2));
            Assert.That(countingTracer.CompletedOperations, Is.EqualTo(2));
        }
    }

    [Test]
    public void Empty_code_does_not_trace_implicit_stop()
    {
        GethLikeTxTrace trace = ExecuteAndTrace(Array.Empty<byte>());

        Assert.That(trace.Entries, Is.Empty);
    }

    [TestCase(Instruction.CALL)]
    [TestCase(Instruction.STATICCALL)]
    public void Final_call_traces_implicit_stop_after_resuming_caller(Instruction instruction)
    {
        byte[] calleeCode = [(byte)Instruction.STOP];
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        byte[] code = instruction switch
        {
            Instruction.CALL => Prepare.EvmCode.Call(TestItem.AddressC, 50_000).Done,
            Instruction.STATICCALL => Prepare.EvmCode.StaticCall(TestItem.AddressC, 50_000).Done,
            _ => throw new ArgumentOutOfRangeException(nameof(instruction), instruction, null)
        };

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        GethTxTraceEntry callerStop = trace.Entries.Last(static entry => entry is { Depth: 1, Opcode: nameof(Instruction.STOP) });

        Assert.That(callerStop.ProgramCounter, Is.EqualTo(code.Length));
    }

    [Test]
    public void Implicit_stop_honors_cancellation_wrapper()
    {
        byte[] code = Prepare.EvmCode.PushData(1).Done;
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code);
        using CancellationTokenSource cancellation = new();
        CancelAfterFirstOperationTracer innerTracer = new(cancellation);
        CancellationTxTracer tracer = new(innerTracer, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<OperationCanceledException>(() =>
                _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer));
            Assert.That(innerTracer.StartedOperations, Is.EqualTo(1));
            Assert.That(innerTracer.CompletedOperations, Is.EqualTo(1));
        }
    }

    [Test]
    public void Cancellation_after_instruction_start_completes_started_instruction()
    {
        byte[] code = [(byte)Instruction.STOP];
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code);
        using CancellationTokenSource cancellation = new();
        CancelOnOperationStartTracer innerTracer = new(cancellation);
        CancellationTxTracer tracer = new(innerTracer, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<OperationCanceledException>(() =>
                _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer));
            Assert.That(innerTracer.StartedOperations, Is.EqualTo(1));
            Assert.That(innerTracer.CompletedOperations, Is.EqualTo(1));
        }
    }

    [Test]
    public void Cancellation_wraps_all_observers_before_forwarding_the_next_instruction([Values] bool cancellationFirst)
    {
        byte[] code = Prepare.EvmCode.PushData(1).Op(Instruction.STOP).Done;
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code);
        using CancellationTokenSource cancellation = new();
        CountingGethLikeTxTracer observer = new();
        CancelOnOperationStartTracer inner = new(cancellation);
        CompositeTxTracer composite = cancellationFirst ? new(inner, observer) : new(observer, inner);
        CancellationTxTracer tracer = new(composite, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<OperationCanceledException>(() =>
                _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer));
            Assert.That(observer.StartedOperations, Is.EqualTo(1));
            Assert.That(observer.CompletedOperations, Is.EqualTo(1));
        }
    }

    [Test]
    public void Cancellation_during_implicit_stop_completes_every_opted_in_tracer()
    {
        using CancellationTokenSource cancellation = new();
        CountingGethLikeTxTracer first = new(cancellation);
        CountingGethLikeTxTracer second = new();
        CancellationTxTracer tracer = new(new CompositeTxTracer(first, second), cancellation.Token);

        Assert.Throws<OperationCanceledException>(() => Execute(tracer, Prepare.EvmCode.PushData(1).Done));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.StartedOperations, Is.EqualTo(2));
            Assert.That(first.CompletedOperations, Is.EqualTo(2));
            Assert.That(second.StartedOperations, Is.EqualTo(2));
            Assert.That(second.CompletedOperations, Is.EqualTo(2));
        }
    }

    [Test]
    public void Cancellation_after_exception_start_reports_completion_and_error([Values] bool cancellationFirst)
    {
        byte[] code = [(byte)Instruction.INVALID];
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code);
        using CancellationTokenSource cancellation = new();
        CancelOnOperationStartTracer innerTracer = new(cancellation);
        InstructionCallbackTracer observer = new();
        CompositeTxTracer composite = cancellationFirst ? new(innerTracer, observer) : new(observer, innerTracer);
        CancellationTxTracer tracer = new(composite, cancellation.Token);

        Assert.Throws<OperationCanceledException>(() =>
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer));
        AssertCallbacksPaired(observer, 1, (Instruction.INVALID, 0, EvmExceptionType.BadInstruction));
        Assert.That(innerTracer.Callbacks, Is.EqualTo(new[]
        {
            $"start:{Instruction.INVALID}",
            "remaining-gas",
            $"error:{EvmExceptionType.BadInstruction}"
        }));
    }

    private sealed class CountingGethLikeTxTracer(CancellationTokenSource? cancelOnStop = null) : GethLikeTxTracer(new GethTraceOptions())
    {
        public int StartedOperations { get; private set; }
        public int CompletedOperations { get; private set; }

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            StartedOperations++;
            if (opcode == Instruction.STOP) cancelOnStop?.Cancel();
        }

        public override void ReportOperationRemainingGas(ulong gas) => CompletedOperations++;
    }

    private sealed class InstructionCallbackTracer : TxTracer
    {
        private InstructionCallback? _activeOperation;
        private InstructionCallback? _completedOperation;

        public override bool IsTracingInstructions => true;

        public List<InstructionCallback> Operations { get; } = [];
        public bool HasActiveOperation => _activeOperation is not null;

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            Assert.That(_activeOperation, Is.Null, "the previous instruction must complete before the next starts");
            _completedOperation = null;
            _activeOperation = new(opcode, env.CallDepth, gas);
            Operations.Add(_activeOperation);
        }

        public override void ReportOperationRemainingGas(ulong gas)
        {
            Assert.That(_activeOperation, Is.Not.Null, "an instruction completion must have a matching start");
            _activeOperation!.RemainingGas = gas;
            _completedOperation = _activeOperation;
            _activeOperation = null;
        }

        public override void ReportOperationError(EvmExceptionType error)
        {
            Assert.That(_completedOperation, Is.Not.Null, "an instruction error must follow its completion");
            _completedOperation!.Error = error;
            _completedOperation = null;
        }

        public ulong[] GetCompletedGas()
        {
            ulong[] gas = new ulong[Operations.Count];
            for (int i = 0; i < Operations.Count; i++)
            {
                gas[i] = Operations[i].RemainingGas!.Value;
            }

            return gas;
        }

        public (Instruction Opcode, int Depth, EvmExceptionType Error)[] GetErrors()
        {
            List<(Instruction Opcode, int Depth, EvmExceptionType Error)> errors = [];
            foreach (InstructionCallback operation in Operations)
            {
                if (operation.Error is not null)
                {
                    errors.Add((operation.Opcode, operation.Depth, operation.Error.Value));
                }
            }

            return errors.ToArray();
        }

        public (Instruction Opcode, int Depth, ulong Gas)[] GetStarts()
        {
            (Instruction Opcode, int Depth, ulong Gas)[] starts = new (Instruction, int, ulong)[Operations.Count];
            for (int i = 0; i < Operations.Count; i++)
            {
                InstructionCallback operation = Operations[i];
                starts[i] = (operation.Opcode, operation.Depth, operation.StartGas);
            }

            return starts;
        }
    }

    private sealed class InstructionCallback(Instruction opcode, int depth, ulong startGas)
    {
        public Instruction Opcode { get; } = opcode;
        public int Depth { get; } = depth;
        public ulong StartGas { get; } = startGas;
        public ulong? RemainingGas { get; set; }
        public EvmExceptionType? Error { get; set; }
    }

    private static void AssertCallbacksPaired(
        InstructionCallbackTracer tracer,
        int expectedCount,
        params (Instruction Opcode, int Depth, EvmExceptionType Error)[] expectedErrors)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Operations, Has.Count.EqualTo(expectedCount));
            Assert.That(tracer.HasActiveOperation, Is.False);
            Assert.That(tracer.Operations, Has.All.Property(nameof(InstructionCallback.RemainingGas)).Not.Null);
            Assert.That(tracer.GetErrors(), Is.EqualTo(expectedErrors));
        }
    }

    private void ExecuteAmsterdam(ITxTracer tracer, byte[] code, Action<IWorldState>? configureState = null)
    {
        const ulong gasLimit = 1_000_000;
        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            code);
        block.Header.Number = MainnetSpecProvider.AmsterdamActivation.BlockNumber;
        block.Header.Timestamp = MainnetSpecProvider.AmsterdamBlockTimestamp;
        configureState?.Invoke(TestState);

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
    }

    private sealed class CancelAfterFirstOperationTracer(CancellationTokenSource cancellation) : GethLikeTxTracer(new GethTraceOptions())
    {
        public int StartedOperations { get; private set; }
        public int CompletedOperations { get; private set; }

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env) =>
            StartedOperations++;

        public override void ReportOperationRemainingGas(ulong gas)
        {
            CompletedOperations++;
            cancellation.Cancel();
        }
    }

    private sealed class CancelOnOperationStartTracer(CancellationTokenSource cancellation) : GethLikeTxTracer(new GethTraceOptions())
    {
        public int StartedOperations { get; private set; }
        public int CompletedOperations { get; private set; }
        public List<string> Callbacks { get; } = [];

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            StartedOperations++;
            Callbacks.Add($"start:{opcode}");
            cancellation.Cancel();
        }

        public override void ReportOperationRemainingGas(ulong gas)
        {
            CompletedOperations++;
            Callbacks.Add("remaining-gas");
        }

        public override void ReportOperationError(EvmExceptionType error) => Callbacks.Add($"error:{error}");
    }

    [Test]
    public void Add_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset), "gas");
            AssertStorage(UInt256.Zero, UInt256.Zero);
        }
    }

    [Test]
    public void Add_0_1()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
            AssertStorage(UInt256.Zero, UInt256.One);
        }
    }

    [Test]
    public void Add_1_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
            AssertStorage(UInt256.Zero, UInt256.One);
        }
    }

    [Test]
    public void Mstore()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96, // data
            (byte)Instruction.PUSH1,
            64, // position
            (byte)Instruction.MSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mstore_twice_same_location()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 6 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mload()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            64, // position
            (byte)Instruction.MLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mload_after_mstore()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Dup1()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.DUP1);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2), "gas");
    }

    [Test]
    public void Codecopy()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            32, // length
            (byte)Instruction.PUSH1,
            0, // src
            (byte)Instruction.PUSH1,
            32, // dest
            (byte)Instruction.CODECOPY);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Swap()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            32, // length
            (byte)Instruction.PUSH1,
            0, // src
            (byte)Instruction.SWAP1);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3), "gas");
    }

    [Test]
    public void Sload()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0, // index
            (byte)Instruction.SLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.SLoadEip150), "gas");
    }

    [Test]
    public void Exp_2_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            2,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet + GasCostOf.Exp + GasCostOf.ExpByteEip160), "gas");
            AssertStorage(UInt256.Zero, BigInteger.Pow(2, 160));
        }
    }

    [Test]
    public void Exp_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.SSet), "gas");
            AssertStorage(UInt256.Zero, UInt256.One);
        }
    }

    [Test]
    public void Exp_0_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SReset), "gas");
            AssertStorage(UInt256.Zero, UInt256.Zero);
        }
    }

    [Test]
    public void Exp_1_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SSet), "gas");
            AssertStorage(UInt256.Zero, UInt256.One);
        }
    }

    [TestCase(0, 2, "0x01", TestName = "Exp_ZeroExponent_ReportsOneByteOne")]
    [TestCase(160, 0, "0x00", TestName = "Exp_ZeroBase_ReportsOneByteZero")]
    [TestCase(160, 1, "0x01", TestName = "Exp_OneBase_ReportsOneByteOne")]
    [TestCase(160, 2, "0x0000000000000000000000010000000000000000000000000000000000000000", TestName = "Exp_Computed_ReportsFullWord")]
    public void Exp_WhenTraced_ReportsResultPush(int exponent, int baseValue, string expectedPush)
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1,
            (byte)exponent,
            (byte)Instruction.PUSH1,
            (byte)baseValue,
            (byte)Instruction.EXP,
        ];
        StackPushTracer tracer = Execute(new StackPushTracer(), code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null, "EXP with both operands on the stack succeeds");
            Assert.That(tracer.Pushes, Has.Count.EqualTo(3), "two PUSH1 results then the EXP result");
            Assert.That(tracer.Pushes[2], Is.EqualTo(Bytes.FromHexString(expectedPush)), "EXP reports the same push shape for each result path");
        }
    }

    [Test]
    public void Sub_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SUB,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
            AssertStorage(UInt256.Zero, UInt256.Zero);
        }
    }

    [Test]
    public void Not_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.NOT,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet), "gas");
            AssertStorage(UInt256.Zero, UInt256.MaxValue);
        }
    }

    [Test]
    public void Or_0_0()
    {
        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ByzantiumBlockNumber, null),
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.OR,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
            AssertStorage(UInt256.Zero, UInt256.Zero);
        }
    }

    [Test]
    public void Sstore_twice_0_same_storage_should_refund_only_once()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.SReset), "gas");
            AssertStorage(UInt256.Zero, UInt256.Zero);
        }
    }

    /// <summary>
    /// TLoad gas cost check
    /// </summary>
    [Test]
    public void Tload()
    {
        byte[] code = Prepare.EvmCode
            .PushData(96)
            .Op(Instruction.TLOAD)
            .Done;

        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp), 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.TLoad), "gas");
    }

    /// <summary>
    /// MCOPY gas cost check
    /// </summary>
    [Test]
    public void MCopy()
    {
        byte[] data = new byte[] { 0x60, 0x17, 0x60, 0x03, 0x02, 0x00 };
        byte[] code = Prepare.EvmCode
            .MSTORE(0, data.PadRight(32))
            .MCOPY(6, 0, 6)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default), code, MainnetSpecProvider.CancunActivation).BuildResult();

        Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)((data.Length + 31) / 32) + GasCostOf.Memory * 0UL), "gas");
    }

    [Test]
    public void MCopy_exclusive_areas()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(32, 0, 32)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        UInt256 copied = traces.Entries.Last().GetMemoryWord(0);
        UInt256 origin = traces.Entries.Last().GetMemoryWord(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)((data.Length + 31) / 32) + GasCostOf.Memory * 1UL), "gas");
            Assert.That(origin, Is.EqualTo(copied));
        }
    }


    [Test]
    public void MCopy_Overwrite_areas_copy_right()
    {
        int SLICE_SIZE = 8;
        byte[] data = Bytes.FromHexString("0102030405060708000000000000000000000000000000000000000000000000");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(1, 0, (UInt256)SLICE_SIZE)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        UInt256 result = traces.Entries.Last().GetMemoryWord(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)(SLICE_SIZE + 31) / 32), "gas");
            Assert.That(result, Is.EqualTo(new UInt256(Bytes.FromHexString("0x0101020304050607080000000000000000000000000000000000000000000000"), isBigEndian: true)), "memory state");
        }
    }

    [Test]
    public void MCopy_twice_same_location()
    {
        byte[] data = Bytes.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(0, 0, 32)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)((data.Length + 31) / 32)), "gas");
            Assert.That(traces.Entries.Last().MemoryWordCount(), Is.EqualTo(1));
        }
    }

    [Test]
    public void MCopy_zero_length_does_not_validate_offsets()
    {
        byte[] bytecode = Prepare.EvmCode
            .MCOPY(UInt256.MaxValue, UInt256.MaxValue, UInt256.Zero)
            .STOP()
            .Done;

        TestAllTracerWithOutput receipt = Execute(MainnetSpecProvider.CancunActivation, bytecode);

        Assert.That(receipt.Error, Is.Null);
    }

    private static IEnumerable<TestCaseData> ZeroLengthMemoryRangeAtMaxOffsetCases()
    {
        yield return new TestCaseData((Func<UInt256, byte[]>)(static offset => Prepare.EvmCode.KECCAK256(offset, 0).STOP().Done), StatusCode.Success)
            .SetName("Zero_length_KECCAK256_ignores_its_offset");
        yield return new TestCaseData((Func<UInt256, byte[]>)(static offset => Prepare.EvmCode.LOGx(0, offset, 0).STOP().Done), StatusCode.Success)
            .SetName("Zero_length_LOG0_ignores_its_offset");
        yield return new TestCaseData((Func<UInt256, byte[]>)(static offset => Prepare.EvmCode.RETURN(offset, 0).Done), StatusCode.Success)
            .SetName("Zero_length_RETURN_ignores_its_offset");
        yield return new TestCaseData((Func<UInt256, byte[]>)(static offset => Prepare.EvmCode.REVERT(offset, 0).Done), StatusCode.Failure)
            .SetName("Zero_length_REVERT_ignores_its_offset");
        yield return new TestCaseData(
                (Func<UInt256, byte[]>)(static offset => Prepare.EvmCode.CALL(50_000, TestItem.AddressC, 0, offset, 0, offset, 0).STOP().Done),
                StatusCode.Success)
            .SetName("Zero_length_CALL_input_and_output_ignore_their_offsets");
        yield return new TestCaseData(
                (Func<UInt256, byte[]>)(static offset => Prepare.EvmCode.STATICCALL(50_000, IdentityPrecompile.Address, offset, 0, offset, 0).STOP().Done),
                StatusCode.Success)
            .SetName("Zero_length_precompile_STATICCALL_input_and_output_ignore_their_offsets");
    }

    [TestCaseSource(nameof(ZeroLengthMemoryRangeAtMaxOffsetCases))]
    public void Zero_length_memory_range_ignores_its_offset(Func<UInt256, byte[]> buildCode, byte expectedStatus)
    {
        byte[] code = buildCode(UInt256.MaxValue);
        byte[] baselineCode = buildCode(UInt256.Zero);
        CallOutputTracer untraced = Execute(new CallOutputTracer(), code, MainnetSpecProvider.CancunActivation);
        TestAllTracerWithOutput traced = Execute(new TestAllTracerWithOutput(), code, MainnetSpecProvider.CancunActivation);
        CallOutputTracer untracedBaseline = Execute(new CallOutputTracer(), baselineCode, MainnetSpecProvider.CancunActivation);
        TestAllTracerWithOutput tracedBaseline = Execute(new TestAllTracerWithOutput(), baselineCode, MainnetSpecProvider.CancunActivation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(untraced.StatusCode, Is.EqualTo(expectedStatus), "the untraced run takes the inline fast paths");
            Assert.That(traced.StatusCode, Is.EqualTo(expectedStatus), "the traced run creates full call frames");
            if (expectedStatus == StatusCode.Failure)
            {
                Assert.That(untraced.Error, Is.EqualTo(TransactionSubstate.Revert), "an explicit revert, not an exceptional halt");
                Assert.That(traced.Error, Is.EqualTo(TransactionSubstate.Revert), "an explicit revert, not an exceptional halt");
            }

            Assert.That(untraced.GasSpent, Is.EqualTo(untracedBaseline.GasSpent), "a zero-length range charges no memory expansion at any offset");
            Assert.That(traced.GasSpent, Is.EqualTo(tracedBaseline.GasSpent), "a zero-length range charges no memory expansion at any offset");
        }
    }

    [Test]
    public void MCopy_Overwrite_areas_copy_left()
    {
        int SLICE_SIZE = 8;
        byte[] data = Bytes.FromHexString("0001020304050607080000000000000000000000000000000000000000000000");
        byte[] bytecode = Prepare.EvmCode
            .MSTORE(0, data)
            .MCOPY(0, 1, (UInt256)SLICE_SIZE)
            .STOP()
            .Done;
        GethLikeTxTrace traces = Execute(
            new GethLikeTxMemoryTracer(Build.A.Transaction.TestObject, GethTraceOptions.Default with { EnableMemory = true }),
            bytecode,
            MainnetSpecProvider.CancunActivation)
            .BuildResult();

        UInt256 result = traces.Entries.Last().GetMemoryWord(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Entries[^2].GasCost, Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow * (ulong)(SLICE_SIZE + 31) / 32), "gas");
            Assert.That(result, Is.EqualTo(new UInt256(Bytes.FromHexString("0x0102030405060708080000000000000000000000000000000000000000000000"), isBigEndian: true)), "memory state");
        }
    }

    /// <summary>
    /// TStore gas cost check
    /// </summary>
    [Test]
    public void Tstore()
    {
        byte[] code = Prepare.EvmCode
            .PushData(96)
            .PushData(64)
            .Op(Instruction.TSTORE)
            .Done;

        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp), 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.TStore), "gas");
    }

    [Test]
    public void Revert()
    {
        // See: https://eips.ethereum.org/EIPS/eip-140

        byte[] code = Bytes.FromHexString("0x6c726576657274656420646174616000557f726576657274206d657373616765000000000000000000000000000000000000600052600e6000fd");
        TestAllTracerWithOutput receipt = Execute(blockNumber: MainnetSpecProvider.ByzantiumBlockNumber, 100_000, code);

        // Raw revert bytes without an Error(string) selector — GetErrorMessage returns null,
        // so Error falls back to the Revert sentinel.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.Error, Is.EqualTo(Nethermind.Evm.TransactionSubstate.Revert));
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 20024));
        }
    }

    private static readonly TestCaseData[] TopLevelOutputCases =
    [
        new TestCaseData((byte[])[0xde, 0xad, 0xbe, 0xef]).SetName("Sub_word_output"),
        new TestCaseData(Bytes.FromHexString("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff0123456789abcdef")).SetName("Multi_word_output"),
    ];

    [TestCaseSource(nameof(TopLevelOutputCases))]
    public void Return_output_reaches_receipt_and_action_tracers_verbatim(byte[] data)
    {
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, data)
            .Return(data.Length, 0)
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.EqualTo(data));
            Assert.That(receipt.ActionOutputs, Is.EqualTo(new[] { data }));
        }
    }

    [TestCaseSource(nameof(TopLevelOutputCases))]
    public void Revert_output_reaches_receipt_and_action_tracers_verbatim(byte[] data)
    {
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, data)
            .Revert(data.Length, 0)
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(receipt.ReturnValue, Is.EqualTo(data));
            Assert.That(receipt.ActionOutputs, Is.Empty);
            Assert.That(receipt.ActionRevertOutputs, Is.EqualTo(new[] { data }));
        }
    }

    [Test]
    public void Empty_return_yields_empty_receipt_and_action_output()
    {
        TestAllTracerWithOutput receipt = Execute(Prepare.EvmCode.Return(0, 0).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.Empty);
            Assert.That(receipt.ActionOutputs, Is.EqualTo(new[] { Array.Empty<byte>() }));
        }
    }

    // Top-level call straight to a precompile exercises the precompile output path, where the backing array may be
    // a whole array that is forwarded without copying.
    [TestCaseSource(nameof(TopLevelOutputCases))]
    public void Top_level_precompile_output_reaches_receipt_and_action_tracers_verbatim(byte[] input)
    {
        EthereumEcdsa ecdsa = new(SpecProvider.ChainId);
        Transaction tx = Build.A.Transaction
            .WithTo(IdentityPrecompile.Address)
            .WithData(input)
            .WithGasLimit(100_000)
            .SignedAndResolved(ecdsa, SenderKey)
            .TestObject;

        TestAllTracerWithOutput receipt = Execute(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.EqualTo(input));
            Assert.That(receipt.ActionOutputs, Is.EqualTo(new[] { input }));
        }
    }

    private static IEnumerable<TestCaseData> TopLevelEndingsAfterNestedCall()
    {
        byte[] topLevelOutput = Bytes.FromHexString("0xaabbccddeeff");
        yield return new TestCaseData(
                Prepare.EvmCode.StoreDataInMemory(64, topLevelOutput).Return(topLevelOutput.Length, 64).Done, topLevelOutput)
            .SetArgDisplayNames("Return_own_output");
        yield return new TestCaseData(Prepare.EvmCode.Op(Instruction.STOP).Done, Array.Empty<byte>())
            .SetArgDisplayNames("Stop");
    }

    [TestCaseSource(nameof(TopLevelEndingsAfterNestedCall))]
    public void Nested_precompile_and_top_level_outputs_remain_frame_local(byte[] topLevelEnding, byte[] topLevelOutput)
    {
        byte[] nestedOutput = Bytes.FromHexString("0x1122334455667788");
        byte[] code = Prepare.EvmCode
            .CallWithInput(IdentityPrecompile.Address, 50_000, nestedOutput)
            .Data(topLevelEnding)
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.EqualTo(topLevelOutput));
            Assert.That(receipt.ActionOutputs, Is.EqualTo(new[] { nestedOutput, topLevelOutput }));
        }
    }

    [TestCaseSource(nameof(TopLevelEndingsAfterNestedCall))]
    public void Reverted_child_output_is_not_reported_as_top_level_output(byte[] topLevelEnding, byte[] topLevelOutput)
    {
        byte[] revertData = Bytes.FromHexString("0x1122334455667788");
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Prepare.EvmCode.StoreDataInMemory(0, revertData).Revert(revertData.Length, 0).Done, Spec);
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50_000)
            .Data(topLevelEnding)
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.EqualTo(topLevelOutput));
            Assert.That(receipt.ActionRevertOutputs, Is.EqualTo(new[] { revertData }));
            Assert.That(receipt.ActionOutputs, Is.EqualTo(new[] { topLevelOutput }));
        }
    }

    // A create frame ends through the deployment overload, so its action output is the deployed code, not RETURN data.
    [Test]
    public void Create_frame_reports_deployed_code_as_action_output()
    {
        byte[] deployedCode = Bytes.FromHexString("0x600060005500");
        byte[] code = Prepare.EvmCode
            .Create(Prepare.EvmCode.ForInitOf(deployedCode).Done, 0)
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ActionOutputs, Is.EqualTo(new[] { deployedCode, Array.Empty<byte>() }));
        }
    }

    [Test]
    public void Exceptional_halt_does_not_report_action_output()
    {
        TestAllTracerWithOutput receipt = Execute((byte)Instruction.ADD);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(receipt.ReturnValue, Is.Empty);
            Assert.That(receipt.ActionOutputs, Is.Empty);
            Assert.That(receipt.ActionRevertOutputs, Is.Empty);
            Assert.That(receipt.ReportedActionErrors, Is.EqualTo([EvmExceptionType.StackUnderflow]));
        }
    }
}

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
