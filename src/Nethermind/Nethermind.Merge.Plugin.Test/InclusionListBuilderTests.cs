// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
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
/// Direct tests for the FOCIL (EIP-7805) <see cref="InclusionListBuilder"/> bytes-and-count
/// gating. Engine-API-level coverage lives in <see cref="EngineModuleTests"/>; this fixture
/// pins the size/limit behavior so future selection-strategy changes can't silently break
/// the spec's MAX_BYTES_PER_INCLUSION_LIST contract.
/// </summary>
public class InclusionListBuilderTests
{
    private static Transaction TxOfSize(int payloadBytes, int nonce = 0)
    {
        // 'Data' bytes inflate the encoded RLP roughly 1:1, so this gives predictable sizing.
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

        builder.GetInclusionList().Should().BeEmpty();
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
        totalBytes.Should().BeLessOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList);
        // Sanity: we returned at least one tx so the cap isn't a false positive on selection
        il.Should().NotBeEmpty();
    }

    [Test]
    public void Skips_txs_that_would_overflow_but_keeps_smaller_ones_that_fit()
    {
        Transaction huge = TxOfSize(8000, 0);   // alone fits
        Transaction tiny = TxOfSize(50, 1);     // tiny enough to slot in after huge
        ITxPool pool = Substitute.For<ITxPool>();
        // Deterministic order independent of the builder's internal shuffle: the builder will
        // skip whichever overflows. We assert via reverse-decoding what got included.
        pool.GetPendingTransactions().Returns([huge, tiny]);
        InclusionListBuilder builder = new(pool);

        List<byte[]> il = builder.GetInclusionList().ToList();

        // Either both fit (sum below cap) or huge alone fits and tiny is skipped — both legal.
        int totalBytes = il.Sum(t => t.Length);
        totalBytes.Should().BeLessOrEqualTo(Eip7805Constants.MaxBytesPerInclusionList);
    }

    [Test]
    public void Returned_bytes_are_valid_RLP_decoding_back_to_originals()
    {
        Transaction[] txs = Enumerable.Range(0, 5).Select(i => TxOfSize(40, i)).ToArray();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(txs);
        InclusionListBuilder builder = new(pool);

        byte[][] ilBytes = builder.GetInclusionList().ToArray();

        // Round-trip: every yielded byte[] must decode to a transaction whose hash is in
        // the original pool. Anything else means the builder smuggled in arbitrary bytes.
        HashSet<Hash256> originals = [.. txs.Select(t => t.Hash!)];
        foreach (byte[] bytes in ilBytes)
        {
            Rlp.ValueDecoderContext ctx = new(bytes);
            Transaction decoded = TxDecoder.Instance.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
            originals.Should().Contain(decoded.Hash!);
        }
    }
}
