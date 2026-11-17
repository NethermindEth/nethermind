// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    private static Transaction TxWithoutSender()
    {
        Transaction tx = Tx();
        tx.SenderAddress = null;
        return tx;
    }

    private static Block BlockWithoutBaseFee() => Build.A.Block.WithGasLimit(BlockGasLimit).WithBaseFeePerGas(0).TestObject;

    private static string ExecutionGasReason(ulong gasRemaining) => $"Not enough execution gas in block, gas limit {TxGasLimit} > {gasRemaining}";

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
        yield return new TestCaseData(Osaka.Instance, TxWithoutSender(), 0UL, 0UL, BlockProcessor.TxAction.Skip, "Null sender")
            .SetName("Null sender skip reason");
        yield return new TestCaseData(Osaka.Instance, Tx(), 0UL, 0UL, BlockProcessor.TxAction.Add, string.Empty)
            .SetName("An added candidate has no reason");
    }

    /// <summary>The skips whose reason is built from numbers: execution gas, state gas and the account nonce.</summary>
    private static IEnumerable<TestCaseData> DeferredSkips()
    {
        yield return new TestCaseData(Osaka.Instance, AccountNonce, BlockGasLimit - 50_000, 0UL, ExecutionGasReason(50_000))
            .SetArgDisplayNames("execution gas");
        yield return new TestCaseData(Amsterdam.Instance, AccountNonce, 0UL, BlockGasLimit - 50_000,
                "Not enough state gas in block, gas limit 100000 > 50000")
            .SetArgDisplayNames("state gas");
        yield return new TestCaseData(Osaka.Instance, AccountNonce + 2, 0UL, 0UL, "Invalid nonce - expected 3")
            .SetArgDisplayNames("account nonce");
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
    public void A_transaction_already_in_block_keeps_its_reason_and_index()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);

        Transaction considered = Tx();
        HashSet<Transaction> transactionsInBlock = [considered];

        BlockProcessor.AddingTxEventArgs alreadyIn = picker.CanAddTransaction(block, considered, transactionsInBlock, sender.State, 0, 0);
        Assert.That((alreadyIn.Action, alreadyIn.Reason, alreadyIn.Index), Is.EqualTo((BlockProcessor.TxAction.Skip, "Transaction already in block", 1)));
    }

    /// <summary>A rejection kept while the same picker rejects other candidates, of every kind and with other numbers,
    /// still reports its own candidate when it is read last; every rejection is an object of its own.</summary>
    [Test]
    public void A_kept_rejection_reports_its_own_candidate_after_later_rejections()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        Block laterBlock = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Amsterdam.Instance);

        Transaction candidate = Tx();
        HashSet<Transaction> transactionsInBlock = [Tx(nonce: AccountNonce + 10)];
        BlockProcessor.AddingTxEventArgs kept = picker.CanAddTransaction(block, candidate, transactionsInBlock, sender.State, BlockGasLimit - 50_000, 0);

        HashSet<Transaction> noTransactions = [];
        (Transaction Tx, ulong ExecutionGas, ulong StateGas, BlockProcessor.TxAction Action, string Reason)[] later =
        [
            (Tx(), 0, BlockGasLimit - 70_000, BlockProcessor.TxAction.Skip, "Not enough state gas in block, gas limit 100000 > 70000"),
            (Tx(nonce: AccountNonce + 2), 0, 0, BlockProcessor.TxAction.Skip, "Invalid nonce - expected 3"),
            (Tx(), BlockGasLimit - 60_000, 0, BlockProcessor.TxAction.Skip, ExecutionGasReason(60_000)),
            (TxWithoutSender(), 0, 0, BlockProcessor.TxAction.Skip, "Null sender"),
            (Tx(), BlockGasLimit, 0, BlockProcessor.TxAction.Stop, "Block full"),
        ];
        BlockProcessor.AddingTxEventArgs[] laterArgs = later
            .Select(c => picker.CanAddTransaction(laterBlock, c.Tx, noTransactions, sender.State, c.ExecutionGas, c.StateGas))
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kept.Action, Is.EqualTo(BlockProcessor.TxAction.Skip));
            Assert.That(kept.Reason, Is.EqualTo(ExecutionGasReason(50_000)));
            Assert.That(kept.Transaction, Is.SameAs(candidate));
            Assert.That(kept.Block, Is.SameAs(block));
            Assert.That(kept.Index, Is.EqualTo(1));
            Assert.That(kept.TransactionsInBlock, Is.SameAs(transactionsInBlock));

            for (int i = 0; i < later.Length; i++)
            {
                Assert.That((laterArgs[i].Action, laterArgs[i].Reason), Is.EqualTo((later[i].Action, later[i].Reason)), $"later rejection {i}");
                Assert.That(laterArgs[i].Transaction, Is.SameAs(later[i].Tx), $"later rejection {i}");
                Assert.That(laterArgs[i].Block, Is.SameAs(laterBlock), $"later rejection {i}");
            }

            Assert.That(laterArgs[0], Is.Not.SameAs(kept));
            Assert.That(laterArgs.Append(kept).Distinct(ReferenceEqualityComparer.Instance).Count(), Is.EqualTo(later.Length + 1),
                "every rejection is an object of its own");
        }
    }

    /// <summary>Two threads share one picker and each rejects its own candidates at the execution-gas check, with numbers
    /// and a considered set of its own; every result, read only after both finish, belongs to the call that returned it.</summary>
    [Test]
    public void Rejections_from_two_threads_on_one_picker_each_keep_their_own_candidate()
    {
        const int callsPerThread = 1_000;
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);

        // Gas remaining stays between the base cost of a transaction and its gas limit, so each call is skipped for gas.
        (Transaction Tx, HashSet<Transaction> TransactionsInBlock, ulong FirstGasRemaining)[] threads =
        [
            (Tx(), [], 30_000),
            (Tx(nonce: AccountNonce + 1), [Tx(nonce: AccountNonce + 20)], 60_000),
        ];
        BlockProcessor.AddingTxEventArgs[][] results = threads.Select(static _ => new BlockProcessor.AddingTxEventArgs[callsPerThread]).ToArray();
        using Barrier start = new(threads.Length);

        Task[] workers = threads.Select((thread, t) => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < callsPerThread; i++)
            {
                results[t][i] = picker.CanAddTransaction(block, thread.Tx, thread.TransactionsInBlock, sender.State,
                    BlockGasLimit - (thread.FirstGasRemaining + (ulong)i), 0);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(workers);

        List<string> wrong = [];
        for (int t = 0; t < threads.Length; t++)
        {
            for (int i = 0; i < callsPerThread; i++)
            {
                BlockProcessor.AddingTxEventArgs args = results[t][i];
                bool own = args.Action == BlockProcessor.TxAction.Skip
                    && args.Reason == ExecutionGasReason(threads[t].FirstGasRemaining + (ulong)i)
                    && ReferenceEquals(args.Transaction, threads[t].Tx)
                    && ReferenceEquals(args.TransactionsInBlock, threads[t].TransactionsInBlock)
                    && args.Index == threads[t].TransactionsInBlock.Count
                    && ReferenceEquals(args.Block, block);
                if (!own) wrong.Add($"thread {t} call {i}: {args.Action} \"{args.Reason}\" index {args.Index}");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrong, Is.Empty);
            Assert.That(results.SelectMany(static r => r).Distinct(ReferenceEqualityComparer.Instance).Count(),
                Is.EqualTo(threads.Length * callsPerThread), "every call returns an object of its own");
        }
    }

    /// <summary>The event raises only added candidates, with the args the call returns; later calls leave the args a
    /// subscriber kept alone.</summary>
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
                "later calls must not rewrite args a subscriber kept");
        }
    }

    /// <summary>A subscriber such as AuRa's permission check vetoes a candidate by setting <c>Skip</c> on the args it is
    /// raised with; the caller gets that action and reason on that same object.</summary>
    [Test]
    public void A_subscriber_s_veto_reaches_the_caller_on_the_args_it_was_raised_with()
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(Osaka.Instance);
        BlockProcessor.AddingTxEventArgs? raised = null;
        picker.AddingTransaction += (_, e) =>
        {
            raised = e;
            e.Set(BlockProcessor.TxAction.Skip, "not permitted");
        };

        Transaction candidate = Tx();
        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(block, candidate, new HashSet<Transaction>(), sender.State, 0, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(args, Is.SameAs(raised));
            Assert.That((args.Action, args.Reason, args.Transaction), Is.EqualTo((BlockProcessor.TxAction.Skip, "not permitted", candidate)));
        }
    }

    /// <summary>A candidate skipped at a check whose reason is built from numbers allocates its own args object and nothing
    /// more: the reason string is built on the first read of <see cref="BlockProcessor.AddingTxEventArgs.Reason"/>, once.</summary>
    [TestCaseSource(nameof(DeferredSkips))]
    public void A_deferred_skip_allocates_only_its_args_until_its_reason_is_read(IReleaseSpec spec, ulong nonce,
        ulong cumulativeExecutionGas, ulong cumulativeStateGas, string expectedReason)
    {
        // The args object: AddingTxEventArgs is 56 B on 64-bit and the deferred reason's kind and two numbers add up to
        // 24 B more, 80 B. The shortest of these reasons, "Invalid nonce - expected 3" (26 chars), is an 80 B string and
        // the gas ones are 136-144 B, so formatting any of them during the call would break this bound.
        const long argsBound = 96;
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(spec);
        Transaction tx = Tx(nonce);
        HashSet<Transaction> transactionsInBlock = [];

        BlockProcessor.AddingTxEventArgs Skip() =>
            picker.CanAddTransaction(block, tx, transactionsInBlock, sender.State, cumulativeExecutionGas, cumulativeStateGas);

        // Warm both paths up (JIT, static initialisation, the pooled buffer of the interpolation) outside the measurement.
        for (int i = 0; i < 10; i++)
        {
            Assert.That(Skip().Reason, Is.EqualTo(expectedReason));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        BlockProcessor.AddingTxEventArgs args = Skip();
        long callAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        string reason = args.Reason;
        long firstReadAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        string secondRead = args.Reason;
        long secondReadAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Out.WriteLine($"skip call {callAllocated} B, first Reason read {firstReadAllocated} B, second {secondReadAllocated} B");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(args.Action, Is.EqualTo(BlockProcessor.TxAction.Skip));
            Assert.That(callAllocated, Is.GreaterThan(0).And.LessThanOrEqualTo(argsBound), "a fresh args object and no reason string");
            Assert.That(firstReadAllocated, Is.GreaterThanOrEqualTo(2L * reason.Length), "the reason is first built when it is read");
            Assert.That(reason, Is.EqualTo(expectedReason));
            Assert.That(secondReadAllocated, Is.Zero, "and only once");
            Assert.That(secondRead, Is.SameAs(reason));
        }
    }

    /// <summary><see cref="BlockProcessor.AddingTxEventArgs.Set"/> on the args of a deferred skip replaces their action and
    /// reason, both before the deferred reason was ever read and after it was.</summary>
    [TestCaseSource(nameof(DeferredSkips))]
    public void Set_replaces_a_deferred_reason_whether_or_not_it_was_read(IReleaseSpec spec, ulong nonce,
        ulong cumulativeExecutionGas, ulong cumulativeStateGas, string expectedReason)
    {
        using FundedSender sender = new(1.Ether);
        Block block = BlockWithoutBaseFee();
        BlockProcessor.BlockProductionTransactionPicker picker = CreatePicker(spec);

        BlockProcessor.AddingTxEventArgs Skip() =>
            picker.CanAddTransaction(block, Tx(nonce), new HashSet<Transaction>(), sender.State, cumulativeExecutionGas, cumulativeStateGas);

        BlockProcessor.AddingTxEventArgs unread = Skip();
        BlockProcessor.AddingTxEventArgs setUnread = unread.Set(BlockProcessor.TxAction.Stop, "replacement");

        BlockProcessor.AddingTxEventArgs read = Skip();
        string readBeforeSet = read.Reason;
        read.Set(BlockProcessor.TxAction.Stop, "replacement after a read");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setUnread, Is.SameAs(unread));
            Assert.That((unread.Action, unread.Reason), Is.EqualTo((BlockProcessor.TxAction.Stop, "replacement")),
                "Set before any Reason read");
            Assert.That(readBeforeSet, Is.EqualTo(expectedReason));
            Assert.That((read.Action, read.Reason), Is.EqualTo((BlockProcessor.TxAction.Stop, "replacement after a read")),
                "Set after a Reason read");
        }
    }
}
