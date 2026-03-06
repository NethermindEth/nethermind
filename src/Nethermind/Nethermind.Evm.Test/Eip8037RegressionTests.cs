// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037RegressionTests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    [Test]
    public void Eip8037_nested_create_code_deposit_must_not_borrow_parent_regular_gas()
    {
        const long spillIntoParent = 4;
        long regularDepositCost = GasCostOf.CodeDepositRegularPerWord;
        long stateDepositCost = GasCostOf.CodeDepositState;

        EthereumGasPolicy childGas = new()
        {
            Value = regularDepositCost,
            StateReservoir = stateDepositCost - spillIntoParent,
        };

        EthereumGasPolicy childOnly = childGas;
        Assert.That(CanChargeCodeDeposit(ref childOnly, stateDepositCost, regularDepositCost), Is.False);

        // Mirror the nested CREATE success path in VirtualMachine:
        // the caller keeps a little regular gas, the child returns with too little regular
        // gas for state spill + code deposit, and then the child gas is refunded first.
        EthereumGasPolicy callerGas = new()
        {
            Value = spillIntoParent,
            StateReservoir = 0,
        };

        EthereumGasPolicy mergedGas = callerGas;
        EthereumGasPolicy.Refund(ref mergedGas, in childGas);

        Assert.That(
            CanChargeCodeDeposit(ref mergedGas, stateDepositCost, regularDepositCost),
            Is.False,
            "Nested CREATE code deposit must stay inside the child gas budget and must not consume the caller's retained gas.");
    }

    [Test]
    public void Eip8037_net_zero_sstore_should_not_leave_block_state_gas()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, code);
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        StorageCell storageCell = new(Recipient, 0);
        Assert.That(TestState.Get(storageCell).IsZero(), Is.True);
        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(1));
        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(transaction.BlockGasUsed, Is.GreaterThan(0));
        Assert.That(
            block.Header.GasUsed,
            Is.EqualTo(transaction.BlockGasUsed),
            "A successful 0 -> X -> 0 SSTORE sequence should not leave committed state gas in Amsterdam block accounting.");
    }

    private static bool CanChargeCodeDeposit(ref EthereumGasPolicy gas, long stateDepositCost, long regularDepositCost)
        => EthereumGasPolicy.ConsumeStateGas(ref gas, stateDepositCost)
            && EthereumGasPolicy.UpdateGas(ref gas, regularDepositCost);
}
