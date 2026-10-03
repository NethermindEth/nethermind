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

    [Test]
    public void Serializes_to_exact_json()
    {
        AccessList accessList = new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(UInt256.Zero)
            .AddStorage((UInt256)0xabc)
            .AddStorage(UInt256.MaxValue)
            .AddAddress(TestItem.AddressB)
            .Build();

        string zeroKey = "0x" + new string('0', 64);
        string abcKey = "0x" + new string('0', 61) + "abc";
        string maxKey = "0x" + new string('f', 64);
        string expected = $$"""[{"address":"{{AddressAJson}}","storageKeys":["{{zeroKey}}","{{abcKey}}","{{maxKey}}"]},{"address":"{{TestItem.AddressB}}","storageKeys":[]}]""";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_serializer.Serialize(AccessListForRpc.FromAccessList(accessList)), Is.EqualTo(expected));
            Assert.That(_serializer.Serialize(AccessListForRpc.FromAccessList(AccessList.Empty)), Is.EqualTo("[]"));
            Assert.That(_serializer.Serialize(AccessListForRpc.FromAccessList(null)), Is.EqualTo("[]"));
        }
    }

    [TestCase("address", "storageKeys")]
    [TestCase("ADDRESS", "StorageKeys")]
    [TestCase("addr\\u0065ss", "storage\\u004beys")]
    public void Deserializes_property_names_case_insensitively(string addressName, string storageKeysName)
    {
        string json = $$"""[{"{{storageKeysName}}":["{{Slot1}}","{{Slot2}}"],"other":[1],"{{addressName}}":"{{AddressAJson}}"}]""";

        AccessList accessList = _serializer.Deserialize<AccessListForRpc>(json)!.ToAccessList();

        Assert.That(_serializer.Serialize(AccessListForRpc.FromAccessList(accessList)),
            Is.EqualTo($$"""[{"address":"{{AddressAJson}}","storageKeys":["{{Slot1}}","{{Slot2}}"]}]"""));
    }
}
