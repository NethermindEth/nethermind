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
}
