// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class InclusionListBuilderTests
{
    private static Transaction TxOfSize(int payloadBytes, int nonce = 0, PrivateKey? sender = null, uint maxFeePerGas = 1)
    {
        byte[] data = new byte[payloadBytes];
        return Build.A.Transaction
            .WithNonce((ulong)nonce)
            .WithTo(TestItem.AddressA)
            .WithData(data)
            // Legacy txs price off GasPrice, which is what MaxFeePerGas reads back for them.
            .WithGasPrice(maxFeePerGas)
            .SignedAndResolved(sender ?? TestItem.PrivateKeyA)
            .TestObject;
    }

    // Frontier leaves the parent's base fee unchanged, so the head header fixes the fee the builder asks for.
    // The age tier defaults to MergeConfig's, so an unnamed tier is the shipped one: off.
    private static InclusionListBuilder BuildBuilder(ITxPool pool, UInt256 baseFee = default, double oldestShare = 0, int oldestCount = 200)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithBaseFeePerGas(baseFee).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Frontier.Instance);
        MergeConfig mergeConfig = new()
        {
            InclusionListOldestSenderShare = oldestShare,
            InclusionListOldestSenderCount = oldestCount
        };
        return new InclusionListBuilder(pool, blockTree, specProvider, mergeConfig);
    }

    /// <summary>A pool whose ready buckets are the given transactions, grouped by sender and nonce-ordered.</summary>
    private static ITxPool PoolOf(params Transaction[] readyTxs)
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = readyTxs
            .GroupBy(tx => new AddressAsKey(tx.SenderAddress!))
            .ToDictionary(g => g.Key, g => g.OrderBy(tx => tx.Nonce).ToArray());
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactionsBySender(Arg.Any<bool>(), Arg.Any<UInt256>()).Returns(bySender);
        return pool;
    }

    private static Transaction Decode(ArrayPoolList<byte> bytes)
    {
        RlpReader ctx = new(bytes.AsSpan());
        return TxDecoder.Instance.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
    }

    [Test]
    public void Empty_pool_yields_empty_inclusion_list() =>
        Assert.That(BuildBuilder(PoolOf()).GetInclusionList(), Is.Empty);

    [Test]
    public void Caps_at_max_bytes_per_inclusion_list()
    {
        // 100 ~150-byte txs deliberately exceeds 8 KiB to force the cap.
        Transaction[] txs = [.. Enumerable.Range(0, 100).Select(i => TxOfSize(100, i))];

        using InclusionListBytes il = BuildBuilder(PoolOf(txs)).GetInclusionList();

        Assert.That(il.Sum(t => t.Count), Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
        Assert.That(il, Is.Not.Empty);
    }

    [Test]
    public void Skips_txs_that_would_overflow_but_keeps_smaller_ones_that_fit()
    {
        using InclusionListBytes il = BuildBuilder(PoolOf(TxOfSize(8000), TxOfSize(50, 1))).GetInclusionList();

        Assert.That(il.Sum(t => t.Count), Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
    }

    // Only what the next block could append belongs in the list, so the pool must do the readiness and
    // affordability filtering against the fee that block will charge.
    [Test]
    public void Requests_only_transactions_ready_at_the_next_base_fee()
    {
        ITxPool pool = PoolOf();

        BuildBuilder(pool, baseFee: 17).GetInclusionList().Dispose();

        pool.Received().GetPendingTransactionsBySender(true, (UInt256)17);
    }

    // The named parent, not the head, fixes the fee the candidates are filtered against.
    [Test]
    public void Requests_transactions_ready_at_the_named_parents_base_fee()
    {
        ITxPool pool = PoolOf();
        BlockHeader parent = Build.A.BlockHeader.WithBaseFee(23).TestObject;

        BuildBuilder(pool, baseFee: 17).GetInclusionList(parent).Dispose();

        pool.Received().GetPendingTransactionsBySender(true, (UInt256)23);
    }

    // A later nonce only becomes appendable once the block includes the earlier one, so a gap ends the run.
    [Test]
    public void Stops_a_sender_run_at_a_nonce_gap()
    {
        Transaction nonce0 = TxOfSize(50, 0);
        Transaction nonce1 = TxOfSize(50, 1);
        Transaction nonce3 = TxOfSize(50, 3);

        using InclusionListBytes il = BuildBuilder(PoolOf(nonce0, nonce1, nonce3)).GetInclusionList();

        Assert.That(il.Count, Is.EqualTo(2));
        Assert.That(il.Select(b => Decode(b).Hash), Is.EqualTo(new[] { nonce0.Hash, nonce1.Hash }));
    }

    // A run also ends at the first transaction the next block's base fee prices out: that one could not be
    // appended, so nothing behind it could either, and listing them only spends the byte cap.
    [TestCase(1u, 1)]
    [TestCase(10u, 3)]
    [TestCase(11u, 3)]
    public void Stops_a_sender_run_at_a_transaction_the_next_base_fee_prices_out(uint middleMaxFeePerGas, int expectedCount)
    {
        Transaction nonce0 = TxOfSize(50, 0, maxFeePerGas: 100);
        Transaction nonce1 = TxOfSize(50, 1, maxFeePerGas: middleMaxFeePerGas);
        Transaction nonce2 = TxOfSize(50, 2, maxFeePerGas: 100);

        using InclusionListBytes il = BuildBuilder(PoolOf(nonce0, nonce1, nonce2), baseFee: 10).GetInclusionList();

        Assert.That(il.Count, Is.EqualTo(expectedCount));
    }

    // One sender's run being priced out must not cost the other drawn senders their place in the list.
    [Test]
    public void Keeps_other_senders_when_one_run_is_priced_out()
    {
        Transaction pricedOut = TxOfSize(50, 0, TestItem.PrivateKeyA, maxFeePerGas: 1);
        Transaction payable = TxOfSize(50, 0, TestItem.PrivateKeyB, maxFeePerGas: 100);

        using InclusionListBytes il = BuildBuilder(PoolOf(pricedOut, payable), baseFee: 10).GetInclusionList();

        Assert.That(il.Select(b => Decode(b).Hash), Is.EqualTo(new[] { payable.Hash }));
    }

    // Every drawn sender must reach the list before any one of them gets a second nonce, or an account
    // with a long ready run spends the byte cap on itself and censors the rest.
    [Test]
    public void Interleaves_sender_runs_rather_than_draining_each_in_turn()
    {
        const int senderCount = 20;
        // Long enough that one sender's run alone would overrun the byte cap.
        const int runLength = 100;
        Transaction[] txs = [.. Enumerable.Range(0, senderCount)
            .SelectMany(s => Enumerable.Range(0, runLength).Select(n => TxOfSize(50, n, TestItem.PrivateKeys[s])))];
        Dictionary<Hash256, Address> senderByHash = txs.ToDictionary(tx => tx.Hash!, tx => tx.SenderAddress!);

        using InclusionListBytes il = BuildBuilder(PoolOf(txs)).GetInclusionList();

        Assert.That(il.Select(b => senderByHash[Decode(b).Hash!]).Distinct().Count(), Is.EqualTo(senderCount));
    }

    // Both draws answer to the transport's caps, whether or not part of one is reserved by age.
    [Test]
    public void Handles_more_senders_than_the_sample_capacity([Values(0.0, 0.5)] double oldestShare)
    {
        Transaction[] txs = [.. Enumerable.Range(0, TestItem.PrivateKeys.Length)
            .SelectMany(i => new[] { TxOfSize(0, 0, TestItem.PrivateKeys[i]), TxOfSize(0, 1, TestItem.PrivateKeys[i]) })];

        using InclusionListBytes il = BuildBuilder(PoolOf(txs), oldestShare: oldestShare).GetInclusionList();

        Assert.That(il.Count, Is.LessThanOrEqualTo(Eip7805Constants.MaxTransactionsPerInclusionList));
        Assert.That(il.Sum(t => t.Count), Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
    }

    /// <summary>A one-transaction sender, its pool index standing in for how long it has been pending.</summary>
    private static Transaction SenderTx(int index, ulong poolIndex)
    {
        // A nonce per sender keeps every transaction distinct and every appendable run one entry long.
        Transaction tx = TxOfSize(50, index);
        // The pool holds far more senders than there are test keys, and only the address is read here.
        tx.SenderAddress = Address.FromNumber((UInt256)(index + 1));
        tx.PoolIndex = poolIndex;
        return tx;
    }

    private const int TierPoolSenders = 800;
    private const int TierCohortSize = 160;

    /// <summary>The share of the listed transactions that came from the oldest cohort, over many lists.</summary>
    /// <param name="step">Pool indices apart between consecutive senders; <c>0</c> stamps them all alike.</param>
    private static double ListedOldestShare(double oldestShare, int oldestCount, ulong firstPoolIndex, ulong step)
    {
        Transaction[] txs = new Transaction[TierPoolSenders];
        HashSet<Hash256> cohort = [];
        for (int i = 0; i < TierPoolSenders; i++)
        {
            txs[i] = SenderTx(i, firstPoolIndex + (ulong)i * step);
            if (i < TierCohortSize) cohort.Add(txs[i].Hash!);
        }

        InclusionListBuilder builder = BuildBuilder(PoolOf(txs), oldestShare: oldestShare, oldestCount: oldestCount);
        int listed = 0;
        int fromCohort = 0;
        // One list is a single draw; a share only means anything across many of them.
        for (int round = 0; round < 60; round++)
        {
            using InclusionListBytes il = builder.GetInclusionList();
            foreach (ArrayPoolList<byte> bytes in il)
            {
                listed++;
                if (cohort.Contains(Decode(bytes).Hash!)) fromCohort++;
            }
        }

        return (double)fromCohort / listed;
    }

    // Off, the draw is uniform and the oldest cohort is listed at its share of the pool, 160 of 800. On, the
    // share reserved out of the 256-sender draw is the share of the list the cohort gets, since the byte cap
    // keeps a random prefix of that draw. Both are what the model of a censored transaction's odds rests on.
    [TestCase(0.0, TierCohortSize, 0ul, 1ul, 0.20)]
    [TestCase(0.25, TierCohortSize, 0ul, 1ul, 0.25)]
    [TestCase(0.5, TierCohortSize, 0ul, 1ul, 0.50)]
    // A share under the cohort's own 0.20 of the pool is a floor that does not bind, not a ceiling that demotes
    // it: reserving a sliver of the draw for the oldest senders must never list fewer of them than drawing
    // uniformly would have. Reserving by share alone inverts here, worst at the smallest non-zero shares.
    [TestCase(0.02, TierCohortSize, 0ul, 1ul, 0.20)]
    [TestCase(0.05, TierCohortSize, 0ul, 1ul, 0.20)]
    // The cohort is the pool's oldest by rank, not by an index threshold, so the same share holds wherever the
    // pool's sequence happens to start — which is what survives it restarting with the process.
    [TestCase(0.5, TierCohortSize, ulong.MaxValue - 10_000ul, 1ul, 0.50)]
    // Nothing to rank by leaves the whole pool tied for oldest, which is the uniform draw again.
    [TestCase(0.5, TierCohortSize, 0ul, 0ul, 0.20)]
    // So does a cohort as wide as the pool: there is nothing left to reserve the draw against.
    [TestCase(0.5, TierPoolSenders, 0ul, 1ul, 0.20)]
    public void Reserved_share_is_a_floor_under_what_the_oldest_cohort_gets(double oldestShare, int oldestCount, ulong firstPoolIndex, ulong step, double expected) =>
        Assert.That(ListedOldestShare(oldestShare, oldestCount, firstPoolIndex, step), Is.EqualTo(expected).Within(0.04));

    /// <summary>Entries one draw fits in the byte cap, asserting no sender reached the list twice.</summary>
    private static int ListedCount(Transaction[] txs, double oldestShare, int oldestCount)
    {
        using InclusionListBytes il = BuildBuilder(PoolOf(txs), oldestShare: oldestShare, oldestCount: oldestCount).GetInclusionList();

        Assert.That(il.Select(b => Decode(b).Hash).Distinct().Count(), Is.EqualTo(il.Count),
            "a sender drawn into both tiers would be listed twice");
        return il.Count;
    }

    // Neither tier running short may cost the list entries: each spends what the other leaves.
    [TestCase(0.05, 28)] // a cohort wider than its reserved draw, with too few senders behind it to fill the rest
    [TestCase(0.5, 5)]   // a cohort too narrow to fill its reserved draw
    public void A_short_tier_does_not_shrink_the_draw(double oldestShare, int oldestCount)
    {
        const int senderCount = 30;
        Transaction[] txs = new Transaction[senderCount];
        for (int i = 0; i < txs.Length; i++) txs[i] = SenderTx(i, (ulong)i);

        // The premise: this pool fits the byte cap whole, so a narrowed draw would cost the list entries.
        Assert.That(ListedCount(txs, 0, oldestCount), Is.EqualTo(senderCount));
        Assert.That(ListedCount(txs, oldestShare, oldestCount), Is.EqualTo(senderCount));
    }

    [Test]
    public void Returned_bytes_are_valid_RLP_decoding_back_to_originals()
    {
        Transaction[] txs = [.. Enumerable.Range(0, 5).Select(i => TxOfSize(40, i))];

        using InclusionListBytes ilBytes = BuildBuilder(PoolOf(txs)).GetInclusionList();

        HashSet<Hash256> originals = [.. txs.Select(t => t.Hash!)];
        foreach (ArrayPoolList<byte> bytes in ilBytes)
        {
            Assert.That(originals, Does.Contain(Decode(bytes).Hash!));
        }
    }
}
