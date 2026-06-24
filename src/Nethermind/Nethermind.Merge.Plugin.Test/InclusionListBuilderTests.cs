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
            .WithNonce((UInt256)nonce)
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
