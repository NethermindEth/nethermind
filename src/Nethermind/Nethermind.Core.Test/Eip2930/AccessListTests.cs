// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Eip2930;

public class AccessListTests
{
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

        accessList.AsEnumerable().Should().BeEquivalentTo(expected);
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

        accessList.AsEnumerable().Should().BeEquivalentTo(expected);
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

        accessList.AsEnumerable().Should().BeEquivalentTo(expected);
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

        accessList.AsEnumerable().Should().BeEquivalentTo(expected);
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
            (address1, new UInt256[] { }),
            (address2, new UInt256[] { })
        };

        accessList.AsEnumerable().Should().BeEquivalentTo(expected);
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

        accessList.AsEnumerable().Should().BeEquivalentTo(expected);
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
            (address2, new UInt256[] { }),
        };

        accessList.AsEnumerable().Should().BeEquivalentTo(expected);
    }

    [Test]
    public void Invalid_storage_when_no_previous_address()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            AccessList.Builder _ = new AccessList.Builder()
                .AddStorage(UInt256.Zero);
        });
    }
}
