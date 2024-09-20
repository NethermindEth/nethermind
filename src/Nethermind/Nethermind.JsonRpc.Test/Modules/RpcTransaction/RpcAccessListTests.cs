// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions.Json;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public class RpcAccessListTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    [Test]
    public void Single_address_with_no_storage()
    {
        Address address = TestItem.AddressA;
        AccessList accessList = new AccessList.Builder()
            .AddAddress(address)
            .Build();

        RpcAccessList forRpc = RpcAccessList.FromAccessList(accessList);
        string serialized = _serializer.Serialize(forRpc);

        var actual = JArray.Parse(serialized);
        var expected = JArray.Parse(
            """
              [{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":[]}]
              """);
        actual.Should().BeEquivalentTo(expected);
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

        RpcAccessList forRpc = RpcAccessList.FromAccessList(accessList);
        string serialized = _serializer.Serialize(forRpc);

        var actual = JArray.Parse(serialized);
        var expected = JArray.Parse(
            """
            [{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000001","0x0000000000000000000000000000000000000000000000000000000000000002","0x0000000000000000000000000000000000000000000000000000000000000003"]}]
            """);
        actual.Should().BeEquivalentTo(expected);
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

        RpcAccessList forRpc = RpcAccessList.FromAccessList(accessList);
        string serialized = _serializer.Serialize(forRpc);

        var actual = JArray.Parse(serialized);
        var expected = JArray.Parse(
            """
            [{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000001","0x0000000000000000000000000000000000000000000000000000000000000002","0x0000000000000000000000000000000000000000000000000000000000000003","0x0000000000000000000000000000000000000000000000000000000000000001"]}]
            """);
        actual.Should().BeEquivalentTo(expected);
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

        RpcAccessList forRpc = RpcAccessList.FromAccessList(accessList);
        string serialized = _serializer.Serialize(forRpc);

        var actual = JArray.Parse(serialized);
        var expected = JArray.Parse(
            """
            [{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000001","0x0000000000000000000000000000000000000000000000000000000000000002"]},{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000003"]}]
            """);
        actual.Should().BeEquivalentTo(expected);
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

        RpcAccessList forRpc = RpcAccessList.FromAccessList(accessList);
        string serialized = _serializer.Serialize(forRpc);

        var actual = JArray.Parse(serialized);
        var expected = JArray.Parse(
            """
            [{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000001","0x0000000000000000000000000000000000000000000000000000000000000002"]},{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","storageKeys":["0x0000000000000000000000000000000000000000000000000000000000000001","0x0000000000000000000000000000000000000000000000000000000000000003"]}]
            """);
        actual.Should().BeEquivalentTo(expected);
    }
}
