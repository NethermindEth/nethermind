// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[TestFixture]
public class BlockProductionTransactionPickerTests
{
    private const ulong BlockGasLimit = 1_000_000;
    private const ulong TxGasLimit = 100_000;
    private const ulong AccountNonce = 3;

    private static BlockProcessor.BlockProductionTransactionPicker CreatePicker(IReleaseSpec spec) =>
        new(new TestSingleReleaseSpecProvider(spec));

    private static Transaction Tx(ulong nonce = AccountNonce, UInt256? value = null, Address? sender = null) => Build.A.Transaction
        .WithType(TxType.EIP1559)
        .WithSenderAddress(sender ?? TestItem.AddressA)
        .WithTo(TestItem.AddressB)
        .WithNonce(nonce)
        .WithValue(value ?? UInt256.Zero)
        .WithGasLimit(TxGasLimit)
        .WithMaxFeePerGas(0)
        .WithMaxPriorityFeePerGas(0)
        .TestObject;

    private static Block BlockWithoutBaseFee() => Build.A.Block.WithGasLimit(BlockGasLimit).WithBaseFeePerGas(0).TestObject;

    private sealed class FundedSender : IDisposable
    {
        private readonly IDisposable _scope;

        public FundedSender(UInt256 balance)
        {
            State = TestWorldStateFactory.CreateForTest();
            _scope = State.BeginScope(IWorldState.PreGenesis);
            State.CreateAccount(TestItem.AddressA, balance, AccountNonce);
            State.Commit(Osaka.Instance);
        }

        public IWorldState State { get; }

        public void Dispose() => _scope.Dispose();
    }

    private static IEnumerable<TestCaseData> SkipReasons()
    {
        yield return new TestCaseData(Osaka.Instance, Tx(), BlockGasLimit - 50_000, 0UL, BlockProcessor.TxAction.Skip,
                "Not enough execution gas in block, gas limit 100000 > 50000")
            .SetName("Execution gas skip reason");
        yield return new TestCaseData(Amsterdam.Instance, Tx(), 0UL, BlockGasLimit - 50_000, BlockProcessor.TxAction.Skip,
                "Not enough state gas in block, gas limit 100000 > 50000")
            .SetName("State gas skip reason");
        yield return new TestCaseData(Osaka.Instance, Tx(nonce: AccountNonce + 2), 0UL, 0UL, BlockProcessor.TxAction.Skip,
                "Invalid nonce - expected 3")
            .SetName("Nonce skip reason");
        yield return new TestCaseData(Osaka.Instance, Tx(value: 2.Ether), 0UL, 0UL, BlockProcessor.TxAction.Skip,
                $"Transaction cost ({2.Ether}) is higher than sender balance ({1.Ether})")
            .SetName("Balance skip reason");
        yield return new TestCaseData(Osaka.Instance, Tx(), BlockGasLimit - 20_000, 0UL, BlockProcessor.TxAction.Stop,
                "Block full")
            .SetName("Block full stop reason");
        yield return new TestCaseData(Osaka.Instance, Tx(), 0UL, 0UL, BlockProcessor.TxAction.Add, string.Empty)
            .SetName("An added candidate has no reason");
    }

    [TestCaseSource(nameof(SkipReasons))]
    public void Rejection_reasons_keep_their_text(IReleaseSpec spec, Transaction tx, ulong cumulativeExecutionGas,
        ulong cumulativeStateGas, BlockProcessor.TxAction expectedAction, string expectedReason)
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();

