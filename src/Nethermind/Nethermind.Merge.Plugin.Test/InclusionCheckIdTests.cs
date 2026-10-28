// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>bogota.md § Deferred inclusion checking: <c>keccak256(b"engine/inclusion-check/v1" || RLP([blockHash,
/// inclusionLists, claims]))</c>, keeping list boundaries, positions, order, duplicates and claim order.</summary>
public class InclusionCheckIdTests
{
    private static readonly byte[] TxA = [0x01];
    private static readonly byte[] TxB = Enumerable.Range(0, 60).Select(static i => (byte)i).ToArray();

    [Test]
    public void Matches_the_rlp_the_engine_api_defines()
    {
        byte[][][] inclusionLists = Lists([TxA, TxB, TxA], [], [TxB]);
        OmissionClaim[] claims = [new(TestItem.KeccakB, 0), new(TestItem.KeccakC, 1UL << 40)];

        Rlp encoded = Rlp.Encode(
            Rlp.Encode(TestItem.KeccakA),
            Rlp.Encode([.. inclusionLists.Select(static list => Rlp.Encode([.. list.Select(static tx => Rlp.Encode(tx))]))]),
            Rlp.Encode([.. claims.Select(static claim => Rlp.Encode(Rlp.Encode(claim.TransactionHash), Rlp.Encode(claim.EvaluationIndex)))]));
        Hash256 expected = Keccak.Compute([.. "engine/inclusion-check/v1"u8.ToArray(), .. encoded.Bytes]);

        Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, inclusionLists, claims), Is.EqualTo(expected));
    }

    [Test]
    public void Distinguishes_every_part_of_the_inputs()
    {
        Hash256 id = InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxA, TxB]), [new(TestItem.KeccakB, 1)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxA, TxB]), [new(TestItem.KeccakB, 1)]), Is.EqualTo(id));
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakC, Lists([TxA, TxB]), [new(TestItem.KeccakB, 1)]), Is.Not.EqualTo(id), "block hash");
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxB, TxA]), [new(TestItem.KeccakB, 1)]), Is.Not.EqualTo(id), "transaction order");
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxA], [TxB]), [new(TestItem.KeccakB, 1)]), Is.Not.EqualTo(id), "list boundaries");
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([], [TxA, TxB]), [new(TestItem.KeccakB, 1)]), Is.Not.EqualTo(id), "committee position");
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxA, TxB, TxB]), [new(TestItem.KeccakB, 1)]), Is.Not.EqualTo(id), "duplicates");
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxA, TxB]), [new(TestItem.KeccakB, 2)]), Is.Not.EqualTo(id), "evaluation index");
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxA, TxB]), []), Is.Not.EqualTo(id), "claims");
            Assert.That(InclusionCheckId.Compute(TestItem.KeccakA, Lists([TxA, TxB]), [new(TestItem.KeccakB, 1), new(TestItem.KeccakB, 1)]), Is.Not.EqualTo(id), "duplicate claims");
        }
    }

    private static byte[][][] Lists(params byte[][][] lists) =>
        [.. lists, .. Enumerable.Repeat<byte[][]>([], Eip7805Constants.InclusionListCommitteeSize - lists.Length)];
}
