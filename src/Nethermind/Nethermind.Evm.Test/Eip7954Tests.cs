// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
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

    private TransactionResult ExecuteRawCreateTransaction(ulong timestamp, int initCodeSize)
    {
        byte[] initCode = new byte[initCodeSize];

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);

        (Block block, Transaction transaction) = PrepareTx((BlockNumber, timestamp), 5_000_000, initCode);

        transaction.GasPrice = 2.GWei;
        transaction.To = null;
        transaction.Data = initCode;
        return _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);
    }

    [Test]
    public void Eip8037_floor_gas_enforced_in_validate_gas()
    {
        // One gas below the calldata floor (still above the standard intrinsic): a floor rejection.
        byte[] calldata = new byte[100];
        for (int i = 0; i < calldata.Length; i++) calldata[i] = 0xFF;

        IReleaseSpec spec = SpecProvider.GetSpec(Activation);
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);

        // Probe tx (generous gas) to read the spec-correct floor/standard intrinsic for this calldata.
        (Block probeBlock, Transaction probeTx) = PrepareTx(Activation, 1_000_000, code: null, input: calldata, value: 0);
        EthereumIntrinsicGas intrinsic = IntrinsicGasCalculator.Calculate(probeTx, spec, probeBlock.Header.GasLimit);
        Assert.That(intrinsic.FloorGas, Is.GreaterThan(intrinsic.Standard),
            "the calldata floor must exceed the standard intrinsic for this calldata-heavy tx");
        ulong gasLimit = intrinsic.FloorGas - 1;

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code: null, input: calldata, value: 0);

        TransactionResult result = _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, spec),
            NullTxTracer.Instance);

        Assert.That(result, Is.EqualTo(TransactionResult.GasLimitBelowFloorGas));
    }

    private TestAllTracerWithOutput ExecuteDeployTransaction(ulong timestamp, int deployedCodeSize)
    {
        // Build minimal init code: PUSH size, PUSH 0, RETURN
        // Returns deployedCodeSize zero bytes from memory as deployed contract code
        byte[] initCode = Prepare.EvmCode
            .PushData(deployedCodeSize)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);

        (Block block, Transaction transaction) = PrepareTx((BlockNumber, timestamp), 7_500_000, initCode, blockGasLimit: 50_000_000);

        transaction.GasPrice = 2.GWei;
        transaction.To = null;
        transaction.Data = initCode;
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Amsterdam.NoEip8037Instance), tracer);
        return tracer;
    }
}
