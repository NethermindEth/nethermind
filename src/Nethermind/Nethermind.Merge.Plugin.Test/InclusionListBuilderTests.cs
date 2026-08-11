// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class InclusionListBuilderTests
{
    private static Transaction TxOfSize(int payloadBytes, int nonce = 0)
    {
        byte[] data = new byte[payloadBytes];
        return Build.A.Transaction
            .WithNonce((ulong)nonce)
            .WithTo(TestItem.AddressA)
            .WithData(data)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
    }

    [Test]
    public void Empty_pool_yields_empty_inclusion_list()
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([]);
        InclusionListBuilder builder = new(pool);

        Assert.That(builder.GetInclusionList(), Is.Empty);
    }

    [Test]
    public void Caps_at_max_bytes_per_inclusion_list()
    {
        // 100 ~150-byte txs deliberately exceeds 8 KiB to force the cap.
        Transaction[] txs = Enumerable.Range(0, 100).Select(i => TxOfSize(100, i)).ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(txs);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        int totalBytes = il.Sum(t => t.Count);
        Assert.That(totalBytes, Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
        Assert.That(il, Is.Not.Empty);
    }

    [Test]
    public void Skips_txs_that_would_overflow_but_keeps_smaller_ones_that_fit()
    {
        Transaction huge = TxOfSize(8000, 0);
        Transaction tiny = TxOfSize(50, 1);
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([huge, tiny]);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        int totalBytes = il.Sum(t => t.Count);
        Assert.That(totalBytes, Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
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
        Transaction normalTx = TxOfSize(50, 1);
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([blobTx, normalTx]);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        Assert.That(il.Count, Is.EqualTo(1));
        RlpReader ctx = new(il[0].AsSpan());
        Transaction decoded = TxDecoder.Instance.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
        Assert.That(decoded.Hash, Is.EqualTo(normalTx.Hash));
    }

    [Test]
    public void Handles_mempool_larger_than_reservoir_capacity()
    {
        Transaction[] txs = Enumerable.Range(0, Eip7805Constants.MaxTransactionsPerInclusionList + 100)
            .Select(i => TxOfSize(0, i))
            .ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(txs);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        Assert.That(il.Count, Is.LessThanOrEqualTo(Eip7805Constants.MaxTransactionsPerInclusionList));
        Assert.That(il.Sum(t => t.Count), Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
    }

    // EIP-8369 includer VERIFY-budget fill. Each Profile-2 frame tx below costs `verifyGas` (the
    // self-verify prefix frame; the trailing execution frame is outside the prefix), so with
    // MAX_VERIFY_GAS_PER_IL = 2^20 exactly floor(2^20 / 300_000) = 3 of six such txs fit.
    private static Transaction ProfileTwoFrameTx(ulong verifyGas) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames =
        [
            new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, verifyGas, UInt256.Zero, default),
            new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, 21_000, UInt256.Zero, default),
        ],
        FrameSignatures = [],
    };

    // A frame tx whose single frame is a plain deploy has no recognized validation prefix, so it
    // classifies Outside and must be dropped before it can occupy a reservoir slot.
    private static Transaction OutsideFrameTx() => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = [new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, target: null, 50_000, UInt256.Zero, default)],
        FrameSignatures = [],
    };

    [Test]
    public void Outside_txs_do_not_consume_reservoir_slots()
    {
        // Far more Outside txs than the reservoir holds, ahead of a handful of eligible Profile-1 txs:
        // if Outside txs were sampled they would crowd the eligible ones out, under-filling the IL.
        Transaction[] outside = Enumerable.Range(0, Eip7805Constants.MaxTransactionsPerInclusionList * 8)
            .Select(_ => OutsideFrameTx()).ToArray();
        Transaction[] eligible = Enumerable.Range(0, 10).Select(i => TxOfSize(50, i)).ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([.. outside, .. eligible]);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        Assert.That(CountByType(il, TxType.Legacy), Is.EqualTo(10));
    }

    private static int CountByType(InclusionListBytes il, TxType type)
    {
        int count = 0;
        foreach (ArrayPoolList<byte> bytes in il)
        {
            RlpReader ctx = new(bytes.AsSpan());
            if (TxDecoder.Instance.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping).Type == type) count++;
        }
        return count;
    }

    [Test]
    public void Fills_verify_budget_across_profile_two_frame_txs()
    {
        Transaction[] txs = Enumerable.Range(0, 6).Select(_ => ProfileTwoFrameTx(300_000)).ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(txs);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        Assert.That(CountByType(il, TxType.FrameTx), Is.EqualTo(3));
    }

    [Test]
    public void Excludes_over_budget_frame_tx_but_keeps_profile_one()
    {
        Transaction overBudget = ProfileTwoFrameTx(Eip8369Constants.MaxVerifyGasPerTx + 1);
        Transaction profileOne = TxOfSize(50, 1);
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([overBudget, profileOne]);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountByType(il, TxType.FrameTx), Is.EqualTo(0));
            Assert.That(il.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void Profile_one_txs_are_not_metered_against_the_frame_budget()
    {
        Transaction[] profileOne = Enumerable.Range(0, 8).Select(i => TxOfSize(50, i)).ToArray();
        Transaction[] frames = Enumerable.Range(0, 6).Select(_ => ProfileTwoFrameTx(300_000)).ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([.. profileOne, .. frames]);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes il = builder.GetInclusionList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountByType(il, TxType.Legacy), Is.EqualTo(8));
            Assert.That(CountByType(il, TxType.FrameTx), Is.EqualTo(3));
        }
    }

    [Test]
    public void Returned_bytes_are_valid_RLP_decoding_back_to_originals()
    {
        Transaction[] txs = Enumerable.Range(0, 5).Select(i => TxOfSize(40, i)).ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(txs);
        InclusionListBuilder builder = new(pool);

        using InclusionListBytes ilBytes = builder.GetInclusionList();

        // Round-trip: every yielded byte buffer must decode to a pool-known tx hash.
        HashSet<Hash256> originals = [.. txs.Select(t => t.Hash!)];
        foreach (ArrayPoolList<byte> bytes in ilBytes)
        {
            RlpReader ctx = new(bytes.AsSpan());
            Transaction decoded = TxDecoder.Instance.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
            Assert.That(originals, Does.Contain(decoded.Hash!));
        }
    }
}
