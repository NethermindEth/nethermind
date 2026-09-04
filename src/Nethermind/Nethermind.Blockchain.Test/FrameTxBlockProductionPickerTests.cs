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

    private static BlockProcessor.BlockProductionTransactionPicker CreatePicker()
    {
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Eip8141Prototype.Instance);
        return new BlockProcessor.BlockProductionTransactionPicker(specProvider, BlocksConfig.DefaultMaxTxKilobytes);
    }

    private static IReadOnlyStateProvider StateWithAccountNonce()
    {
        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.GetNonce(TestItem.AddressA).Returns(AccountNonce);
        return state;
    }

    private static Transaction FrameTx(ulong nonce, UInt256[]? nonceKeys, ulong executionGasLimit, ulong stateGasLimit) => new()
    {
        Type = TxType.FrameTx,
        ChainId = 1,
        Nonce = nonce,
        NonceKeys = nonceKeys,
        SenderAddress = TestItem.AddressA,
        Frames =
        [
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null,
                executionGasLimit, stateGasLimit, UInt256.Zero, default),
        ],
        FrameSignatures = [],
        GasPrice = 1,
        DecodedMaxFeePerGas = 1,
    };

    [TestCase(TxType.EIP1559, AccountNonce, BlockProcessor.TxAction.Skip, "Sender is contract",
        TestName = "Contract sender and no funds skip an ordinary transaction")]
    [TestCase(TxType.FrameTx, AccountNonce, BlockProcessor.TxAction.Add, null,
        TestName = "Contract sender and no funds admit a frame transaction")]
    [TestCase(TxType.FrameTx, AccountNonce + 1, BlockProcessor.TxAction.Skip, "Invalid nonce - expected 5",
        TestName = "The account nonce still gates a frame transaction")]
    public void Sender_account_checks(TxType txType, ulong nonce, BlockProcessor.TxAction expectedAction, string? expectedReason)
    {
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker();

        IReadOnlyStateProvider state = StateWithAccountNonce();
        state.HasCode(TestItem.AddressA).Returns(true);
        state.GetCode(TestItem.AddressA).Returns(new byte[] { 0x60, 0x00 });
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
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker();
        IReadOnlyStateProvider state = StateWithAccountNonce();

        Transaction tx = FrameTx(AccountNonce, nonceKeys: null, executionGasLimit: 500_000, stateGasLimit: 500_000);
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
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker();
        IReadOnlyStateProvider state = StateWithAccountNonce();

        Transaction tx = FrameTx(AccountNonce, nonceKeys: null, executionGasLimit: 500_000, stateGasLimit: 0);
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

    // EIP-8250 moves a keyed transaction's replay protection into NONCE_MANAGER, so nonce_seq is
    // unrelated to the sender's account nonce; only the [0] set still means the account nonce.
    [TestCase(false, BlockProcessor.TxAction.Skip, TestName = "The account-nonce domain still gates on the account nonce")]
    [TestCase(true, BlockProcessor.TxAction.Add, TestName = "A keyed nonce domain is not gated on the account nonce")]
    public void Keyed_nonce_frame_transaction_is_not_gated_on_the_account_nonce(bool keyedDomain, BlockProcessor.TxAction expectedAction)
    {
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker();
        IReadOnlyStateProvider state = StateWithAccountNonce();

        // A fresh sequence, which the account nonce of 5 cannot coincide with.
        Transaction tx = FrameTx(nonce: 0, keyedDomain ? [UInt256.One] : [UInt256.Zero], executionGasLimit: 100_000, stateGasLimit: 0);
        Block block = Build.A.Block.WithGasLimit(30_000_000).TestObject;

        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(
            block, tx, new HashSet<Transaction>(), state, block.GasUsed, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(args.Action, Is.EqualTo(expectedAction));
            // Pins the negative control to the nonce gate: the earlier Skip exits would report otherwise.
            Assert.That(args.Reason, expectedAction == BlockProcessor.TxAction.Skip
                ? Is.EqualTo($"Invalid nonce - expected {AccountNonce}")
                : Is.Empty.Or.Null);
        }
    }
}
