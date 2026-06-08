// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class PayloadAttributesPayloadIdTests
{
    private static BlockHeader Parent() => Build.A.BlockHeader.WithHash(TestItem.KeccakA).TestObject;

    private static PayloadAttributes BaseAttrs(byte[][]? il) => new()
    {
        Timestamp = 1234,
        PrevRandao = TestItem.KeccakB,
        SuggestedFeeRecipient = TestItem.AddressA,
        Withdrawals = [],
        ParentBeaconBlockRoot = TestItem.KeccakC,
        SlotNumber = 42,
        InclusionListTransactions = il,
    };

    [Test]
    public void Different_inclusion_lists_yield_different_payload_ids()
    {
        BlockHeader parent = Parent();
        string idA = BaseAttrs([[0x01, 0x02]]).GetPayloadId(parent);
        string idB = BaseAttrs([[0x03, 0x04]]).GetPayloadId(parent);

        Assert.That(idA, Is.Not.EqualTo(idB));
    }

    [Test]
    public void Empty_inclusion_list_differs_from_nonempty()
    {
        BlockHeader parent = Parent();
        string idEmpty = BaseAttrs([]).GetPayloadId(parent);
        string idOne = BaseAttrs([[0x01]]).GetPayloadId(parent);

        Assert.That(idEmpty, Is.Not.EqualTo(idOne));
    }

    [Test]
    public void Inclusion_list_order_affects_payload_id()
    {
        BlockHeader parent = Parent();
        string idAB = BaseAttrs([[0x01], [0x02]]).GetPayloadId(parent);
        string idBA = BaseAttrs([[0x02], [0x01]]).GetPayloadId(parent);

        Assert.That(idAB, Is.Not.EqualTo(idBA));
    }

    [Test]
    public void Same_inclusion_list_yields_same_payload_id()
    {
        BlockHeader parent = Parent();
        string id1 = BaseAttrs([[0x01, 0x02, 0x03]]).GetPayloadId(parent);
        string id2 = BaseAttrs([[0x01, 0x02, 0x03]]).GetPayloadId(parent);

        Assert.That(id1, Is.EqualTo(id2));
    }

    [Test]
    public void Null_vs_empty_inclusion_list_yields_different_payload_ids()
    {
        // null = V4 (IL omitted from digest); [] = V5 with empty IL — different fork versions.
        BlockHeader parent = Parent();
        string idNull = BaseAttrs(null).GetPayloadId(parent);
        string idEmpty = BaseAttrs([]).GetPayloadId(parent);

        Assert.That(idNull, Is.Not.EqualTo(idEmpty));
    }
}
