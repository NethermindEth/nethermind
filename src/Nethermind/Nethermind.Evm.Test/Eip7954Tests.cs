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

    [TestCase(true, 55000, ExpectedResult = false, TestName = "InitCode_between_old_and_new_limit_accepted")]
    [TestCase(false, 55000, ExpectedResult = true, TestName = "InitCode_above_old_limit_rejected_before_eip7954")]
    [TestCase(true, 2 * CodeSizeConstants.MaxCodeSizeEip7954 + 1, ExpectedResult = true, TestName = "InitCode_exceeding_new_limit_rejected")]
    public bool InitCode_size_validation(bool eip7954Enabled, long initCodeSize)
    {
        ulong timestamp = eip7954Enabled ? Timestamp : MainnetSpecProvider.ShanghaiBlockTimestamp;
        (TransactionResult result, _) = ExecuteRawCreateTransaction(timestamp, initCodeSize);
        return result == TransactionResult.TransactionSizeOverMaxInitCodeSize;
    }

    [TestCase(30000, ExpectedResult = StatusCode.Success, TestName = "Code_deposit_between_old_and_new_limit_succeeds")]
    [TestCase(CodeSizeConstants.MaxCodeSizeEip7954 + 1, ExpectedResult = StatusCode.Failure, TestName = "Code_deposit_above_new_limit_fails")]
    public byte Code_deposit_size_validation(int deployedCodeSize)
    {
        (_, TestAllTracerWithOutput tracer) = ExecuteDeployTransaction(Timestamp, deployedCodeSize);
        return tracer.StatusCode;
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
