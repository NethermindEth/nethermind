// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Test.Tracing;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

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
    ];

    private static readonly TestCaseData[] JumpFailureCases =
    [
        new TestCaseData("56", 100000UL, 1).SetName("Jump_stack_underflow"),
        new TestCaseData("600056", 100000UL, 2).SetName("Jump_invalid_destination"),
        new TestCaseData("6003565b", 21010UL, 2).SetName("Jump_charge_out_of_gas"),
        new TestCaseData("6003565b", 21011UL, 3).SetName("JumpDest_charge_out_of_gas_after_Jump"),
        new TestCaseData("60016005575b", 21015UL, 3).SetName("JumpI_charge_out_of_gas"),
        new TestCaseData("60016005575b", 21016UL, 4).SetName("JumpDest_charge_out_of_gas_after_JumpI"),
    ];

    private sealed class NoInstructionTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }

    private sealed class CountingCancellationTracer(int cancelAtPoll = int.MaxValue) : TestAllTracerWithOutput, ITxTracer
    {
        public int PollCount { get; private set; }

        public override bool IsTracingInstructions => false;

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
    public void Warm_up_opcode_handlers_does_not_throw() =>
        Assert.That(
            () => EthereumVirtualMachine.WarmUpEvmInstructions(TestState, CodeInfoRepository),
            Throws.Nothing);

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

    [TestCase(1023, true, 1)]
    [TestCase(1024, false, 1)]
    [TestCase(1024, true, 2)]
    [TestCase(2048, true, 3)]
    public void Cancellation_is_polled_before_the_first_opcode_and_each_complete_1024_opcode_batch(
        int continuingOpcodeCount,
        bool appendStop,
        int expectedPollCount)
    {
        byte[] code = new byte[continuingOpcodeCount + (appendStop ? 1 : 0)];
        Array.Fill(code, (byte)Instruction.JUMPDEST);
        if (appendStop)
            code[^1] = (byte)Instruction.STOP;
        CountingCancellationTracer tracer = new();

        Execute(tracer, code);

        Assert.That(tracer.PollCount, Is.EqualTo(expectedPollCount));
    }

    [Test]
    public void Cancellation_at_a_1024_opcode_boundary_stops_before_the_next_opcode()
    {
        byte[] code = new byte[1025];
        Array.Fill(code, (byte)Instruction.JUMPDEST);
        code[^1] = (byte)Instruction.STOP;
        CountingCancellationTracer tracer = new(cancelAtPoll: 2);

        Assert.Throws<OperationCanceledException>(() => Execute(tracer, code));
        Assert.That(tracer.PollCount, Is.EqualTo(2));
    }

    private static IEnumerable<TestCaseData> FixedCostOpcodeGasCases()
    {
        (Instruction Opcode, int Depth, ulong Cost)[] operations =
        [
            (Instruction.ADD, 2, 3), (Instruction.MUL, 2, 5), (Instruction.SUB, 2, 3),
            (Instruction.DIV, 2, 5), (Instruction.SDIV, 2, 5), (Instruction.MOD, 2, 5),
            (Instruction.SMOD, 2, 5), (Instruction.LT, 2, 3), (Instruction.GT, 2, 3),
            (Instruction.SLT, 2, 3), (Instruction.SGT, 2, 3), (Instruction.EQ, 2, 3),
            (Instruction.ISZERO, 1, 3), (Instruction.NOT, 1, 3),
            (Instruction.POP, 1, 2),
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
            foreach (int tracerMode in new[] { 0, 1, 2 })
            {
                foreach (bool sufficientStack in new[] { false, true })
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

    private static IEnumerable<TestCaseData> StackGrowthCases()
    {
        for (Instruction opcode = Instruction.DUP1; opcode <= Instruction.DUP16; opcode++)
        {
            foreach (int depth in new[] { 1023, 1024 })
            foreach (bool sufficientGas in new[] { false, true })
            foreach (int tracerMode in new[] { 0, 1, 2 })
                yield return new TestCaseData(opcode, depth, sufficientGas, tracerMode);
        }
    }

    [TestCaseSource(nameof(StackGrowthCases))]
    public void Stack_growth_preserves_limit_and_gas_precedence(Instruction opcode, int depth, bool sufficientGas, int tracerMode)
    {
        byte[] code = new byte[depth * 2 + 1];
        for (int i = 0; i < depth; i++)
        {
            code[i * 2] = (byte)Instruction.PUSH1;
            code[i * 2 + 1] = 1;
        }
        code[depth * 2] = (byte)opcode;
        ulong gasLimit = GasCostOf.Transaction + (ulong)depth * GasCostOf.VeryLow + GasCostOf.VeryLow - (sufficientGas ? 0UL : 1UL);
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);
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

    [TestCaseSource(nameof(JumpCompletionCases))]
    public void Untraced_jump_completion_preserves_semantics(string bytecode, ulong expectedGas, int expectedOpCodeCount)
    {
        TestAllTracerWithOutput receipt = ExecuteUntraced(100000UL, Bytes.FromHexString(bytecode));

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

    private TestAllTracerWithOutput ExecuteUntraced(ulong gasLimit, byte[] code, ulong blockGasLimit = DefaultBlockGasLimit)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code, blockGasLimit: blockGasLimit);
        NoInstructionTracer tracer = new();
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
        Assert.That(trace.Entries.Count, Is.EqualTo(5), "number of entries");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Pow(2, 160).ToBigEndianByteArray()), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo((BigInteger.Pow(2, 256) - 1).ToBigEndianByteArray()), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
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
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
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

    // Regression cover for the returndata copy-elision in the transaction processor: the bytes handed to the
    // receipt tracer must equal the top-level RETURN / REVERT / precompile output, whether the backing array is
    // forwarded directly or copied.
    [TestCaseSource(nameof(TopLevelOutputCases))]
    public void Return_output_reaches_receipt_tracer_verbatim(byte[] data)
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
        }
    }

    [TestCaseSource(nameof(TopLevelOutputCases))]
    public void Revert_output_reaches_receipt_tracer_verbatim(byte[] data)
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
        }
    }

    [Test]
    public void Empty_return_yields_empty_receipt_output()
    {
        TestAllTracerWithOutput receipt = Execute(Prepare.EvmCode.Return(0, 0).Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(receipt.ReturnValue, Is.Empty);
        }
    }

    // Top-level call straight to a precompile exercises the precompile output path, where the backing array may be
    // a whole array that is forwarded without copying.
    [Test]
    public void Top_level_precompile_output_reaches_receipt_tracer_verbatim()
    {
        byte[] input = Bytes.FromHexString("0x00112233445566778899aabbccddeeff");
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
        }
    }
}
