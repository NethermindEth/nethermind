// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class PayloadAttributesPayloadIdTests
{
    private static BlockHeader Parent() => Build.A.BlockHeader.WithHash(TestItem.KeccakA).TestObject;

    private static PayloadAttributes BaseAttrs(byte[][][]? inclusionLists) => new()
    {
        Timestamp = 1234,
        PrevRandao = TestItem.KeccakB,
        SuggestedFeeRecipient = TestItem.AddressA,
        Withdrawals = [],
        ParentBeaconBlockRoot = TestItem.KeccakC,
        SlotNumber = 42,
        InclusionLists = inclusionLists,
    };

    /// <summary>One list per committee position, <paramref name="lists"/> from position 0 and the rest empty.</summary>
    private static byte[][][] Lists(params byte[][][] lists) =>
        [.. lists, .. Enumerable.Repeat<byte[][]>([], Eip7805Constants.InclusionListCommitteeSize - lists.Length)];

    [Test]
    public void Different_inclusion_lists_yield_different_payload_ids()
    {
        BlockHeader parent = Parent();
        string idA = BaseAttrs(Lists([[0x01, 0x02]])).GetPayloadId(parent);
        string idB = BaseAttrs(Lists([[0x03, 0x04]])).GetPayloadId(parent);

        Assert.That(idA, Is.Not.EqualTo(idB));
    }

    [Test]
    public void Empty_inclusion_lists_differ_from_nonempty()
    {
        BlockHeader parent = Parent();
        string idEmpty = BaseAttrs(Lists()).GetPayloadId(parent);
        string idOne = BaseAttrs(Lists([[0x01]])).GetPayloadId(parent);

        Assert.That(idEmpty, Is.Not.EqualTo(idOne));
    }

    [Test]
    public void Inclusion_list_order_affects_payload_id()
    {
        BlockHeader parent = Parent();
        string idAB = BaseAttrs(Lists([[0x01], [0x02]])).GetPayloadId(parent);
        string idBA = BaseAttrs(Lists([[0x02], [0x01]])).GetPayloadId(parent);

        Assert.That(idAB, Is.Not.EqualTo(idBA));
    }

    // Each list's entries decide its own budget fill, so neither which position carries an entry nor where one list
    // ends and the next begins may collapse into the same build.
    [Test]
    public void Committee_positions_and_list_boundaries_affect_payload_id()
    {
        BlockHeader parent = Parent();
        string atFirst = BaseAttrs(Lists([[0x01]])).GetPayloadId(parent);
        string atSecond = BaseAttrs(Lists([], [[0x01]])).GetPayloadId(parent);
        string oneList = BaseAttrs(Lists([[0x01], [0x02]])).GetPayloadId(parent);
        string twoLists = BaseAttrs(Lists([[0x01]], [[0x02]])).GetPayloadId(parent);
        string oneEntry = BaseAttrs(Lists([[0x01, 0x02]])).GetPayloadId(parent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atFirst, Is.Not.EqualTo(atSecond));
            Assert.That(oneList, Is.Not.EqualTo(twoLists));
            Assert.That(oneList, Is.Not.EqualTo(oneEntry));
        }
    }

    [Test]
    public void Same_inclusion_lists_yield_same_payload_id()
    {
        BlockHeader parent = Parent();
        string id1 = BaseAttrs(Lists([[0x01, 0x02, 0x03]])).GetPayloadId(parent);
        string id2 = BaseAttrs(Lists([[0x01, 0x02, 0x03]])).GetPayloadId(parent);

        Assert.That(id1, Is.EqualTo(id2));
    }

    [Test]
    public void Oversized_inclusion_list_still_yields_a_stable_distinct_payload_id()
    {
        // Far past one list's byte cap, which forkchoiceUpdated rejects before a build, so only the digest runs.
        byte[] oversized = new byte[Eip7805Constants.InclusionListCommitteeSize * Eip7805Constants.MaxBytesPerInclusionList * 2];

        string id1 = BaseAttrs(Lists([oversized])).GetPayloadId(Parent());
        string id2 = BaseAttrs(Lists([oversized])).GetPayloadId(Parent());
        oversized[0] = 0xff;
        string idChanged = BaseAttrs(Lists([oversized])).GetPayloadId(Parent());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(id1, Is.EqualTo(id2));
            Assert.That(idChanged, Is.Not.EqualTo(id1));
        }
    }

    [Test]
    public void Null_vs_empty_inclusion_list_yields_different_payload_ids()
    {
        // null = V4 (lists omitted from digest); empty lists = V5 — different fork versions.
        BlockHeader parent = Parent();
        string idNull = BaseAttrs(null).GetPayloadId(parent);
        string idEmpty = BaseAttrs(Lists()).GetPayloadId(parent);

        Assert.That(idNull, Is.Not.EqualTo(idEmpty));
    }
}
