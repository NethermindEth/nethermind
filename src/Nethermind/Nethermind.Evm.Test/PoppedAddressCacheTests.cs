// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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

    private static readonly byte[] AddressC = Address.FromNumber(0x3000).Bytes.ToArray();
    private static readonly byte[] AddressD = Address.FromNumber(0x4000).Bytes.ToArray();
    private static readonly byte[] AddressE = Address.FromNumber(0x5000).Bytes.ToArray();
}
