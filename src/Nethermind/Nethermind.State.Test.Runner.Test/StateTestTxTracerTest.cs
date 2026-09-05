// SPDX-FileCopyrightText: 2022-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Test;
using Nethermind.Evm.Test.Tracing;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

public class StateTestTxTracerTest : GethLikeTracerTestsBase
{
    private readonly ISpecProvider _specProvider = new TestSpecProvider(Amsterdam.Instance);

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider => _specProvider;

    private StateTestTxTracer tracer;

    [SetUp]
    public void StateTestTxTracerSetUp() => tracer = new StateTestTxTracer(standardIntrinsicGas: 0, destroyRefund: 0);

    [TearDown]
    public void StateTestTxTracerTearDown() => tracer.Dispose();

    [Test]
    public void Does_not_throw_on_call()
    {
        byte[] code = Prepare.EvmCode
            .CallWithValue(TestItem.AddressC, 50000, 1000000.Ether)
            .Done;

        Assert.DoesNotThrow(() => Execute(tracer, code));
    }

    [Test]
    public void Does_not_throw_on_self_destruct()
    {
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressC)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        Assert.DoesNotThrow(() => Execute(tracer, code));
    }

    [Test]
    public void Reports_pre_settlement_top_level_action_gas()
    {
        tracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        tracer.ReportAction(60, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        tracer.ReportActionEnd(40, default);
        tracer.ReportActionEnd(70, default);

        GasConsumed settledGas = new(SpentGas: 200, OperationGas: 180, BlockStateGas: 80, GasRefund: 20);
        tracer.MarkAsSuccess(Address.Zero, in settledGas, [], []);

        // EIP-3155 follows the frame delta; receipt settlement and refunds do not change it.
        Assert.That(tracer.BuildResult().Result.GasUsed, Is.EqualTo(30));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Reports_zero_for_stop_call_with_calldata_floor(bool amsterdam)
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;
        Hash256 stopCallPostHash = new(amsterdam
            ? "0x53a1c1658d4a73b0b3696812f30e2b247e3bd3fd289c9436979f14e3c23810be"
            : "0x9efbc3518d97c09664295c8fcf82ddc73ea94a770bfbd59bb98f4c2c6c8219a4");
        GeneralStateTest test = CreateStateTest(
            amsterdam ? Amsterdam.Instance : Osaka.Instance,
            code,
            input: [0],
            gasLimit: 5_000_000,
            postHash: stopCallPostHash);

        AssertTraceGas(test, 0);
    }

    // PUSH1 + PUSH0 + SSTORE consumes 12,105 execution gas; constrained gas adds a 97,920 state-gas spill.
    // Both cases settle to the same 131,025 receipt gas and post-state root; only the EIP-3155 frame delta differs,
    // which is why it cannot be derived from the receipt.
    [TestCase(20_000_000UL, 12_105UL)]
    [TestCase(200_000UL, 12_105UL + 97_920UL)]
    public void Reports_state_gas_only_when_it_spills_into_execution(ulong gasLimit, ulong expectedGasUsed)
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        Hash256 stateGasSpillPostHash = new("0xacd480565b8de9ee8f4c137da3d5d6ca7fdd70808d65abb33609a907c3339e41");
        GeneralStateTest test = CreateStateTest(
            Amsterdam.Instance,
            code,
            input: [],
            gasLimit: gasLimit,
            postHash: stateGasSpillPostHash);

        AssertTraceGas(test, expectedGasUsed);
    }

    [Test]
    public void Reports_refunded_state_gas_on_revert()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .PushData((byte)0)
            .PushData((byte)0)
            .Op(Instruction.REVERT)
            .Done;
        Hash256 revertedStateGasPostHash = new("0x0cdcee5f7be607fbf231de46ab3788ca0204fb402880036d85c2a1f7cf85cc84");
        GeneralStateTest test = CreateStateTest(
            Amsterdam.Instance,
            code,
            input: [],
            gasLimit: 200_000,
            postHash: revertedStateGasPostHash);

        AssertTraceGas(test, 12_111);
    }

    [Test]
    public void Does_not_charge_code_deposit_for_revert_data()
    {
        byte[] initCode = Prepare.EvmCode
            .PushData(1)
            .PushData((byte)0)
            .Op(Instruction.MSTORE)
            .PushData((byte)32)
            .PushData((byte)0)
            .Op(Instruction.REVERT)
            .Done;
        Hash256 createRevertPostHash = new("0x157a9a369824ebd3e66e2658150f54254bcaf26e23e11163b6a1f39cd1e8b046");
        GeneralStateTest test = CreateStateTest(
            Osaka.Instance,
            code: [],
            input: initCode,
            gasLimit: 100_000,
            postHash: createRevertPostHash,
            contractCreation: true);

        AssertTraceGas(test, 18);
    }

    [Test]
    public void Falls_back_to_receipt_gas_for_create_collision()
    {
        byte[] initCode = Prepare.EvmCode
            .PushData((byte)0)
            .PushData((byte)0)
            .Op(Instruction.RETURN)
            .Done;
        Hash256 createCollisionPostHash = new("0x23f9dffa595df45c4c8bed92dfb495e14ebbc4ca6bcd3d1e63983da7ef1c4306");
        GeneralStateTest test = CreateStateTest(
            Osaka.Instance,
            code: [],
            input: initCode,
            gasLimit: 100_000,
            postHash: createCollisionPostHash,
            contractCreation: true,
            collision: true);
        using StateTestTxTracer collisionTracer = new(
            IntrinsicGasCalculator.Calculate(test.Transaction, test.Fork).Standard,
            (long)test.Fork.GasCosts.DestroyRefund);

        // 100,000 gas limit minus the 53,058 creation intrinsic gas.
        AssertTraceGas(test, 46_942, collisionTracer);
    }

    [Test]
    public void Receipt_gas_fallback_saturates_below_intrinsic_gas()
    {
        using StateTestTxTracer fallbackTracer = new(standardIntrinsicGas: 100, destroyRefund: 0);
        GasConsumed settledGas = new(SpentGas: 90, OperationGas: 90);

        fallbackTracer.MarkAsSuccess(Address.Zero, in settledGas, [], []);

        Assert.That(fallbackTracer.BuildResult().Result.GasUsed, Is.Zero);
    }

    [TestCase(Instruction.DUPN, 0x80, 18)]
    [TestCase(Instruction.SWAPN, 0x80, 18)]
    [TestCase(Instruction.EXCHANGE, 0x8e, 3)]
    public void Eip8024_immediate_is_not_traced_as_an_instruction(Instruction operation, byte immediate, int stackDepth)
    {
        byte[] code = Prepare.EvmCode
            .For(stackDepth, static (prepare, _) => prepare.PushData(0))
            .Op(operation)
            .Data(immediate)
            .Op(Instruction.STOP)
            .Done;

        StateTestTxTrace trace = Execute(tracer, code).BuildResult();
        int operationPc = stackDepth * 2;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Entries.Count, Is.EqualTo(stackDepth + 2));
            Assert.That(trace.Entries[stackDepth].Pc, Is.EqualTo(operationPc));
            Assert.That(trace.Entries[stackDepth].Operation, Is.EqualTo((byte)operation));
            Assert.That(trace.Entries[stackDepth + 1].Pc, Is.EqualTo(operationPc + 2));
            Assert.That(trace.Entries[stackDepth + 1].Operation, Is.EqualTo((byte)Instruction.STOP));
        }
    }

    [Test]
    public void Traces_implicit_stop_after_memory_expanding_final_operation()
    {
        byte[] code = Prepare.EvmCode
            .PushData(UInt256.MaxValue)
            .PushData(UInt256.MaxValue)
            .PushData(UInt256.MaxValue)
            .PushData(32)
            .PushData(255)
            .Op(Instruction.LOG3)
            .Done;

        StateTestTxTrace trace = Execute(tracer, code).BuildResult();
        StateTestTxTraceEntry log = trace.Entries[^2];
        StateTestTxTraceEntry stop = trace.Entries[^1];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.Operation, Is.EqualTo((byte)Instruction.LOG3));
            Assert.That(log.Pc, Is.EqualTo(103));
            Assert.That(log.GasCost, Is.EqualTo(0x6f7));
            Assert.That(log.MemSize, Is.Zero);
            Assert.That(stop.Operation, Is.EqualTo((byte)Instruction.STOP));
            Assert.That(stop.Pc, Is.EqualTo(104));
            Assert.That(stop.GasCost, Is.Zero);
            Assert.That(stop.MemSize, Is.EqualTo(288));
        }
    }

    [Test]
    public void Trace_entries_include_opcode_name_and_cumulative_refund()
    {
        StateTestTxTrace trace = Execute(tracer, ClearSstoreCode()).BuildResult();
        StateTestTxTraceEntry sstore = FindEntry(trace, Instruction.SSTORE);
        StateTestTxTraceEntry stop = FindEntry(trace, Instruction.STOP);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sstore.OperationName, Is.EqualTo(nameof(Instruction.SSTORE)));
            Assert.That(sstore.Refund, Is.EqualTo(0));
            Assert.That(stop.OperationName, Is.EqualTo(nameof(Instruction.STOP)));
            Assert.That(stop.Refund, Is.EqualTo(Spec.GasCosts.SClearRefund));
        }
    }

    [TestCase(Instruction.KECCAK256, "KECCAK256")]
    [TestCase(Instruction.PREVRANDAO, "DIFFICULTY")]
    [TestCase((Instruction)0xd0, "DATALOAD")]
    [TestCase((Instruction)0xd1, "DATALOADN")]
    [TestCase((Instruction)0xd2, "DATASIZE")]
    [TestCase((Instruction)0xd3, "DATACOPY")]
    [TestCase((Instruction)0xe0, "RJUMP")]
    [TestCase((Instruction)0xe1, "RJUMPI")]
    [TestCase((Instruction)0xe2, "RJUMPV")]
    [TestCase((Instruction)0xe3, "CALLF")]
    [TestCase((Instruction)0xe4, "RETF")]
    [TestCase((Instruction)0xe5, "JUMPF")]
    [TestCase((Instruction)0xec, "EOFCREATE")]
    [TestCase((Instruction)0xee, "RETURNCONTRACT")]
    [TestCase((Instruction)0xf7, "RETURNDATALOAD")]
    [TestCase((Instruction)0xf8, "EXTCALL")]
    [TestCase((Instruction)0xf9, "EXTDELEGATECALL")]
    [TestCase((Instruction)0xfb, "EXTSTATICCALL")]
    [TestCase((Instruction)0x0f, "opcode 0xf not defined")]
    public void Trace_entry_uses_geth_opcode_name(Instruction operation, string expectedName)
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);

        tracer.StartOperation(0, operation, 100, in environment);

        Assert.That(tracer.BuildResult().Entries[0].OperationName, Is.EqualTo(expectedName));
    }

    [Test]
    public void Refund_decreases_when_a_storage_clear_is_reversed()
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), [1]);
        TestState.Commit(Spec);
        byte[] code = Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .PersistData("0x0", "01")
            .Op(Instruction.STOP)
            .Done;

        StateTestTxTrace trace = Execute(tracer, code).BuildResult();
        StateTestTxTraceEntry firstSstore = FindEntry(trace, Instruction.SSTORE);
        StateTestTxTraceEntry secondSstore = FindEntry(trace, Instruction.SSTORE, fromEnd: true);
        StateTestTxTraceEntry stop = FindEntry(trace, Instruction.STOP);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstSstore.Refund, Is.Zero);
            Assert.That(secondSstore.Refund, Is.EqualTo(Spec.GasCosts.SClearRefund));
            Assert.That(stop.Refund, Is.EqualTo(Eip8038Constants.StorageWrite));
        }
    }

    [Test]
    public void Refund_is_rolled_back_when_frame_reverts()
    {
        StateTestTxTrace trace = Execute(tracer, ChildClearThenRevertCode()).BuildResult();
        StateTestTxTraceEntry revert = FindEntry(trace, Instruction.REVERT);
        StateTestTxTraceEntry topLevelStop = FindEntry(trace, Instruction.STOP, depth: 1, fromEnd: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(revert.Refund, Is.EqualTo(Spec.GasCosts.SClearRefund));
            Assert.That(topLevelStop.Refund, Is.EqualTo(0));
        }
    }

    [Test]
    public void Exceptional_halt_rolls_back_refund_and_consumes_top_level_gas()
    {
        tracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        tracer.ReportAction(50, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        tracer.ReportRefund((long)Spec.GasCosts.SClearRefund);
        tracer.ReportActionError(EvmExceptionType.OutOfGas);

        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);
        tracer.StartOperation(0, Instruction.STOP, 25, in environment);
        tracer.ReportActionError(EvmExceptionType.OutOfGas);
        StateTestTxTrace trace = tracer.BuildResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Entries[0].Refund, Is.Zero);
            Assert.That(trace.Result.GasUsed, Is.EqualTo(100));
        }
    }

    [Test]
    public void Parent_refund_survives_when_child_frame_reverts()
    {
        StateTestTxTrace trace = Execute(tracer, RefundThenChildRevertCode()).BuildResult();
        StateTestTxTraceEntry topLevelStop = FindEntry(trace, Instruction.STOP, depth: 1, fromEnd: true);

        Assert.That(topLevelStop.Refund, Is.EqualTo(Spec.GasCosts.SClearRefund));
    }

    [Test]
    public void Legacy_self_destruct_refund_is_deduplicated_and_journaled()
    {
        long destroyRefund = (long)Frontier.Instance.GasCosts.DestroyRefund;
        using StateTestTxTracer legacyTracer = new(standardIntrinsicGas: 0, destroyRefund: destroyRefund);

        legacyTracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        legacyTracer.ReportAction(50, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        legacyTracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
        legacyTracer.ReportActionRevert(25, default);

        legacyTracer.ReportAction(50, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        legacyTracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
        legacyTracer.ReportActionEnd(25, default);

        legacyTracer.ReportAction(50, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        legacyTracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
        legacyTracer.ReportActionEnd(25, default);

        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);
        legacyTracer.StartOperation(0, Instruction.STOP, 25, in environment);

        Assert.That(legacyTracer.BuildResult().Entries[0].Refund, Is.EqualTo(destroyRefund));
    }

    [Test, NonParallelizable]
    public void Jsonl_trace_includes_refund_and_opcode_name()
    {
        GeneralStateTest test = CreateStateTest(
            Osaka.Instance,
            code: [0],
            input: [0],
            gasLimit: 5_000_000,
            postHash: new Hash256("0x9efbc3518d97c09664295c8fcf82ddc73ea94a770bfbd59bb98f4c2c6c8219a4"));
        (EthereumTestResult result, string trace) = RunAndCaptureJsonl(test, traceMemory: false);

        using StringReader lines = new(trace);
        using JsonDocument operation = JsonDocument.Parse(lines.ReadLine()!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.True, result.Error);
            Assert.That(operation.RootElement.GetProperty("refund").GetInt64(), Is.Zero);
            Assert.That(operation.RootElement.GetProperty("opName").GetString(), Is.EqualTo(nameof(Instruction.STOP)));
            Assert.That(operation.RootElement.TryGetProperty("memory", out _), Is.False);
        }
    }

    [Test, NonParallelizable]
    public void Jsonl_trace_includes_memory_when_enabled()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .PushData(1)
            .PushData((byte)0)
            .Op(Instruction.MSTORE)
            .PushData((byte)32)
            .PushData((byte)0)
            .Op(Instruction.REVERT)
            .Done;
        GeneralStateTest test = CreateStateTest(
            Amsterdam.Instance,
            code,
            input: [],
            gasLimit: 200_000,
            postHash: new Hash256("0x57efb01840362d55bfc0f9b920788f3446023a16688d71bf6b80b144259e6b2e"));
        (EthereumTestResult result, string trace) = RunAndCaptureJsonl(test, traceMemory: true);

        string revertLine = trace
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(static line => line.Contains("\"opName\":\"REVERT\"", StringComparison.Ordinal));
        using JsonDocument operation = JsonDocument.Parse(revertLine);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.True, result.Error);
            Assert.That(operation.RootElement.GetProperty("memory").GetString(), Is.EqualTo("0x" + new string('0', 63) + "1"));
        }
    }

    private static (EthereumTestResult Result, string Trace) RunAndCaptureJsonl(GeneralStateTest test, bool traceMemory)
    {
        TextWriter originalError = Console.Error;
        using StringWriter error = new();

        Console.SetError(error);
        try
        {
            StateTestsRunner runner = new(WhenTrace.Always, traceMemory, traceStack: true, chainId: 1);
            return (runner.RunSingleTest(test), error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static StateTestTxTraceEntry FindEntry(StateTestTxTrace trace, Instruction operation, int? depth = null, bool fromEnd = false)
    {
        int index = fromEnd ? trace.Entries.Count - 1 : 0;
        int end = fromEnd ? -1 : trace.Entries.Count;
        int step = fromEnd ? -1 : 1;

        for (; index != end; index += step)
        {
            StateTestTxTraceEntry entry = trace.Entries[index];
            if (entry.Operation == (byte)operation && (depth is null || entry.Depth == depth))
                return entry;
        }

        throw new AssertionException($"No {operation} trace entry was found.");
    }

    private void AssertTraceGas(GeneralStateTest test, ulong expectedGasUsed)
        => AssertTraceGas(test, expectedGasUsed, tracer);

    private static void AssertTraceGas(GeneralStateTest test, ulong expectedGasUsed, StateTestTxTracer txTracer)
    {
        EthereumTestResult result = new StateTestExecutor().Execute(test, txTracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.True, result.Error);
            Assert.That(txTracer.BuildResult().Result.GasUsed, Is.EqualTo(expectedGasUsed));
        }
    }

    private static GeneralStateTest CreateStateTest(
        IReleaseSpec fork,
        byte[] code,
        byte[] input,
        ulong gasLimit,
        Hash256 postHash,
        bool contractCreation = false,
        bool collision = false)
    {
        Address recipient = TestItem.AddressB;
        TransactionBuilder<Transaction> transactionBuilder = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithChainId(1)
            .WithAccessList(AccessList.Empty)
            .WithGasLimit(gasLimit)
            .WithGasPrice(7)
            .WithNonce(0)
            .WithValue(0);

        if (contractCreation)
            transactionBuilder.WithCode(input);
        else
            transactionBuilder.WithData(input).To(recipient);

        Transaction transaction = transactionBuilder
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        GeneralStateTest test = new()
        {
            Name = nameof(StateTestTxTracerTest),
            Category = "state",
            Fork = fork,
            CurrentCoinbase = TestItem.AddressC,
            CurrentDifficulty = UInt256.Zero,
            CurrentGasLimit = 100_000_000,
            CurrentNumber = 1,
            CurrentTimestamp = 1_000,
            CurrentBaseFee = 7,
            CurrentRandom = Hash256.Zero,
            CurrentExcessBlobGas = 0,
            PreviousHash = Hash256.Zero,
            Pre = new()
            {
                [transaction.SenderAddress!] = new AccountState { Balance = 1_000_000_000 },
            },
            PostHash = postHash,
            Transaction = transaction,
        };

        if (!contractCreation)
        {
            test.Pre[recipient] = new AccountState { Code = code, Nonce = 1 };
        }
        else if (collision)
        {
            Address deploymentAddress = ContractAddress.From(transaction.SenderAddress, transaction.Nonce);
            test.Pre[deploymentAddress] = new AccountState { Code = [0], Nonce = 1 };
        }

        return test;
    }

    private sealed class StateTestExecutor : GeneralStateTestBase
    {
        public EthereumTestResult Execute(GeneralStateTest test, ITxTracer txTracer) => RunTest(test, txTracer);
    }
}
