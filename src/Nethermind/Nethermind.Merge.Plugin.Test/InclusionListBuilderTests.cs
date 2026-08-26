// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
    private static InclusionListBuilder BuildBuilder(ITxPool pool, UInt256 baseFee = default, (Address Sender, ulong Nonce)[]? accountNonces = null)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithBaseFeePerGas(baseFee).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Frontier.Instance);
        IReadOnlyStateProvider headState = Substitute.For<IReadOnlyStateProvider>();
        foreach ((Address sender, ulong nonce) in accountNonces ?? []) headState.GetNonce(sender).Returns(nonce);
        return new InclusionListBuilder(pool, blockTree, specProvider, headState);
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

    // The pool admits a whole bucket on its first entry's readiness alone, so a frame transaction at the
    // head is what vouched for the run; once dropped, what remains need not sit at the next account nonce.
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

    // The pool checks CanPayBaseFee on the bucket's first entry alone, so a transaction behind a paying head can
    // sit below the next block's base fee. The validator excuses omitting it, so listing it burns the cap.
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

    // Worst case for the state read: every sender's bucket is headed by a keyed frame transaction, so the cheap
    // anchor comparison never decides. The reservoir must bound the reads whatever the pool size.
    private static (ITxPool Pool, IReadOnlyStateProvider HeadState, InclusionListBuilder Builder) KeyedHeadSetup(int senderCount)
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
        return (pool, headState, new InclusionListBuilder(pool, blockTree, specProvider, headState));
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
        (_, IReadOnlyStateProvider headState, InclusionListBuilder builder) = KeyedHeadSetup(senderCount);

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
        (_, IReadOnlyStateProvider headState, InclusionListBuilder builder) = KeyedHeadSetup(senderCount);

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
        Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
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

    [Test]
    public void Handles_more_senders_than_the_sample_capacity()
    {
        Transaction[] txs = [.. Enumerable.Range(0, TestItem.PrivateKeys.Length)
            .SelectMany(i => new[] { TxOfSize(0, 0, TestItem.PrivateKeys[i]), TxOfSize(0, 1, TestItem.PrivateKeys[i]) })];

        using InclusionListBytes il = BuildBuilder(PoolOf(txs)).GetInclusionList();

        Assert.That(il.Count, Is.LessThanOrEqualTo(Eip7805Constants.MaxTransactionsPerInclusionList));
        Assert.That(il.Sum(t => t.Count), Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
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
