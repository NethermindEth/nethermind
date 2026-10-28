// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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
