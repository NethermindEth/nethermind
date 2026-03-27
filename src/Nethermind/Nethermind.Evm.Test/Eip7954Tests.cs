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

    [TestCase(false, TestName = "Over_max_initcode_via_CREATE_spends_all_tx_gas_but_excludes_create_state_from_block_gas")]
    [TestCase(true, TestName = "Over_max_initcode_via_CREATE2_spends_all_tx_gas_but_excludes_create_state_from_block_gas")]
    public void Over_max_initcode_via_create_spends_all_tx_gas_but_excludes_create_state_from_block_gas(bool create2)
    {
        const long gasLimit = 0x1000000;

        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            PrepareOverMaxInitCodeCreate(create2),
            blockGasLimit: gasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.OutOfGas)));
        Assert.That(transaction.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(transaction.BlockGasUsed, Is.EqualTo(gasLimit - GasCostOf.CreateState));
        Assert.That(block.Header.GasUsed, Is.EqualTo(gasLimit - GasCostOf.CreateState));
    }

    [TestCase(false, TestName = "Over_max_initcode_via_CREATE_with_too_little_gas_for_regular_costs_keeps_full_block_gas")]
    [TestCase(true, TestName = "Over_max_initcode_via_CREATE2_with_too_little_gas_for_regular_costs_keeps_full_block_gas")]
    public void Over_max_initcode_via_create_with_too_little_gas_for_regular_costs_keeps_full_block_gas(bool create2)
    {
        const long gasLimit = 160_000;

        (Block block, Transaction transaction) = PrepareTx(
            Activation,
            gasLimit,
            PrepareOverMaxInitCodeCreate(create2),
            blockGasLimit: gasLimit);

        TestAllTracerWithOutput tracer = CreateTracer();
        TransactionResult result = _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            tracer);

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(tracer.Error, Is.EqualTo(nameof(EvmExceptionType.OutOfGas)));
        Assert.That(transaction.SpentGas, Is.EqualTo(gasLimit));
        Assert.That(transaction.BlockGasUsed, Is.EqualTo(gasLimit));
        Assert.That(block.Header.GasUsed, Is.EqualTo(gasLimit));
    }

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
        // Craft a calldata-heavy tx where floor gas exceeds regular + state gas.
        // 100 non-zero bytes: tokens = 100*4 = 400
        // regularGas = 21000 + 400*4 = 22600, stateGas = 0, floorGas = 21000 + 400*10 = 25000
        // gasLimit = 23000 is between regularGas and floorGas — must be rejected.
        byte[] calldata = new byte[100];
        for (int i = 0; i < calldata.Length; i++) calldata[i] = 0xFF;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);

        (Block block, Transaction transaction) = PrepareTx(Activation, 23000, null);
        transaction.Data = calldata;
        transaction.To = TestItem.AddressC;

        TransactionResult result = _processor.Execute(
            transaction,
            new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            NullTxTracer.Instance);

        Assert.That(result, Is.EqualTo(TransactionResult.GasLimitBelowIntrinsicGas));
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

    private byte[] PrepareOverMaxInitCodeCreate(bool create2)
    {
        int initCodeLength = checked((int)Spec.MaxInitCodeSize + 1);
        Prepare prepare = Prepare.EvmCode;
        if (create2)
        {
            prepare = prepare.PushData(0);
        }

        return prepare
            .PushData(initCodeLength)
            .PushData(0)
            .PushData(0)
            .Op(create2 ? Instruction.CREATE2 : Instruction.CREATE)
            .Done;
    }
}
