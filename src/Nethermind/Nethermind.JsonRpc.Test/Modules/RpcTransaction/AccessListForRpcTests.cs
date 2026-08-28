// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

public class AccessListForRpcTests
{
    private readonly EthereumJsonSerializer _serializer = new();

    private const string AddressAJson = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099";
    private const string Slot1 = "0x0000000000000000000000000000000000000000000000000000000000000001";
    private const string Slot2 = "0x0000000000000000000000000000000000000000000000000000000000000002";
    private const string Slot3 = "0x0000000000000000000000000000000000000000000000000000000000000003";

    [Test]
    public void Single_address_with_no_storage() =>
        AssertSerializedAccessList(
            new AccessList.Builder()
                .AddAddress(TestItem.AddressA)
                .Build(),
            $$"""[{"address":"{{AddressAJson}}","storageKeys":[]}]""");

    [Test]
    public void Single_address_with_multiple_storage_keys() =>
        AssertSerializedAccessList(
            new AccessList.Builder()
                .AddAddress(TestItem.AddressA)
                .AddStorage((UInt256)1)
                .AddStorage((UInt256)2)
                .AddStorage((UInt256)3)
                .Build(),
            $$"""[{"address":"{{AddressAJson}}","storageKeys":["{{Slot1}}","{{Slot2}}","{{Slot3}}"]}]""");

    [Test]
    public void Single_address_with_duplicated_storage_keys() =>
        AssertSerializedAccessList(
            new AccessList.Builder()
                .AddAddress(TestItem.AddressA)
                .AddStorage((UInt256)1)
                .AddStorage((UInt256)2)
                .AddStorage((UInt256)3)
                .AddStorage((UInt256)1)
                .Build(),
            $$"""[{"address":"{{AddressAJson}}","storageKeys":["{{Slot1}}","{{Slot2}}","{{Slot3}}","{{Slot1}}"]}]""");

    [Test]
    public void Duplicated_address_with_multiple_storage_keys() =>
        AssertSerializedAccessList(
            new AccessList.Builder()
                .AddAddress(TestItem.AddressA)
                .AddStorage((UInt256)1)
                .AddStorage((UInt256)2)
                .AddAddress(TestItem.AddressA)
                .AddStorage((UInt256)3)
                .Build(),
            $$"""[{"address":"{{AddressAJson}}","storageKeys":["{{Slot1}}","{{Slot2}}"]},{"address":"{{AddressAJson}}","storageKeys":["{{Slot3}}"]}]""");

    [Test]
    public void Duplicated_address_with_duplicated_storage_keys() =>
        AssertSerializedAccessList(
            new AccessList.Builder()
                .AddAddress(TestItem.AddressA)
                .AddStorage((UInt256)1)
                .AddStorage((UInt256)2)
                .AddAddress(TestItem.AddressA)
                .AddStorage((UInt256)1)
                .AddStorage((UInt256)3)
                .Build(),
            $$"""[{"address":"{{AddressAJson}}","storageKeys":["{{Slot1}}","{{Slot2}}"]},{"address":"{{AddressAJson}}","storageKeys":["{{Slot1}}","{{Slot3}}"]}]""");

    private void AssertSerializedAccessList(AccessList accessList, string expectedJson)
    {
        AccessListForRpc forRpc = AccessListForRpc.FromAccessList(accessList);
        string serialized = _serializer.Serialize(forRpc);
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedJson)).Using(JToken.EqualityComparer));
    }
}
