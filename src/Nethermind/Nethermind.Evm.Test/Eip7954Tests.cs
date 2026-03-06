// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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
    public bool InitCode_size_validation(bool eip7954Enabled, int initCodeSize) =>
        ExecuteRawCreateTransaction(eip7954Enabled ? Timestamp : MainnetSpecProvider.ShanghaiBlockTimestamp, initCodeSize) == TransactionResult.TransactionSizeOverMaxInitCodeSize;

    [TestCase(30000, ExpectedResult = StatusCode.Success, TestName = "Code_deposit_between_old_and_new_limit_succeeds")]
    [TestCase(CodeSizeConstants.MaxCodeSizeEip7954 + 1, ExpectedResult = StatusCode.Failure, TestName = "Code_deposit_above_new_limit_fails")]
    public byte Code_deposit_size_validation(int deployedCodeSize) => ExecuteDeployTransaction(Timestamp, deployedCodeSize).StatusCode;

    [Test]
    public void Eip8037_top_level_code_deposit_above_new_limit_refunds_unused_gas()
    {
        const int gasLimit = 100_000_000;

        TestAllTracerWithOutput tracer = ExecuteDeployTransaction(Timestamp, CodeSizeConstants.MaxCodeSizeEip7954 + 1, Amsterdam.Instance, gasLimit);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasSpent, Is.LessThan(gasLimit));
    }

    [Test]
    public void Eip8037_top_level_over_max_fixture_gas_matches_pyspec()
    {
        const int gasLimit = 100_000_000;
        const long expectedGasSpent = 55_379_510;

        byte[] deployedCode = new byte[CodeSizeConstants.MaxCodeSizeEip7954 + 1];
        Array.Fill(deployedCode, (byte)Instruction.JUMPDEST);

        byte[] initCode = [0x61, 0x80, 0x01, 0x60, 0x00, 0x81, 0x60, 0x0b, 0x82, 0x39, 0xf3];

        byte[] txData = new byte[initCode.Length + deployedCode.Length];
        initCode.CopyTo(txData, 0);
        deployedCode.CopyTo(txData, initCode.Length);

        (Block block, Transaction transaction) = PrepareTx((BlockNumber, Timestamp), gasLimit, txData, blockGasLimit: 120_000_000, gasPrice: 10);

        transaction.To = null;
        transaction.Data = txData;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Amsterdam.Instance), tracer);

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.GasSpent, Is.EqualTo(expectedGasSpent));
    }

    private TransactionResult ExecuteRawCreateTransaction(ulong timestamp, int initCodeSize)
    {
        byte[] initCode = new byte[initCodeSize];

        TestState.CreateAccount(TestItem.AddressC, 1.Ether());

        (Block block, Transaction transaction) = PrepareTx((BlockNumber, timestamp), 5_000_000, initCode);

        transaction.GasPrice = 2.GWei();
        transaction.To = null;
        transaction.Data = initCode;
        return _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);
    }

    private TestAllTracerWithOutput ExecuteDeployTransaction(ulong timestamp, int deployedCodeSize)
        => ExecuteDeployTransaction(timestamp, deployedCodeSize, Amsterdam.NoEip8037Instance, 7_500_000);

    private TestAllTracerWithOutput ExecuteDeployTransaction(ulong timestamp, int deployedCodeSize, IReleaseSpec spec, long gasLimit)
    {
        // Build minimal init code: PUSH size, PUSH 0, RETURN
        // Returns deployedCodeSize zero bytes from memory as deployed contract code
        byte[] initCode = Prepare.EvmCode
            .PushData(deployedCodeSize)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether());

        (Block block, Transaction transaction) = PrepareTx((BlockNumber, timestamp), gasLimit, initCode, blockGasLimit: 50_000_000);

        transaction.GasPrice = 2.GWei();
        transaction.To = null;
        transaction.Data = initCode;
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, spec), tracer);
        return tracer;
    }
}
