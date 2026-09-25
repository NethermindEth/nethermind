// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
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
    private static Transaction TxOfSize(int payloadBytes, int nonce = 0, PrivateKey? sender = null, UInt256? maxFeePerGas = null)
    {
        byte[] data = new byte[payloadBytes];
        return Build.A.Transaction
            .WithNonce((ulong)nonce)
            .WithTo(TestItem.AddressA)
            .WithData(data)
            // Legacy type, so MaxFeePerGas reads back as GasPrice.
            .WithGasPrice(maxFeePerGas ?? UInt256.Zero)
            .SignedAndResolved(sender ?? TestItem.PrivateKeyA)
            .TestObject;
    }

    // Frontier leaves the parent's base fee unchanged, so the head header fixes the fee the builder asks for.
    // The age tier defaults to MergeConfig's, so an unnamed tier is the shipped one: off.
    private static InclusionListBuilder BuildBuilder(ITxPool pool, UInt256 baseFee = default,
        (Address Sender, ulong Nonce)[]? accountNonces = null, double oldestShare = 0, int oldestCount = 200)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithBaseFeePerGas(baseFee).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Frontier.Instance);
        IReadOnlyStateProvider headState = Substitute.For<IReadOnlyStateProvider>();
        foreach ((Address sender, ulong nonce) in accountNonces ?? []) headState.GetNonce(sender).Returns(nonce);
        MergeConfig mergeConfig = new()
        {
            InclusionListOldestSenderShare = oldestShare,
            InclusionListOldestSenderCount = oldestCount
        };
        return new InclusionListBuilder(pool, blockTree, specProvider, headState, mergeConfig);
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

    // ITxPool's contract doesn't guarantee GetPendingTransactionsBySender omits empty buckets.
    [Test]
    public void Tolerates_an_empty_bucket_from_the_pool([Values(0.0, 0.5)] double oldestShare)
    {
        Dictionary<AddressAsKey, Transaction[]> bySender = new()
        {
            [new AddressAsKey(TestItem.AddressA)] = [],
            [new AddressAsKey(TestItem.AddressB)] = [TxOfSize(50, 0, TestItem.PrivateKeyB)]
        };
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactionsBySender(Arg.Any<bool>(), Arg.Any<UInt256>()).Returns(bySender);

        using InclusionListBytes il = BuildBuilder(pool, oldestShare: oldestShare, oldestCount: 1).GetInclusionList();

        Assert.That(il.Count, Is.EqualTo(1));
    }

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

    // Listing a frame transaction spends the byte cap for nothing, and its per-key nonce would break the
    // gapless-offset test for everything behind it — hence both assertions.
    [Test]
    public void Frame_transactions_are_kept_out_and_do_not_break_the_sender_run()
    {
        Transaction frameTx = FrameTx(TestItem.AddressA, nonce: 0);
        Transaction nonce0 = TxOfSize(50, 0);
        Transaction nonce1 = TxOfSize(50, 1);

        using InclusionListBytes il = BuildBuilder(PoolOf(frameTx, nonce0, nonce1)).GetInclusionList();

        Assert.That(il.Select(b => Decode(b).Hash), Is.EqualTo(new[] { nonce0.Hash, nonce1.Hash }));
    }

    // The pool admits a bucket on any one entry being ready, so a frame transaction can be what vouched for
    // the run; once dropped, what remains need not sit at the next account nonce.
    [Test]
    public void Drops_a_sender_run_the_removed_frame_transaction_was_vouching_for()
    {
        // A: the keyed head names a per-key sequence, and the account puts its next nonce at 100, not 101.
        Transaction keyedHead = FrameTx(TestItem.AddressA, nonce: 0, nonceKeys: [1]);
        Transaction behindKeyed = TxOfSize(50, 101, TestItem.PrivateKeyA);
        // B: the frame holds the account's next nonce, so the transaction behind it is a nonce ahead.
        Transaction accountHead = FrameTx(TestItem.AddressB, nonce: 0);
        Transaction behindAccount = TxOfSize(50, 1, TestItem.PrivateKeyB);

        using InclusionListBytes il = BuildBuilder(
            PoolOf(keyedHead, behindKeyed, accountHead, behindAccount),
            accountNonces: [(TestItem.AddressA, 100)]).GetInclusionList();

        Assert.That(il, Is.Empty);
    }

    // Keyed sequences start at 0 per key, so a keyed frame transaction heads the bucket of any sender with a
    // non-zero account nonce. Dropping those wholesale would cost ordinary transactions their coverage.
    [Test]
    public void Keeps_the_ordinary_run_behind_a_keyed_frame_transaction_the_account_says_is_next()
    {
        Transaction keyedHead = FrameTx(TestItem.AddressA, nonce: 0, nonceKeys: [1]);
        Transaction atAccountNonce = TxOfSize(50, 100, TestItem.PrivateKeyA);
        Transaction next = TxOfSize(50, 101, TestItem.PrivateKeyA);

        using InclusionListBytes il = BuildBuilder(
            PoolOf(keyedHead, atAccountNonce, next),
            accountNonces: [(TestItem.AddressA, 100)]).GetInclusionList();

        Assert.That(il.Select(b => Decode(b).Hash), Is.EqualTo(new[] { atAccountNonce.Hash, next.Hash }));
    }

    private static IEnumerable<TestCaseData> BucketsAdmittedByANonFrontEntry()
    {
        // The pool reads a bucket entry under the account nonce as spent rather than blocking, so it admits this
        // bucket on nonce 5 alone while nonce 4 heads it. Only nonce 5 is appendable.
        Transaction spent = TxOfSize(50, 4);
        Transaction atAccountNonce = TxOfSize(50, 5);
        yield return new TestCaseData(new[] { spent, atAccountNonce }, 5UL, new[] { atAccountNonce })
            .SetName("Skips_a_spent_nonce_heading_the_bucket");

        // A keyed frame transaction is judged on its own sequence however the account-nonce entries sit, so it can
        // admit a bucket whose only ordinary entry is four nonces ahead. Stripping it leaves nothing appendable.
        Transaction gapped = TxOfSize(50, 9);
        Transaction keyedFrame = FrameTx(TestItem.AddressA, nonce: 100, nonceKeys: [1]);
        yield return new TestCaseData(new[] { gapped, keyedFrame }, 5UL, Array.Empty<Transaction>())
            .SetName("Drops_a_gapped_run_admitted_by_a_keyed_frame_transaction");
    }

    // The bucket's lowest entry is not the account's next nonce: the pool admits a bucket on any one entry being
    // ready, so only a state read names the anchor.
    [TestCaseSource(nameof(BucketsAdmittedByANonFrontEntry))]
    public void Anchors_a_sender_run_at_the_account_nonce_not_at_the_bucket_front(
        Transaction[] bucket, ulong accountNonce, Transaction[] expected)
    {
        using InclusionListBytes il = BuildBuilder(
            PoolOf(bucket),
            accountNonces: [(TestItem.AddressA, accountNonce)]).GetInclusionList();

        Assert.That(il.Select(b => Decode(b).Hash), Is.EqualTo(expected.Select(tx => tx.Hash)));
    }

    // The pool admits a bucket once one entry both pays and is nonce-ready, so a transaction behind a paying head
    // can sit below the next block's base fee. The validator excuses omitting it, so listing it burns the cap.
    [Test]
    public void Truncates_a_sender_run_at_the_first_transaction_below_the_next_base_fee()
    {
        Transaction paying = TxOfSize(50, 0, maxFeePerGas: 100);
        Transaction belowBaseFee = TxOfSize(50, 1, maxFeePerGas: 5);
        Transaction behindIt = TxOfSize(50, 2, maxFeePerGas: 100);

        using InclusionListBytes il = BuildBuilder(PoolOf(paying, belowBaseFee, behindIt), baseFee: 10).GetInclusionList();

        Assert.That(il.Select(b => Decode(b).Hash), Is.EqualTo(new[] { paying.Hash }));
    }

    // The fee twin of the promotion hole: the keyed frame head is what satisfied CanPayBaseFee, so the ordinary
    // transaction promoted behind it has never been priced against the next block at all.
    [Test]
    public void Drops_an_ordinary_transaction_below_the_next_base_fee_promoted_by_a_keyed_frame_head()
    {
        Transaction keyedHead = FrameTx(TestItem.AddressA, nonce: 0, nonceKeys: [1]);
        Transaction belowBaseFee = TxOfSize(50, 100, maxFeePerGas: 5);

        using InclusionListBytes il = BuildBuilder(
            PoolOf(keyedHead, belowBaseFee),
            baseFee: 10,
            accountNonces: [(TestItem.AddressA, 100)]).GetInclusionList();

        Assert.That(il, Is.Empty);
    }

    // Every drawn sender costs one account-nonce read, so the reservoir must bound the reads whatever the pool size.
    // The frame transaction no longer decides whether that read happens; it keeps each bucket on the copying
    // branch of WithoutFrameTxs, which is what still makes this the worst case per drawn sender.
    private static (ITxPool Pool, IReadOnlyStateProvider HeadState, InclusionListBuilder Builder) WorstCaseReadSetup(int senderCount)
    {
        Transaction[] txs = new Transaction[senderCount * 2];
        for (int i = 0; i < senderCount; i++)
        {
            Address sender = SenderAt(i);
            txs[i * 2] = FrameTx(sender, nonce: 0, nonceKeys: [1]);
            // A nonce the account is not at, so every drawn sender is dropped after its read and nothing encodes.
            txs[i * 2 + 1] = Build.A.Transaction.WithNonce(500).WithTo(TestItem.AddressA).WithSenderAddress(sender).TestObject;
        }

        ITxPool pool = PoolOf(txs);
        IReadOnlyStateProvider headState = Substitute.For<IReadOnlyStateProvider>();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Frontier.Instance);
        return (pool, headState, new InclusionListBuilder(pool, blockTree, specProvider, headState, new MergeConfig()));
    }

    private static Address SenderAt(int index)
    {
        byte[] bytes = new byte[Address.Size];
        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(bytes, Address.Size - sizeof(int), sizeof(int)), index + 1);
        return new Address(bytes);
    }

    // A default pool holds 2,048 transactions, so reading state per sender rather than per drawn sender would
    // cost roughly 1,024 trie lookups per request. Bound it at the reservoir instead.
    [Test]
    public void State_reads_are_bounded_by_the_sender_sample_capacity()
    {
        const int senderCount = 1024;
        (_, IReadOnlyStateProvider headState, InclusionListBuilder builder) = WorstCaseReadSetup(senderCount);

        builder.GetInclusionList().Dispose();

        int reads = headState.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAccountStateProvider.GetNonce));
        Assert.That(reads, Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList / 32),
            "a larger pool must not buy more trie reads than the reservoir draws senders");
    }

    /// <summary>Reports the worst-case cost of one inclusion-list request against a full default-size pool.</summary>
    [Explicit("measurement harness")]
    [Test]
    public void Measure_worst_case_inclusion_list_cost()
    {
        const int senderCount = 1024;
        const int iterations = 50;
        (_, IReadOnlyStateProvider headState, InclusionListBuilder builder) = WorstCaseReadSetup(senderCount);

        builder.GetInclusionList().Dispose();  // warm
        long before = headState.ReceivedCalls().Count();
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) builder.GetInclusionList().Dispose();
        sw.Stop();

        long reads = headState.ReceivedCalls().Count() - before;
        TestContext.Out.WriteLine($"senders={senderCount} iterations={iterations}");
        TestContext.Out.WriteLine($"per_request_ms={(double)sw.Elapsed.TotalMilliseconds / iterations:F3}");
        TestContext.Out.WriteLine($"per_request_state_reads={(double)reads / iterations:F1}");
    }

    private static Transaction FrameTx(Address sender, ulong nonce, UInt256[]? nonceKeys = null) => new()
    {
        Type = TxType.FrameTx,
        ChainId = TestBlockchainIds.ChainId,
        SenderAddress = sender,
        Nonce = nonce,
        NonceKeys = nonceKeys,
        Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
        FrameSignatures = [],
        GasLimit = 100_000,
        GasPrice = 1,
        DecodedMaxFeePerGas = 10,
    };

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

        using InclusionListBytes il = BuildBuilder(PoolOf(txs), oldestShare: oldestShare, oldestCount: 5).GetInclusionList();

        Assert.That(il.Count, Is.LessThanOrEqualTo(Eip7805Constants.MaxTransactionsPerInclusionList));
        Assert.That(il.Sum(t => t.Count), Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
    }

    /// <summary>A one-transaction sender, its pool index standing in for how long it has been pending.</summary>
    private static Transaction SenderTx(int index, ulong poolIndex, int payloadBytes = 50)
    {
        // A price per sender keeps every transaction distinct and every appendable run one entry long.
        Transaction tx = TxOfSize(payloadBytes, maxFeePerGas: (UInt256)(index + 1));
        // The pool holds far more senders than there are test keys, so each needs a distinct address.
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

    // A share worth less than one of the draw's slots must still reserve one: truncating it away would leave
    // the tier off, and the shipped default running, on a knob an operator is calibrating.
    [Test]
    public void Reserves_a_slot_for_a_share_below_one_slot()
    {
        Transaction[] txs = new Transaction[TierPoolSenders];
        txs[0] = SenderTx(0, 0);
        // Nothing behind the cohort can be listed, so the list is non-empty exactly when the cohort is drawn.
        for (int i = 1; i < txs.Length; i++) txs[i] = SenderTx(i, (ulong)i, Eip7805Constants.MaxBytesPerInclusionList);

        using InclusionListBytes il = BuildBuilder(PoolOf(txs), oldestShare: 0.003, oldestCount: 1).GetInclusionList();

        Assert.That(il.Select(b => Decode(b).Hash), Is.EqualTo(new[] { txs[0].Hash }));
    }

    // A misconfigured tier must fail loudly: clamping or truncating it hands back the shipped default.
    [TestCase(double.NaN, 200)]
    [TestCase(double.PositiveInfinity, 200)]
    [TestCase(-0.1, 200)]
    [TestCase(1.5, 200)]
    [TestCase(0.5, -1)]
    public void Rejects_a_tier_it_cannot_honour(double oldestShare, int oldestCount) =>
        Assert.That(() => BuildBuilder(PoolOf(), oldestShare: oldestShare, oldestCount: oldestCount),
            Throws.InstanceOf<InvalidConfigurationException>()
                .With.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(ExitCodes.ForbiddenOptionValue));

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
