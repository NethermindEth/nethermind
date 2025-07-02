// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.Proofs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture(true)]
[TestFixture(false)]
[Parallelizable(ParallelScope.Self)]
public class ProofTxTracerTests : VirtualMachineTestsBase
{
    private readonly bool _treatSystemAccountDifferently;

    public ProofTxTracerTests(bool treatSystemAccountDifferently)
    {
        _treatSystemAccountDifferently = treatSystemAccountDifferently;
    }

    [Test]
    public void Can_trace_sender_recipient_miner()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Accounts.Count, Is.EqualTo(3), "count");
        Assert.That(trace.Accounts.Contains(Sender), Is.True);
        Assert.That(trace.Accounts.Contains(Recipient), Is.True);
        Assert.That(trace.Accounts.Contains(Miner), Is.True);
    }

    [Test]
    public void Can_trace_sender_recipient_miner_when_miner_and_sender_are_same()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1)
            .Done;

        SenderRecipientAndMiner addresses = new();
        addresses.MinerKey = SenderKey;
        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(addresses, code);
        Assert.That(trace.Accounts.Count, Is.EqualTo(2), "count");
        Assert.That(trace.Accounts.Contains(Sender), Is.True);
    }

    [Test]
    public void Can_trace_sender_recipient_miner_when_miner_and_recipient_are_same()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1)
            .Done;

        SenderRecipientAndMiner addresses = new();
        addresses.MinerKey = addresses.RecipientKey;
        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(addresses, code);
        Assert.That(trace.Accounts.Count, Is.EqualTo(2), "count");
        Assert.That(trace.Accounts.Contains(Sender), Is.True);
    }

    [Test]
    public void Can_trace_touch_only_null_accounts()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1)
            .PushData(TestItem.AddressC.Bytes)
            .Op(Instruction.BALANCE)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Accounts.Count, Is.EqualTo(4), "count");
        Assert.That(trace.Accounts.Contains(TestItem.AddressC), Is.True);
    }

    [Test]
    public void Can_trace_touch_only_preexisting_accounts()
    {
        TestState.CreateAccount(TestItem.AddressC, 100);
        TestState.Commit(Spec);

        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1)
            .PushData(TestItem.AddressC.Bytes)
            .Op(Instruction.BALANCE)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Accounts.Count, Is.EqualTo(4), "count");
        Assert.That(trace.Accounts.Contains(TestItem.AddressC), Is.True);
    }

    [Test]
    public void Can_trace_touch_only_null_miner_accounts()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1)
            .PushData(SenderRecipientAndMiner.Default.Miner.Bytes)
            .Op(Instruction.BALANCE)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Accounts.Count, Is.EqualTo(3), "count");
    }

    [Test]
    public void Can_trace_blockhash()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.BLOCKHASH)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.BlockHashes.Count, Is.EqualTo(1), "count");
    }

    [Test]
    public void Can_trace_multiple_blockhash()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.BLOCKHASH)
            .PushData("0x02")
            .Op(Instruction.BLOCKHASH)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.BlockHashes.Count, Is.EqualTo(2), "count");
    }

    [Test]
    public void Can_trace_result()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x03")
            .PushData("0x00")
            .Op(Instruction.RETURN)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Output.Length, Is.EqualTo(3));
    }

    [Test]
    public void Can_trace_storage_read()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.SLOAD)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);

        Assert.That(trace.Storages.Count, Is.EqualTo(1));
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 1)), Is.True);
    }

    [Test]
    public void When_tracing_storage_the_account_will_always_be_already_added()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.SLOAD)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);

        Assert.That(trace.Storages.Count, Is.EqualTo(1));
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 1)), Is.True);
        Assert.That(trace.Accounts.Contains(trace.Storages.First().Address), Is.True);
    }

    [Test]
    public void Can_trace_multiple_storage_reads_on_the_same_cell()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.SLOAD)
            .PushData("0x01")
            .Op(Instruction.SLOAD)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);

        Assert.That(trace.Storages.Count, Is.EqualTo(1));
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 1)), Is.True);
    }

    [Test]
    public void Can_trace_multiple_storage_reads()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .Op(Instruction.SLOAD)
            .PushData("0x02")
            .Op(Instruction.SLOAD)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);

        Assert.That(trace.Storages.Count, Is.EqualTo(2));
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 1)), Is.True);
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 2)), Is.True);
    }

    [Test]
    public void Can_trace_storage_write()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .PushData("0x02")
            .Op(Instruction.SSTORE)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Storages.Count, Is.EqualTo(1));
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 2)), Is.True);
    }

    [Test]
    public void Can_trace_multiple_storage_writes()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .PushData("0x02")
            .Op(Instruction.SSTORE)
            .PushData("0x03")
            .PushData("0x04")
            .Op(Instruction.SSTORE)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Storages.Count, Is.EqualTo(2));
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 2)), Is.True);
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 4)), Is.True);
    }

    [Test]
    public void Multiple_write_to_same_storage_can_be_traced_without_issues()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .PushData("0x02")
            .Op(Instruction.SSTORE)
            .PushData("0x01")
            .PushData("0x02")
            .Op(Instruction.SSTORE)
            .Done;

        (ProofTxTracer trace, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(trace.Storages.Count, Is.EqualTo(1));
        Assert.That(trace.Storages.Contains(new StorageCell(SenderRecipientAndMiner.Default.Recipient, 2)), Is.True);
    }

    [Test]
    public void Can_trace_on_failure()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x01")
            .PushData("0x02")
            .Op(Instruction.SSTORE)
            .PushData("0x01")
            .Op(Instruction.BLOCKHASH)
            .PushData(TestItem.AddressC)
            .Op(Instruction.BALANCE)
            .Op(Instruction.ADD) // stack underflow
            .Done;

        (ProofTxTracer tracer, _, _) = ExecuteAndTraceProofCall(SenderRecipientAndMiner.Default, code);
        Assert.That(tracer.Accounts.Count, Is.EqualTo(4));
        Assert.That(tracer.Output.Length, Is.EqualTo(0));
        Assert.That(tracer.BlockHashes.Count, Is.EqualTo(1));
        Assert.That(tracer.Storages.Count, Is.EqualTo(1));
    }

    protected (ProofTxTracer trace, Block block, Transaction transaction) ExecuteAndTraceProofCall(SenderRecipientAndMiner addresses, params byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses);
        ProofTxTracer tracer = new(_treatSystemAccountDifferently);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);
        return (tracer, block, transaction);
    }
}
