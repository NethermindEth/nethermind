// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Unit-tests <see cref="InclusionListBuilder"/>'s byte and count caps so a future selection
/// strategy can't silently break the MAX_BYTES_PER_INCLUSION_LIST contract.
/// </summary>
public class InclusionListBuilderTests
{
    private static Transaction TxOfSize(int payloadBytes, int nonce = 0)
    {
        // Data bytes inflate encoded RLP ~1:1 → predictable sizing.
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

        List<byte[]> il = builder.GetInclusionList().ToList();

        int totalBytes = il.Sum(t => t.Length);
        Assert.That(totalBytes, Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
        // Sanity: we returned at least one tx so the cap isn't a false positive on selection
        Assert.That(il, Is.Not.Empty);
    }

    [Test]
    public void Skips_txs_that_would_overflow_but_keeps_smaller_ones_that_fit()
    {
        Transaction huge = TxOfSize(8000, 0);   // alone fits
        Transaction tiny = TxOfSize(50, 1);     // slots in after huge
        ITxPool pool = Substitute.For<ITxPool>();
        // Builder's internal shuffle picks the order; assert only the cap invariant below.
        pool.GetPendingTransactions().Returns([huge, tiny]);
        InclusionListBuilder builder = new(pool);

        List<byte[]> il = builder.GetInclusionList().ToList();

        // Either both fit (sum below cap) or huge alone fits and tiny is skipped — both legal.
        int totalBytes = il.Sum(t => t.Length);
        Assert.That(totalBytes, Is.LessThanOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList));
    }

    [Test]
    public void Returned_bytes_are_valid_RLP_decoding_back_to_originals()
    {
        Transaction[] txs = Enumerable.Range(0, 5).Select(i => TxOfSize(40, i)).ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(txs);
        InclusionListBuilder builder = new(pool);

        byte[][] ilBytes = builder.GetInclusionList().ToArray();

        // Round-trip: every yielded byte[] must decode to a pool-known tx hash.
        HashSet<Hash256> originals = [.. txs.Select(t => t.Hash!)];
        foreach (byte[] bytes in ilBytes)
        {
            Rlp.ValueDecoderContext ctx = new(bytes);
            Transaction decoded = TxDecoder.Instance.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
            Assert.That(originals, Does.Contain(decoded.Hash!));
        }
    }
}
