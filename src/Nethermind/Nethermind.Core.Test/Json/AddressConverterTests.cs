// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class AddressConverterTests : ConverterTestBase<Address>
{
    static readonly AddressConverter converter = new();

    [TestCaseSource(nameof(AddressTestCases))]
    public void Test_roundtrip(Address? value) => TestConverter(value!, static (address, address1) => address == address1, converter);

    [TestCase("\"0xc94770007dda54cf92009bff0de90c06f603a09\"", TestName = "Rejects_39_hex_odd_short")]
    [TestCase("\"0xc94770007dda54cf92009bff0de90c06f603a09f1\"", TestName = "Rejects_41_hex_odd_long")]
    public void Rejects_odd_length_hex(string json)
    {
        JsonSerializerOptions options = new() { Converters = { converter } };
        Assert.That(() => JsonSerializer.Deserialize<Address>(json, options), Throws.InstanceOf<FormatException>());
    }

    [Test]
    public void Address_dictionary_roundtrips_with_global_options()
    {
        Dictionary<Address, int> dictionary = new()
        {
            [TestItem.AddressA] = 1,
            [TestItem.AddressB] = 2
        };

        string json = JsonSerializer.Serialize(dictionary, EthereumJsonSerializer.JsonOptions);
        Dictionary<Address, int>? result = JsonSerializer.Deserialize<Dictionary<Address, int>>(json, EthereumJsonSerializer.JsonOptions);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result![TestItem.AddressA], Is.EqualTo(1));
            Assert.That(result[TestItem.AddressB], Is.EqualTo(2));
        }
    }

    [Test]
    public void AddressAsKey_dictionary_roundtrips_with_global_options()
    {
        AddressAsKey addressA = TestItem.AddressA;
        AddressAsKey addressB = TestItem.AddressB;
        Dictionary<AddressAsKey, int> dictionary = new()
        {
            [addressA] = 1,
            [addressB] = 2
        };

        string json = JsonSerializer.Serialize(dictionary, EthereumJsonSerializer.JsonOptions);
        Dictionary<AddressAsKey, int>? result = JsonSerializer.Deserialize<Dictionary<AddressAsKey, int>>(json, EthereumJsonSerializer.JsonOptions);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result![addressA], Is.EqualTo(1));
            Assert.That(result[addressB], Is.EqualTo(2));
        }
    }

    static IEnumerable<TestCaseData> AddressTestCases =
    [
        new TestCaseData(null).SetName("null"),
        new TestCaseData(Address.Zero).SetName("zero"),
        new TestCaseData(TestItem.AddressA).SetName("testItemA"),
    ];
}
