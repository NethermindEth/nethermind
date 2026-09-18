// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class LogOpcodeTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static readonly Hash256[] Topics =
    [
        Topic(0xa1),
        Topic(0xb2),
        Topic(0xc3),
        Topic(0xd4),
    ];

    [Test]
    public void Log_family_records_exact_payload_topics_and_gas([Range(0, 4)] int topicCount)
    {
        byte[] payload = [0xaa, 0xbb, 0xcc, 0xdd];
        Prepare code = Prepare.EvmCode.StoreDataInMemory(0, payload);
        for (int index = topicCount - 1; index >= 0; index--)
        {
            code.PushData(Topics[index].Bytes.ToArray());
        }

        code.PushData(2).PushData(1).Op((byte)((byte)Instruction.LOG0 + topicCount));

        (CapturingTracer tracer, ulong intrinsicGas) = ExecuteWithExecutionGas(code.Done, 10_000);

        Assert.That(tracer.Logs, Has.Length.EqualTo(1));
        LogEntry log = tracer.Logs[0];
        ulong expectedExecutionGas = 409UL + 378UL * (ulong)topicCount;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null);
            Assert.That(log.Address, Is.EqualTo(Recipient));
            Assert.That(log.Data, Is.EqualTo(new byte[] { 0xbb, 0xcc }));
            Assert.That(log.Topics, Is.EqualTo(Topics[..topicCount]));
            Assert.That(tracer.GasSpent, Is.EqualTo(intrinsicGas + expectedExecutionGas));
        }
    }

    [TestCase(459UL, null)]
    [TestCase(458UL, nameof(EvmExceptionType.OutOfGas))]
    public void Log0_charges_the_quadratic_memory_boundary(ulong executionGas, string? expectedError)
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(704)
            .Op(Instruction.LOG0)
            .Done;

        (CapturingTracer tracer, ulong intrinsicGas) = ExecuteWithExecutionGas(code, executionGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.EqualTo(expectedError));
            Assert.That(tracer.Logs, Has.Length.EqualTo(expectedError is null ? 1 : 0));
            Assert.That(tracer.GasSpent, Is.EqualTo(expectedError is null ? intrinsicGas + 459UL : intrinsicGas + executionGas));
        }
    }

    [Test]
    public void Topic_underflow_does_not_emit_a_partial_log()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0xa1)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.LOG2)
            .Done;

        (CapturingTracer tracer, _) = ExecuteWithExecutionGas(code, 10_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.StackUnderflow)));
            Assert.That(tracer.Logs, Is.Empty);
        }
    }

    private (CapturingTracer Tracer, ulong IntrinsicGas) ExecuteWithExecutionGas(byte[] code, ulong executionGas)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, code, value: 0);
        IntrinsicGas<EthereumGasPolicy> intrinsic =
            EthereumGasPolicy.CalculateIntrinsicGas(transaction, Spec, block.Header.GasLimit);
        ulong intrinsicGas = intrinsic.StandardGas;
        transaction.GasLimit = checked(intrinsicGas + executionGas);
        CapturingTracer tracer = new();

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);

        return (tracer, intrinsicGas);
    }

    private static Hash256 Topic(byte suffix)
    {
        byte[] bytes = new byte[Hash256.Size];
        bytes[^1] = suffix;
        return new Hash256(bytes);
    }

    private sealed class CapturingTracer : TestAllTracerWithOutput
    {
        public LogEntry[] Logs { get; private set; } = [];

        public override void MarkAsSuccess(
            Address recipient,
            in GasConsumed gasSpent,
            byte[] output,
            LogEntry[] logs,
            Hash256? stateRoot = null)
        {
            Logs = logs;
            base.MarkAsSuccess(recipient, in gasSpent, output, logs, stateRoot);
        }
    }
}
