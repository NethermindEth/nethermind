// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeTxFileTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Should_have_expected_file_tracing_flags()
    {
        GethLikeTxFileTracer tracer = new(static e => { }, GethTraceOptions.Default, destroyRefund: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.IsTracingMemory, Is.True);
            Assert.That(tracer.IsTracingOpLevelStorage, Is.False);
            Assert.That(tracer.IsTracingRefunds, Is.True);
            Assert.That(tracer.IsTracingActions, Is.True);
        }
    }

    [Test]
    public void Should_return_gas_and_return_value_as_expected()
    {
        GethLikeTxTrace trace = ExecuteAndTraceToFile(static e => { }, GetBytecode(), GethTraceOptions.Default);

        Assert.That(trace.Gas, Is.EqualTo(24));
        Assert.That(trace.ReturnValue.Length, Is.EqualTo(0));
    }

    [Test]
    public void Should_include_final_opcode_cost_in_gas_used()
    {
        byte[] code = Prepare.EvmCode
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTraceToFile(static e => { }, code, GethTraceOptions.Default);

        Assert.That(trace.Gas, Is.EqualTo(9));
    }

    [Test]
    public void Should_report_gas_used_for_top_level_precompile_without_opcode_entries()
    {
        byte[] input = [0x01];
        Transaction transaction = Build.A.Transaction
            .WithTo(IdentityPrecompile.Address)
            .WithData(input)
            .WithGasLimit(100_000)
            .WithGasPrice(1)
            .WithValue(0)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        (Block block, _) = PrepareTx(Activation, 100_000, transaction: transaction);
        List<GethTxFileTraceEntry> entries = [];

        GethLikeTxTrace trace = ExecuteAndTraceToFile(block, transaction, entries);
        ulong expectedGas = IdentityPrecompile.Instance.BaseGasCost(Spec) + IdentityPrecompile.Instance.DataGasCost(input, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Is.Empty);
            Assert.That(trace.Gas, Is.EqualTo(expectedGas));
        }
    }

    [Test]
    public void Should_include_code_deposit_in_creation_gas_used()
    {
        byte[] initCode = Prepare.EvmCode
            .PushData((byte)0)
            .PushData((byte)0)
            .Op(Instruction.MSTORE8)
            .PushData((byte)1)
            .PushData((byte)0)
            .Op(Instruction.RETURN)
            .Done;
        (Block block, Transaction transaction) = PrepareInitTx(Activation, 100_000, initCode);
        List<GethTxFileTraceEntry> entries = [];

        GethLikeTxTrace trace = ExecuteAndTraceToFile(block, transaction, entries);
        ulong opcodeGas = entries.Aggregate(0UL, static (gas, entry) => gas + entry.GasCost);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.ReturnValue, Has.Length.EqualTo(1));
            Assert.That(trace.Gas, Is.EqualTo(opcodeGas + GasCostOf.CodeDeposit));
        }
    }

    [Test]
    public void Should_consume_top_level_gas_on_action_error()
    {
        GethLikeTxFileTracer tracer = new(static e => { }, GethTraceOptions.Default, destroyRefund: 0);

        tracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        tracer.ReportActionError(EvmExceptionType.OutOfGas);

        Assert.That(tracer.BuildResult().Gas, Is.EqualTo(100));
    }

    [Test]
    public void Should_report_gas_used_for_create_collision_without_action()
    {
        const ulong gasLimit = 100_000;
        byte[] initCode = [0x00];
        (Block block, Transaction transaction) = PrepareInitTx(Activation, gasLimit, initCode);
        Address deploymentAddress = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        TestState.CreateAccount(deploymentAddress, UInt256.Zero, nonce: 1);
        List<GethTxFileTraceEntry> entries = [];
        ulong standardIntrinsicGas = IntrinsicGasCalculator.Calculate(transaction, Spec).Standard;

        GethLikeTxTrace trace = ExecuteAndTraceToFile(block, transaction, entries);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Is.Empty);
            Assert.That(trace.Failed, Is.True);
            Assert.That(trace.Gas, Is.EqualTo(gasLimit - standardIntrinsicGas));
        }
    }

    [Test]
    public void Should_report_refund_received_before_first_operation()
    {
        const long initialRefund = 12_500;
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxFileTracer tracer = new(e => entries.Add(CloneTraceEntry(e)), GethTraceOptions.Default, destroyRefund: 0);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);

        tracer.ReportRefund(initialRefund);
        tracer.StartOperation(0, Instruction.STOP, 100, in environment);
        tracer.BuildResult();

        Assert.That(entries.Single().Refund, Is.EqualTo(initialRefund));
    }

    [Test]
    public void Should_restore_refund_when_action_reverts()
    {
        const long initialRefund = 12_500;
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxFileTracer tracer = new(e => entries.Add(CloneTraceEntry(e)), GethTraceOptions.Default, destroyRefund: 0);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);

        tracer.ReportRefund(initialRefund);
        tracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        tracer.ReportAction(50, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        tracer.ReportRefund(10_000);
        tracer.ReportActionRevert(25, default);
        tracer.StartOperation(0, Instruction.STOP, 50, in environment);
        tracer.ReportActionEnd(50, default);
        tracer.BuildResult();

        Assert.That(entries.Single().Refund, Is.EqualTo(initialRefund));
    }

    [Test]
    public void Should_report_and_deduplicate_legacy_self_destruct_refund_after_child_returns()
    {
        const long destroyRefund = (long)RefundOf.DestroyBeforeEip3529;
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxFileTracer tracer = new(e => entries.Add(CloneTraceEntry(e)), GethTraceOptions.Default, destroyRefund);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: UInt256.Zero, inputData: ReadOnlyMemory<byte>.Empty);

        tracer.ReportAction(100, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.TRANSACTION);
        tracer.ReportAction(50, UInt256.Zero, Address.Zero, Address.Zero, default, ExecutionType.CALL);
        tracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
        tracer.ReportSelfDestruct(TestItem.AddressA, UInt256.Zero, Address.Zero);
        tracer.ReportActionEnd(25, default);
        tracer.StartOperation(0, Instruction.STOP, 50, in environment);
        tracer.ReportActionEnd(50, default);
        tracer.BuildResult();

        Assert.That(entries.Single().Refund, Is.EqualTo(destroyRefund));
    }

    [Test]
    public void Should_return_memory_size_with_memory_disabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].MemorySize, Is.EqualTo(0));
            Assert.That(entries[1].MemorySize, Is.EqualTo(0));
            Assert.That(entries[2].MemorySize, Is.EqualTo(0));
            Assert.That(entries[3].MemorySize, Is.EqualTo(32));
            Assert.That(entries[4].MemorySize, Is.EqualTo(32));
            Assert.That(entries[5].MemorySize, Is.EqualTo(32));
            Assert.That(entries[6].MemorySize, Is.EqualTo(64));

            Assert.That(entries.All(e => e.Memory is null), Is.True);
        }
    }

    [Test]
    public void Should_return_memory_when_enabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default with { EnableMemory = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].MemoryWordCount(), Is.EqualTo(0));
            Assert.That(entries[1].MemoryWordCount(), Is.EqualTo(0));
            Assert.That(entries[2].MemoryWordCount(), Is.EqualTo(0));
            Assert.That(entries[3].MemoryWordCount(), Is.EqualTo(1));
            Assert.That(entries[4].MemoryWordCount(), Is.EqualTo(1));
            Assert.That(entries[5].MemoryWordCount(), Is.EqualTo(1));
            Assert.That(entries[6].MemoryWordCount(), Is.EqualTo(2));
        }
    }

    [Test]
    public void Should_return_stack_when_enabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries[0].StackWordCount(), Is.EqualTo(0));
            Assert.That(entries[1].StackWordCount(), Is.EqualTo(1));
            Assert.That(entries[2].StackWordCount(), Is.EqualTo(2));
            Assert.That(entries[3].StackWordCount(), Is.EqualTo(0));
            Assert.That(entries[4].StackWordCount(), Is.EqualTo(1));
            Assert.That(entries[5].StackWordCount(), Is.EqualTo(2));
            Assert.That(entries[6].StackWordCount(), Is.EqualTo(0));
        }
    }

    [Test]
    public void Should_not_return_stack_when_disabled()
    {
        List<GethTxFileTraceEntry> entries = [];
        GethLikeTxTrace trace = ExecuteAndTraceToFile(e => entries.Add(CloneTraceEntry(e)), GetBytecode(), GethTraceOptions.Default with { DisableStack = true });

        Assert.That(entries.All(e => e.Stack is null), Is.True);
    }

    /// <summary>
    /// Clones the specified trace entry as <see cref="GethLikeTxFileTracer"/>
    /// reuses the same instance for all entries.
    /// </summary>
    private static GethTxFileTraceEntry CloneTraceEntry(GethTxFileTraceEntry entry) =>
        JsonSerializer.Deserialize<GethTxFileTraceEntry>(JsonSerializer.Serialize(entry));

    private GethLikeTxTrace ExecuteAndTraceToFile(Block block, Transaction transaction, List<GethTxFileTraceEntry> entries)
    {
        GethLikeTxFileTracer tracer = new(
            entry => entries.Add(CloneTraceEntry(entry)),
            GethTraceOptions.Default,
            (long)SpecProvider.GetSpec(block.Header).GasCosts.DestroyRefund,
            IntrinsicGasCalculator.Calculate(transaction, SpecProvider.GetSpec(block.Header), block.Header.GasLimit).Standard);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer.BuildResult();
    }

    private static byte[] GetBytecode() =>
        Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(32)
            .Op(Instruction.MSTORE)
            .Op(Instruction.STOP)
            .Done;
}
