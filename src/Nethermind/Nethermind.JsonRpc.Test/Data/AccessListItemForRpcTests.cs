// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

public class AccessListItemForRpcTests
{
    [Test]
    public void Single_address_with_no_storage()
    {
        Address address = TestItem.AddressA;
        AccessList accessList = new AccessList.Builder()
            .AddAddress(address)
            .Build();

        IEnumerable<AccessListItemForRpc> forRpc = AccessListItemForRpc.FromAccessList(accessList);
        AccessListItemForRpc[] expected = { new(address, new UInt256[] { }) };
        forRpc.Should().BeEquivalentTo(expected);
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

        IEnumerable<AccessListItemForRpc> forRpc = AccessListItemForRpc.FromAccessList(accessList);
        AccessListItemForRpc[] expected = { new(address, new[] { storageKey1, storageKey2, storageKey3 }) };
        forRpc.Should().BeEquivalentTo(expected);
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

        IEnumerable<AccessListItemForRpc> forRpc = AccessListItemForRpc.FromAccessList(accessList);

        AccessListItemForRpc[] expected = { new(address, new[] { storageKey1, storageKey2, storageKey3, storageKey1 }) };
        forRpc.Should().BeEquivalentTo(expected);
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

        IEnumerable<AccessListItemForRpc> forRpc = AccessListItemForRpc.FromAccessList(accessList);

        AccessListItemForRpc[] expected = { new(address, new[] { storageKey1, storageKey2 }), new(address, new[] { storageKey3 }) };
        forRpc.Should().BeEquivalentTo(expected);
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

        IEnumerable<AccessListItemForRpc> forRpc = AccessListItemForRpc.FromAccessList(accessList);
        AccessListItemForRpc[] expected = { new(address, new[] { storageKey1, storageKey2 }), new(address, new[] { storageKey1, storageKey3 }) };
        forRpc.Should().BeEquivalentTo(expected);
    }
}
