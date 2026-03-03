// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7954Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    [Test]
    public void MaxCodeSize_and_MaxInitCodeSize_are_correct()
    {
        Assert.That(Spec.MaxCodeSize, Is.EqualTo(CodeSizeConstants.MaxCodeSizeEip7954));
        Assert.That(Spec.MaxInitCodeSize, Is.EqualTo(2L * CodeSizeConstants.MaxCodeSizeEip7954));
    }

    [Test]
    public void InitCode_above_old_limit_accepted_with_eip7954()
    {
        // Initcode between old limit (49152) and new limit (65536) should be accepted
        (TransactionResult result, _) = ExecuteRawCreateTransaction(Timestamp, 55000);

        Assert.That(result, Is.Not.EqualTo(TransactionResult.TransactionSizeOverMaxInitCodeSize));
    }

    [Test]
    public void InitCode_above_old_limit_rejected_before_eip7954()
    {
        // Same initcode size should be rejected before EIP-7954 (Shanghai era)
        (TransactionResult result, _) = ExecuteRawCreateTransaction(MainnetSpecProvider.ShanghaiBlockTimestamp, 55000);

        Assert.That(result, Is.EqualTo(TransactionResult.TransactionSizeOverMaxInitCodeSize));
    }

    [Test]
    public void InitCode_exceeding_new_limit_rejected()
    {
        // Initcode exceeding new limit (65536) should be rejected even with EIP-7954
        (TransactionResult result, _) = ExecuteRawCreateTransaction(Timestamp, 2 * CodeSizeConstants.MaxCodeSizeEip7954 + 1);

        Assert.That(result, Is.EqualTo(TransactionResult.TransactionSizeOverMaxInitCodeSize));
    }

    [Test]
    public void Code_deposit_between_old_and_new_limit_succeeds()
    {
        // Deployed bytecode of 30000 bytes (between old 24576 and new 32768 limit) should succeed
        (_, TestAllTracerWithOutput tracer) = ExecuteDeployTransaction(Timestamp, 30000);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
    }

    [Test]
    public void Code_deposit_above_new_limit_fails()
    {
        // Deployed bytecode exceeding new limit (32768) should fail
        (_, TestAllTracerWithOutput tracer) = ExecuteDeployTransaction(Timestamp, CodeSizeConstants.MaxCodeSizeEip7954 + 1);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    private (TransactionResult, TestAllTracerWithOutput) ExecuteRawCreateTransaction(ulong timestamp, long initCodeSize)
    {
        byte[] initCode = new byte[initCodeSize];

        TestState.CreateAccount(TestItem.AddressC, 1.Ether());

        (Block block, Transaction transaction) = PrepareTx((BlockNumber, timestamp), 5_000_000, initCode);

        transaction.GasPrice = 2.GWei();
        transaction.To = null;
        transaction.Data = initCode;
        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return (result, tracer);
    }

    private (TransactionResult, TestAllTracerWithOutput) ExecuteDeployTransaction(ulong timestamp, int deployedCodeSize)
    {
        // Build minimal init code: PUSH size, PUSH 0, RETURN
        // Returns deployedCodeSize zero bytes from memory as deployed contract code
        byte[] initCode = Prepare.EvmCode
            .PushData(deployedCodeSize)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether());

        (Block block, Transaction transaction) = PrepareTx((BlockNumber, timestamp), 7_500_000, initCode, blockGasLimit: 50_000_000);

        transaction.GasPrice = 2.GWei();
        transaction.To = null;
        transaction.Data = initCode;
        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return (result, tracer);
    }
}
