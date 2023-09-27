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

public class AccessListTests
{
    [Test]
    public void Single_address_with_multiple_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessListBuilder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddStorage(storageKey3)
            .ToAccessList();

        AccessListItemForRpc[] forRpc = AccessListItemForRpc.FromAccessList(accessList);

        forRpc.Length.Should().Be(1);
        forRpc[0].Address.Should().Be(address);
        List<UInt256> expectedKeys = new() { storageKey1, storageKey2, storageKey3 };
        forRpc[0].StorageKeys.Should().BeEquivalentTo(expectedKeys);
    }

    [Test]
    public void Single_address_with_duplicated_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessListBuilder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddStorage(storageKey3)
            .AddStorage(storageKey1)
            .ToAccessList();

        AccessListItemForRpc[] forRpc = AccessListItemForRpc.FromAccessList(accessList);

        forRpc.Length.Should().Be(1);
        forRpc[0].Address.Should().Be(address);
        List<UInt256> expectedKeys = new() { storageKey1, storageKey2, storageKey3, storageKey1 };
        forRpc[0].StorageKeys.Should().BeEquivalentTo(expectedKeys);
    }

    [Test]
    public void Duplicated_address_with_multiple_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessListBuilder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddAddress(address)
            .AddStorage(storageKey3)
            .ToAccessList();

        AccessListItemForRpc[] forRpc = AccessListItemForRpc.FromAccessList(accessList);

        forRpc.Length.Should().Be(2);
        {
            forRpc[0].Address.Should().Be(address);
            List<UInt256> expectedKeys = new() { storageKey1, storageKey2 };
            forRpc[0].StorageKeys.Should().BeEquivalentTo(expectedKeys);
        }
        {
            forRpc[1].Address.Should().Be(address);
            List<UInt256> expectedKeys = new() { storageKey3 };
            forRpc[1].StorageKeys.Should().BeEquivalentTo(expectedKeys);
        }
    }

    [Test]
    public void Duplicated_address_with_duplicated_storage_keys()
    {
        Address address = TestItem.AddressA;
        UInt256 storageKey1 = (UInt256)1;
        UInt256 storageKey2 = (UInt256)2;
        UInt256 storageKey3 = (UInt256)3;

        AccessList accessList = new AccessListBuilder()
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey2)
            .AddAddress(address)
            .AddStorage(storageKey1)
            .AddStorage(storageKey3)
            .ToAccessList();

        AccessListItemForRpc[] forRpc = AccessListItemForRpc.FromAccessList(accessList);

        forRpc.Length.Should().Be(2);
        {
            forRpc[0].Address.Should().Be(address);
            List<UInt256> expectedKeys = new() { storageKey1, storageKey2 };
            forRpc[0].StorageKeys.Should().BeEquivalentTo(expectedKeys);
        }
        {
            forRpc[1].Address.Should().Be(address);
            List<UInt256> expectedKeys = new() { storageKey1, storageKey3 };
            forRpc[1].StorageKeys.Should().BeEquivalentTo(expectedKeys);
        }
    }
}
