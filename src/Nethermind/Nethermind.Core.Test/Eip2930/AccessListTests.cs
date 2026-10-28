// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Eip2930;

public class AccessListTests
{
    [Test]
    public void Concrete_storage_enumeration_preserves_entry_boundaries([Values(0, 1, 32)] int keyCount)
    {
        AccessList.Builder builder = new();
        for (int entry = 0; entry < 3; entry++)
        {
            builder.AddAddress(TestItem.AddressA);
            for (int key = 0; key < keyCount; key++)
                builder.AddStorage((UInt256)(entry * keyCount + key));
        }

        int index = 0;
        foreach ((Address _, AccessList.StorageKeysEnumerable keys) in builder.Build())
        {
            List<UInt256> actual = [];
            foreach (UInt256 key in keys) actual.Add(key);

            List<UInt256> expected = [];
            for (int key = 0; key < keyCount; key++) expected.Add((UInt256)(index * keyCount + key));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That((IEnumerable<UInt256>)keys, Is.EqualTo(actual));
            }
            index++;
        }
        Assert.That(index, Is.EqualTo(3));
    }

    [Test]
    public void Single_address_with_multiple_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessList.Builder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddStorage(storageKey3)
            .Build();

        IEnumerable<(Address, IEnumerable<UInt256>)> expected = new List<(Address, IEnumerable<UInt256>)>
        {
            (address, new[] { storageKey1, storageKey2, storageKey3 })
        };

        Assert.That(accessList, Is.EqualTo(expected));
    }

    [Test]
    public void Single_address_with_duplicated_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessList.Builder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddStorage(storageKey3)
            .AddStorage(storageKey1)
            .Build();

        IEnumerable<(Address, IEnumerable<UInt256>)> expected = new List<(Address, IEnumerable<UInt256>)>
        {
            (address, new[] { storageKey1, storageKey2, storageKey3, storageKey1 })
        };

        Assert.That(accessList, Is.EqualTo(expected));
    }

    [Test]
    public void Duplicated_address_with_multiple_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessList.Builder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddAddress(address)
            .AddStorage(storageKey3)
            .Build();

        IEnumerable<(Address, IEnumerable<UInt256>)> expected = new List<(Address, IEnumerable<UInt256>)>
        {
            (address, new[] { storageKey1, storageKey2 }),
            (address, new[] { storageKey3 })
        };

        Assert.That(accessList, Is.EqualTo(expected));
    }

    [Test]
    public void Duplicated_address_with_duplicated_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessList.Builder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey3)
            .Build();

        IEnumerable<(Address, IEnumerable<UInt256>)> expected = new List<(Address, IEnumerable<UInt256>)>
        {
            (address, new[] { storageKey1, storageKey2 }),
            (address, new[] { storageKey1, storageKey3 })
        };

        Assert.That(accessList, Is.EqualTo(expected));
    }

    [Test]
    public void Multiple_addresses_no_storage()
    {
        Address address1 = TestItem.AddressA;
        Address address2 = TestItem.AddressB;

        AccessList accessList = new AccessList.Builder()
            .AddAddress(address1)
            .AddAddress(address2)
            .Build();

        IEnumerable<(Address, IEnumerable<UInt256>)> expected = new List<(Address, IEnumerable<UInt256>)>
        {
            (address1, Array.Empty<UInt256>()),
            (address2, Array.Empty<UInt256>())
        };

        Assert.That(accessList, Is.EqualTo(expected));
    }

    [Test]
    public void Multiple_addresses_with_storage()
    {
        Address address1 = TestItem.AddressA;
        Address address2 = TestItem.AddressB;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessList.Builder()
            .AddAddress(address1)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddAddress(address2)
            .AddStorage(storageKey3)
            .Build();

        IEnumerable<(Address, IEnumerable<UInt256>)> expected = new List<(Address, IEnumerable<UInt256>)>
        {
            (address1, new[] { storageKey1, storageKey2 }),
            (address2, new[] { storageKey3 })
        };

        Assert.That(accessList, Is.EqualTo(expected));
    }

    [Test]
    public void Multiple_duplicated_addresses_with_storage()
    {
        Address address1 = TestItem.AddressA;
        Address address2 = TestItem.AddressB;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessList.Builder()
            .AddAddress(address1)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddAddress(address2)
            .AddStorage(storageKey3)
            .AddAddress(address1)
            .AddStorage(storageKey1)
            .AddAddress(address2)
            .Build();

        IEnumerable<(Address, IEnumerable<UInt256>)> expected = new List<(Address, IEnumerable<UInt256>)>
        {
            (address1, new[] { storageKey1, storageKey2 }),
            (address2, new[] { storageKey3 }),
            (address1, new[] { storageKey1 }),
            (address2, Array.Empty<UInt256>()),
        };

        Assert.That(accessList, Is.EqualTo(expected));
    }

    [Test]
    public void Invalid_storage_when_no_previous_address() => Assert.Throws<InvalidOperationException>(static () =>
    {
        AccessList.Builder _ = new AccessList.Builder()
            .AddStorage(UInt256.Zero);
    });
}
