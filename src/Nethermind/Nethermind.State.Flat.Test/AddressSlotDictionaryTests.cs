// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class AddressSlotDictionaryTests
{
    [Test]
    public void Set_TryGetValue_Count_Enumerate_RoundTrip()
    {
        AddressSlotDictionary slots = new();
        slots.Set(TestItem.AddressA, 1, 10);
        slots.Set(TestItem.AddressA, 2, null);
        slots[(TestItem.AddressB, (UInt256)7)] = 70;
        slots.Set(TestItem.AddressA, 1, 11);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.Count, Is.EqualTo(3));
            Assert.That(slots.TryGetValue(TestItem.AddressA, 1, out UInt256? overwritten), Is.True);
            Assert.That(overwritten, Is.EqualTo((UInt256?)11));
            Assert.That(slots.TryGetValue(TestItem.AddressA, 2, out UInt256? deleted), Is.True);
            Assert.That(deleted, Is.Null);
            Assert.That(slots.TryGetValue((TestItem.AddressB, (UInt256)7), out UInt256? viaKey), Is.True);
            Assert.That(viaKey, Is.EqualTo((UInt256?)70));
            Assert.That(slots.TryGetValue(TestItem.AddressC, 1, out _), Is.False);
            Assert.That(slots.TryGetValue(TestItem.AddressA, 3, out _), Is.False);
        }

        Dictionary<HashedKey<(Address, UInt256)>, UInt256?> enumerated = slots.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.That(enumerated, Is.EquivalentTo(new Dictionary<HashedKey<(Address, UInt256)>, UInt256?>
        {
            [(TestItem.AddressA, (UInt256)1)] = 11,
            [(TestItem.AddressA, (UInt256)2)] = null,
            [(TestItem.AddressB, (UInt256)7)] = 70,
        }));
    }

    [Test]
    public void RemoveAddress_DropsOnlyThatAddress()
    {
        AddressSlotDictionary slots = new();
        slots.Set(TestItem.AddressA, 1, 10);
        slots.Set(TestItem.AddressB, 1, 20);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.RemoveAddress(TestItem.AddressA), Is.True);
            Assert.That(slots.RemoveAddress(TestItem.AddressA), Is.False);
            Assert.That(slots.TryGetValue(TestItem.AddressA, 1, out _), Is.False);
            Assert.That(slots.TryGetValue(TestItem.AddressB, 1, out UInt256? kept), Is.True);
            Assert.That(kept, Is.EqualTo((UInt256?)20));
            Assert.That(slots.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void NoLockClear_EmptiesAndAllowsReuse()
    {
        AddressSlotDictionary slots = new();
        slots.Set(TestItem.AddressA, 1, 10);

        slots.NoLockClear();

        Assert.That(slots.Count, Is.Zero);
        Assert.That(slots.Any(), Is.False);

        slots.Set(TestItem.AddressA, 5, 50);
        Assert.That(slots.TryGetValue(TestItem.AddressA, 5, out UInt256? value) && value == 50, Is.True);
    }
}
