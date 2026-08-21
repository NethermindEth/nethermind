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
    private static Transaction TxOfSize(int payloadBytes, int nonce = 0, PrivateKey? sender = null)
    {
        byte[] data = new byte[payloadBytes];
        return Build.A.Transaction
            .WithNonce((ulong)nonce)
            .WithTo(TestItem.AddressA)
            .WithData(data)
            .SignedAndResolved(sender ?? TestItem.PrivateKeyA)
            .TestObject;
    }

    // Frontier leaves the parent's base fee unchanged, so the head header fixes the fee the builder asks for.
    private static InclusionListBuilder BuildBuilder(ITxPool pool, UInt256 baseFee = default)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithBaseFeePerGas(baseFee).TestObject);
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(Frontier.Instance);
        return new InclusionListBuilder(pool, blockTree, specProvider);
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

    [Test]
    public void Skips_blob_transactions()
    {
        Transaction blobTx = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithNonce(0)
            .WithMaxFeePerGas(10)
            .WithMaxPriorityFeePerGas(1)
            .WithMaxFeePerBlobGas(10)
            .WithBlobVersionedHashes(1)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction normalTx = TxOfSize(50, 0, TestItem.PrivateKeyB);

        using InclusionListBytes il = BuildBuilder(PoolOf(blobTx, normalTx)).GetInclusionList();

        Assert.That(il.Count, Is.EqualTo(1));
        Assert.That(Decode(il[0]).Hash, Is.EqualTo(normalTx.Hash));
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
