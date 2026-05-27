// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// Regression tests for <see cref="PayloadAttributes.GetPayloadId"/>. EIP-7805 (FOCIL)
/// requires that two FCUv5 calls with the same parent/timestamp but different
/// <see cref="PayloadAttributes.InclusionListTransactions"/> produce DIFFERENT payload
/// ids — otherwise the second caller hits the producer's cache and receives a block
/// built for the first IL.
/// </summary>
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

        idA.Should().NotBe(idB);
    }

    [Test]
    public void Empty_inclusion_list_differs_from_nonempty()
    {
        BlockHeader parent = Parent();
        string idEmpty = BaseAttrs([]).GetPayloadId(parent);
        string idOne = BaseAttrs([[0x01]]).GetPayloadId(parent);

        idEmpty.Should().NotBe(idOne);
    }

    [Test]
    public void Inclusion_list_order_affects_payload_id()
    {
        BlockHeader parent = Parent();
        string idAB = BaseAttrs([[0x01], [0x02]]).GetPayloadId(parent);
        string idBA = BaseAttrs([[0x02], [0x01]]).GetPayloadId(parent);

        idAB.Should().NotBe(idBA);
    }

    [Test]
    public void Same_inclusion_list_yields_same_payload_id()
    {
        BlockHeader parent = Parent();
        string id1 = BaseAttrs([[0x01, 0x02, 0x03]]).GetPayloadId(parent);
        string id2 = BaseAttrs([[0x01, 0x02, 0x03]]).GetPayloadId(parent);

        id1.Should().Be(id2);
    }

    [Test]
    public void Null_vs_empty_inclusion_list_yields_different_payload_ids()
    {
        // Null = V4 (Amsterdam) attributes — IL absent from the digest entirely.
        // Empty array = V5 (Bogota) attributes — IL present, just with no entries.
        // These are different fork-versioned messages so their payload ids must not collide.
        BlockHeader parent = Parent();
        string idNull = BaseAttrs(null).GetPayloadId(parent);
        string idEmpty = BaseAttrs([]).GetPayloadId(parent);

        idNull.Should().NotBe(idEmpty);
    }
}
