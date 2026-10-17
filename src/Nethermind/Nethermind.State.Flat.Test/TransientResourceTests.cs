// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class TransientResourceTests
{
    [Test]
    public void PrewarmKey_MatchesTheKeyTypesOwnHashes()
    {
        // The prewarm filter mixes its key with a bijection, so the key it is handed decides how often it lies.
        // Deriving it the same way the cached key types do keeps one definition to reason about and to fix.
        Address address = TestItem.AddressA;
        UInt256 slot = 42;
        AddressAsKey accountKey = address;
        StorageCell cell = new(address, slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TransientResource.PrewarmKey(address.Bytes, null), Is.EqualTo((ulong)accountKey.GetHashCode64()));
            Assert.That(TransientResource.PrewarmKey(address.Bytes, slot), Is.EqualTo((ulong)cell.GetHashCode64()));
        }
    }

    [Test]
    public void PrewarmKey_SeparatesAnAccountFromItsSlots()
    {
        Address address = TestItem.AddressA;

        ulong account = TransientResource.PrewarmKey(address.Bytes, null);
        ulong slotZero = TransientResource.PrewarmKey(address.Bytes, UInt256.Zero);
        ulong slotOne = TransientResource.PrewarmKey(address.Bytes, UInt256.One);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slotZero, Is.Not.EqualTo(account), "an account and its slot zero share the filter");
            Assert.That(slotOne, Is.Not.EqualTo(slotZero));
        }
    }

    [Test]
    public void Reset_AfterPrewarmMarks_ClearsTheDedupeBloom()
    {
        using TransientResource resource = new(new TransientResource.Size(1024, 1024));
        Assert.That(resource.ShouldPrewarm(TestItem.AddressA, null), Is.True, "precondition: the first hint is queued");
        Assert.That(resource.ShouldPrewarm(TestItem.AddressA, null), Is.False, "precondition: a repeated hint is deduplicated");

        resource.Reset();

        Assert.That(resource.ShouldPrewarm(TestItem.AddressA, null), Is.True, "a reset resource must forget earlier hints");
    }

    [Test]
    public void Reset_GrowsTheWarmedNodesOnlyPastTheirCapacity([Values] bool pastCapacity)
    {
        const int capacity = 4;
        using TransientResource resource = new(new TransientResource.Size(1024, capacity));
        WarmedNodes warmedNodes = resource.WarmedNodes;
        int nodeCount = pastCapacity ? capacity + 1 : capacity;
        ulong originalPointer = 0;
        for (byte value = 0; value < nodeCount; value++)
        {
            byte[] rlp = FlatTestHelpers.LeafRlp(value);
            warmedNodes.TryInsert(null, TreePath.Empty, ValueKeccak.Compute(rlp), rlp, out _, out ulong pointer);
            if (value == 0) originalPointer = pointer;
        }

        resource.Reset();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource.WarmedNodes, pastCapacity ? Is.Not.SameAs(warmedNodes) : Is.SameAs(warmedNodes));
            Assert.That(resource.WarmedNodes.Count, Is.Zero);
            Assert.That(resource.GetSize().NodesCacheSize, Is.EqualTo(nodeCount));
        }

        byte[] originalRlp = FlatTestHelpers.LeafRlp(0);
        ValueHash256 originalHash = ValueKeccak.Compute(originalRlp);
        resource.WarmedNodes.TryInsert(null, TreePath.Empty, originalHash, originalRlp, out _, out ulong newPointer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPointer, Is.Not.EqualTo(originalPointer), "growth and reuse both change pointer identity");
            Assert.That(resource.WarmedNodes.TryGet(originalPointer, originalHash, out _), Is.False);
            Assert.That(resource.WarmedNodes.TryGet(newPointer, originalHash, out _), Is.True);
        }
    }

    [Test]
    public void Reset_WhenNothingWasMarked_KeepsTheDedupeBloomEmpty()
    {
        using TransientResource resource = new(new TransientResource.Size(1024, 1024));

        resource.Reset();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource.PrewarmedAddresses.Count, Is.EqualTo(0), "an unused bloom stays empty");
            Assert.That(resource.ShouldPrewarm(TestItem.AddressA, UInt256.One), Is.True, "nothing is marked after resetting an unused resource");
        }
    }
}
