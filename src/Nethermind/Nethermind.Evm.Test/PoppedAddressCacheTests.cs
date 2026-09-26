// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class PoppedAddressCacheTests
{
    private static readonly byte[] AddressA = Address.FromNumber(0x1000).Bytes.ToArray();
    private static readonly byte[] AddressB = Address.FromNumber(0x2000).Bytes.ToArray();

    [Test]
    public void GetOrCreate_SameBytesTwice_ReturnsSameInstance()
    {
        PoppedAddressCache cache = new();

        Address first = cache.GetOrCreate(AddressA);
        Address second = cache.GetOrCreate(AddressA);

        Assert.That(second, Is.SameAs(first));
        Assert.That(first.Bytes.ToArray(), Is.EqualTo(AddressA));
    }

    [Test]
    public void GetOrCreate_DifferentBytes_ReturnsCorrectAddresses()
    {
        PoppedAddressCache cache = new();

        Address first = cache.GetOrCreate(AddressA);
        Address second = cache.GetOrCreate(AddressB);

        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(first.Bytes.ToArray(), Is.EqualTo(AddressA));
        Assert.That(second.Bytes.ToArray(), Is.EqualTo(AddressB));
    }

    [Test]
    public void GetOrCreate_AlternatingBytes_AlwaysReturnsMatchingAddress()
    {
        PoppedAddressCache cache = new();

        for (int i = 0; i < 4; i++)
        {
            byte[] expected = (i & 1) == 0 ? AddressA : AddressB;
            Address address = cache.GetOrCreate(expected);
            Assert.That(address.Bytes.ToArray(), Is.EqualTo(expected));
        }
    }

    [Test]
    public void GetOrCreate_AlternatingBytes_ReusesBothInstances()
    {
        PoppedAddressCache cache = new();
        Address firstA = cache.GetOrCreate(AddressA);
        Address firstB = cache.GetOrCreate(AddressB);

        for (int i = 0; i < 4; i++)
        {
            Assert.That(cache.GetOrCreate(AddressA), Is.SameAs(firstA));
            Assert.That(cache.GetOrCreate(AddressB), Is.SameAs(firstB));
        }
    }

    [Test]
    public void GetOrCreate_WorkingSetOfFour_ReusesAllInstances()
    {
        PoppedAddressCache cache = new();
        byte[][] workingSet = [AddressA, AddressB, AddressC, AddressD];
        Address[] firstRound = new Address[4];
        for (int i = 0; i < 4; i++)
        {
            firstRound[i] = cache.GetOrCreate(workingSet[i]);
        }

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.That(cache.GetOrCreate(workingSet[i]), Is.SameAs(firstRound[i]));
            }
        }
    }

    [Test]
    public void GetOrCreate_FifthDistinctAddress_EvictsLeastRecentlyUsed()
    {
        PoppedAddressCache cache = new();
        Address firstA = cache.GetOrCreate(AddressA);
        cache.GetOrCreate(AddressB);
        cache.GetOrCreate(AddressC);
        cache.GetOrCreate(AddressD);

        cache.GetOrCreate(AddressE);
        Address secondA = cache.GetOrCreate(AddressA);

        Assert.That(secondA, Is.Not.SameAs(firstA));
        Assert.That(secondA.Bytes.ToArray(), Is.EqualTo(AddressA));
    }

    [Test]
    public void GetOrCreate_rejects_non_address_lengths([Values(0, 19, 21, 32)] int length)
    {
        PoppedAddressCache cache = new();
        Assert.Throws<System.ArgumentException>(() => cache.GetOrCreate(new byte[length]));
    }

    [Test]
    public void Native_key_ignores_discarded_bits([Range(160, 255)] int bit)
    {
        PoppedAddressCache cache = new();
        UInt256 original = new(0x0123456789abcdef, 0xfedcba9876543210, 0x89abcdef, 0);
        Address first = cache.GetOrCreate(in original);
        UInt256 changed = original | (UInt256.One << bit);

        Assert.That(cache.GetOrCreate(in changed), Is.SameAs(first));
    }

    [Test]
    public void Native_key_preserves_every_address_bit([Range(0, 159)] int bit)
    {
        PoppedAddressCache cache = new();
        Address zero = cache.GetOrCreate(in UInt256.Zero);
        UInt256 value = UInt256.One << bit;
        Address actual = cache.GetOrCreate(in value);
        byte[] expected = new byte[Address.Size];
        expected[Address.Size - 1 - bit / 8] = (byte)(1 << (bit % 8));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.Not.SameAs(zero));
            Assert.That(actual.Bytes.ToArray(), Is.EqualTo(expected));
            Assert.That(cache.GetOrCreate(expected), Is.SameAs(actual));
        }
    }

    [Test]
    public void Native_key_preserves_fifo_and_byte_lookup_interoperability()
    {
        PoppedAddressCache cache = new();
        UInt256[] values = [UInt256.Zero, UInt256.One, new(0, 1, 0, 0), UInt256.MaxValue];
        Address[] addresses = new Address[values.Length];
        for (int i = 0; i < values.Length; i++) addresses[i] = cache.GetOrCreate(in values[i]);
        for (int i = 0; i < values.Length; i++)
            Assert.That(cache.GetOrCreate(addresses[i].Bytes), Is.SameAs(addresses[i]));

        UInt256 fifth = new(5);
        cache.GetOrCreate(in fifth);
        Assert.That(cache.GetOrCreate(in values[0]), Is.Not.SameAs(addresses[0]));
    }

    private static readonly byte[] AddressC = Address.FromNumber(0x3000).Bytes.ToArray();
    private static readonly byte[] AddressD = Address.FromNumber(0x4000).Bytes.ToArray();
    private static readonly byte[] AddressE = Address.FromNumber(0x5000).Bytes.ToArray();
}
