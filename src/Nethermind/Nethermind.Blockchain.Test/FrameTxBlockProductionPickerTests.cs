// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Generic;

namespace Nethermind.Blockchain.Test;

[TestFixture]
public class FrameTxBlockProductionPickerTests
{
    private const ulong AccountNonce = 5;

    [TestCase(TxType.EIP1559, AccountNonce, BlockProcessor.TxAction.Skip, "Sender is contract",
        TestName = "Contract sender and no funds skip an ordinary transaction")]
    [TestCase(TxType.FrameTx, AccountNonce, BlockProcessor.TxAction.Add, null,
        TestName = "Contract sender and no funds admit a frame transaction")]
    [TestCase(TxType.FrameTx, AccountNonce + 1, BlockProcessor.TxAction.Skip, "Invalid nonce - expected 5",
        TestName = "The account nonce still gates a frame transaction")]
    public void Sender_account_checks(TxType txType, ulong nonce, BlockProcessor.TxAction expectedAction, string? expectedReason)
    {
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Eip8141Prototype.Instance);
        BlockProcessor.BlockProductionTransactionPicker picker = new(specProvider, BlocksConfig.DefaultMaxTxKilobytes);

        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.HasCode(TestItem.AddressA).Returns(true);
        state.GetCode(TestItem.AddressA).Returns(new byte[] { 0x60, 0x00 });
        state.GetNonce(TestItem.AddressA).Returns(AccountNonce);
        state.GetBalance(TestItem.AddressA).Returns(UInt256.Zero);

        Transaction tx = Build.A.Transaction
            .WithType(txType)
            .WithSenderAddress(TestItem.AddressA)
            .WithNonce(nonce)
            .WithGasLimit(100_000)
            .TestObject;

        if (txType == TxType.FrameTx)
        {
            // A frame transaction the picker cannot price is skipped on its gas budget before it ever
            // reaches the sender-account checks under test.
            tx.Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)];
            tx.FrameSignatures = [];
        }

        Block block = Build.A.Block.WithGasLimit(30_000_000).TestObject;

        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(block, tx, new HashSet<Transaction>(), state, block.GasUsed, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(args.Action, Is.EqualTo(expectedAction));
            Assert.That(args.Reason, expectedReason is null ? Is.Empty.Or.Null : Is.EqualTo(expectedReason));
        }
    }

    [TestCase(0UL, BlockProcessor.TxAction.Add)]
    [TestCase(600_000UL, BlockProcessor.TxAction.Skip)]
    public void Frame_transaction_is_checked_against_each_remaining_block_dimension(
        ulong cumulativeStateGas,
        BlockProcessor.TxAction expectedAction)
    {
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Eip8141Prototype.Instance);
        BlockProcessor.BlockProductionTransactionPicker picker = new(specProvider, BlocksConfig.DefaultMaxTxKilobytes);
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.GetNonce(TestItem.AddressA).Returns(AccountNonce);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            Nonce = AccountNonce,
            SenderAddress = TestItem.AddressA,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null,
                    executionGasLimit: 500_000, stateGasLimit: 500_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
        Block block = Build.A.Block.WithGasLimit(1_000_000).TestObject;

        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(
            block,
            tx,
            new HashSet<Transaction>(),
            state,
            cumulativeBlockExecutionGas: 0,
            cumulativeStateGas);

        Assert.That(args.Action, Is.EqualTo(expectedAction));
    }

    [Test]
    public void Execution_headroom_is_measured_from_cumulative_execution_not_the_block_maximum()
    {
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Eip8141Prototype.Instance);
        BlockProcessor.BlockProductionTransactionPicker picker = new(specProvider, BlocksConfig.DefaultMaxTxKilobytes);
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.GetNonce(TestItem.AddressA).Returns(AccountNonce);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            Nonce = AccountNonce,
            SenderAddress = TestItem.AddressA,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null,
                    executionGasLimit: 500_000, stateGasLimit: 0, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
        Block block = Build.A.Block.WithGasLimit(1_000_000).TestObject;

        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(
            block,
            tx,
            new HashSet<Transaction>(),
            state,
            cumulativeBlockExecutionGas: 100_000,
            cumulativeBlockStateGas: 600_000);

        Assert.That(args.Action, Is.EqualTo(BlockProcessor.TxAction.Add));
    }
}