        BlockProcessor.AddingTxEventArgs args = CreatePicker(spec).CanAddTransaction(
            block, tx, new HashSet<Transaction>(), sender.State, cumulativeExecutionGas, cumulativeStateGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(args.Action, Is.EqualTo(expectedAction));
            string reason = args.Reason;
            Assert.That(reason, Is.EqualTo(expectedReason));
            Assert.That(args.Reason, Is.SameAs(reason), "a deferred reason is formatted once");
            Assert.That(args.Transaction, Is.SameAs(tx));
            Assert.That(args.Block, Is.SameAs(block));
        }
    }

    [Test]
    public void Constant_rejection_reasons_keep_their_text()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);

        Transaction noSender = Tx();
        noSender.SenderAddress = null;
        Transaction considered = Tx();
        HashSet<Transaction> transactionsInBlock = [considered];

        BlockProcessor.AddingTxEventArgs nullSender = picker.CanAddTransaction(block, noSender, new HashSet<Transaction>(), sender.State, 0, 0);
        Assert.That((nullSender.Action, nullSender.Reason), Is.EqualTo((BlockProcessor.TxAction.Skip, "Null sender")));

        BlockProcessor.AddingTxEventArgs alreadyIn = picker.CanAddTransaction(block, considered, transactionsInBlock, sender.State, 0, 0);
        Assert.That((alreadyIn.Action, alreadyIn.Reason, alreadyIn.Index), Is.EqualTo((BlockProcessor.TxAction.Skip, "Transaction already in block", 1)));
    }

    [Test]
    public void A_skip_after_a_skip_with_a_deferred_reason_reports_its_own_reason()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);

        BlockProcessor.AddingTxEventArgs gasSkip = picker.CanAddTransaction(block, Tx(), new HashSet<Transaction>(), sender.State, BlockGasLimit - 50_000, 0);
        Assert.That(gasSkip.Action, Is.EqualTo(BlockProcessor.TxAction.Skip));

        Transaction noSender = Tx();
        noSender.SenderAddress = null;
        BlockProcessor.AddingTxEventArgs nullSender = picker.CanAddTransaction(block, noSender, new HashSet<Transaction>(), sender.State, 0, 0);

        Assert.That((nullSender.Action, nullSender.Reason, nullSender.Transaction), Is.EqualTo((BlockProcessor.TxAction.Skip, "Null sender", noSender)));
    }

    /// <summary>One picker rejects candidates for different deferred reasons in turn; each reports its own kind and numbers,
    /// in both orders, so nothing of an earlier rejection's reason sticks to the reused args.</summary>
    [Test]
    public void Deferred_rejections_of_different_kinds_each_report_their_own_reason()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Amsterdam.Instance);
        HashSet<Transaction> transactionsInBlock = [];

        string Reason(Transaction tx, ulong cumulativeExecutionGas, ulong cumulativeStateGas)
        {
            BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(block, tx, transactionsInBlock, sender.State, cumulativeExecutionGas, cumulativeStateGas);
            Assert.That(args.Action, Is.EqualTo(BlockProcessor.TxAction.Skip));
            return args.Reason;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Reason(Tx(), BlockGasLimit - 50_000, 0), Is.EqualTo("Not enough execution gas in block, gas limit 100000 > 50000"));
            Assert.That(Reason(Tx(nonce: AccountNonce + 2), 0, 0), Is.EqualTo("Invalid nonce - expected 3"));
            Assert.That(Reason(Tx(), BlockGasLimit - 60_000, 0), Is.EqualTo("Not enough execution gas in block, gas limit 100000 > 60000"));
            Assert.That(Reason(Tx(), 0, BlockGasLimit - 70_000), Is.EqualTo("Not enough state gas in block, gas limit 100000 > 70000"));
            Assert.That(Reason(Tx(nonce: AccountNonce + 1), 0, 0), Is.EqualTo("Invalid nonce - expected 3"));
        }
    }

    /// <summary>Subscribers such as AuRa's permission check may keep or change the args they are raised with, so a later
    /// candidate must never be written into them, and what they set must be what the caller gets back.</summary>
    [Test]
    public void Added_candidates_get_args_of_their_own_that_later_calls_leave_alone()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);
        List<BlockProcessor.AddingTxEventArgs> raised = [];
        picker.AddingTransaction += (_, e) => raised.Add(e);

        Transaction first = Tx();
        BlockProcessor.AddingTxEventArgs added = picker.CanAddTransaction(block, first, new HashSet<Transaction>(), sender.State, 0, 0);
        BlockProcessor.AddingTxEventArgs skipped = picker.CanAddTransaction(block, Tx(), new HashSet<Transaction>(), sender.State, BlockGasLimit - 50_000, 0);
        BlockProcessor.AddingTxEventArgs addedAgain = picker.CanAddTransaction(block, Tx(), new HashSet<Transaction>(), sender.State, 0, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raised, Has.Count.EqualTo(2), "only added candidates raise the event");
            Assert.That(raised[0], Is.SameAs(added));
            Assert.That(raised[1], Is.SameAs(addedAgain));
            Assert.That(addedAgain, Is.Not.SameAs(added));
            Assert.That(skipped, Is.Not.SameAs(added).And.Not.SameAs(addedAgain));
            Assert.That((added.Action, added.Reason, added.Transaction), Is.EqualTo((BlockProcessor.TxAction.Add, string.Empty, first)),
                "a later skip must not rewrite args a subscriber kept");
        }
    }

    [Test]
    public void A_subscriber_can_still_skip_the_candidate_it_is_raised_for()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);
        picker.AddingTransaction += (_, e) => e.Set(BlockProcessor.TxAction.Skip, "not permitted");

        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(block, Tx(), new HashSet<Transaction>(), sender.State, 0, 0);

        Assert.That((args.Action, args.Reason), Is.EqualTo((BlockProcessor.TxAction.Skip, "not permitted")));
    }

    [TestCase(BlockGasLimit - 50_000, AccountNonce, TestName = "A candidate skipped for gas allocates nothing")]
    [TestCase(0UL, AccountNonce + 2, TestName = "A candidate skipped for its nonce allocates nothing")]
    public void A_skipped_candidate_allocates_nothing(ulong cumulativeExecutionGas, ulong nonce)
    {
        const int calls = 1_000;
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);
        Transaction tx = Tx(nonce);
        HashSet<Transaction> transactionsInBlock = [];

        BlockProcessor.TxAction Skip() => picker.CanAddTransaction(block, tx, transactionsInBlock, sender.State, cumulativeExecutionGas, 0).Action;

        for (int i = 0; i < 10; i++)
        {
            Assert.That(Skip(), Is.EqualTo(BlockProcessor.TxAction.Skip));
        }

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < calls; i++)
        {
            Skip();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        // Less than a byte per call: an args object or a reason string per candidate would be tens of kilobytes.
        Assert.That(allocated, Is.LessThan(calls));
    }
}
