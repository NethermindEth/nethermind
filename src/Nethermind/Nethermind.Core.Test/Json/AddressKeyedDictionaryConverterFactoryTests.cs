// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class AddressKeyedDictionaryConverterFactoryTests
{
    private static readonly JsonSerializerOptions Options = EthereumJsonSerializer.JsonOptions;

    // The same options without the factory: System.Text.Json's own dictionary converter.
    private static readonly JsonSerializerOptions BuiltInOptions = WithoutFactory();

    private static JsonSerializerOptions WithoutFactory()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions);
        foreach (AddressKeyedDictionaryConverterFactory converter in options.Converters.OfType<AddressKeyedDictionaryConverterFactory>().ToList())
        {
            options.Converters.Remove(converter);
        }

        return options;
    }

    public sealed class Account
    {
        public ulong? Nonce { get; set; }
        public Dictionary<string, int>? Slots { get; set; }
    }

    [Test]
    public void Nested_values_round_trip_and_match_the_built_in_converter()
    {
        Dictionary<Address, Account?> dictionary = new()
        {
            [TestItem.AddressA] = new Account { Nonce = 7, Slots = new() { ["a"] = 1 } },
            [TestItem.AddressB] = null,
            [TestItem.AddressC] = new Account(),
        };

        string json = JsonSerializer.Serialize(dictionary, Options);
        Dictionary<Address, Account?> result = JsonSerializer.Deserialize<Dictionary<Address, Account?>>(json, Options)!;

        Assert.That(json, Is.EqualTo(JsonSerializer.Serialize(dictionary, BuiltInOptions)));
        Assert.That(result.Keys, Is.EquivalentTo(dictionary.Keys));
        Assert.That(result[TestItem.AddressA]!.Nonce, Is.EqualTo(7));
        Assert.That(result[TestItem.AddressA]!.Slots!["a"], Is.EqualTo(1));
        Assert.That(result[TestItem.AddressB], Is.Null);
        Assert.That(result[TestItem.AddressC]!.Nonce, Is.Null);
    }

    [Test]
    public void Null_reads_as_null() =>
        Assert.That(JsonSerializer.Deserialize<Dictionary<Address, int>>("null", Options), Is.Null);

    [Test]
    public void Repeated_key_keeps_the_last_value_like_the_built_in_converter()
    {
        string json = $"{{\"{TestItem.AddressA}\":1,\"{TestItem.AddressA}\":2}}";

        Dictionary<Address, int> result = JsonSerializer.Deserialize<Dictionary<Address, int>>(json, Options)!;

        Assert.That(result[TestItem.AddressA], Is.EqualTo(JsonSerializer.Deserialize<Dictionary<Address, int>>(json, BuiltInOptions)![TestItem.AddressA]));
        Assert.That(result.Count, Is.EqualTo(1));
    }

    [Test]
    public void Mixed_case_keys_parse_to_the_same_address()
    {
        string json = $"{{\"{TestItem.AddressA.ToString().ToUpperInvariant().Replace("0X", "0x")}\":3}}";

        Assert.That(JsonSerializer.Deserialize<Dictionary<Address, int>>(json, Options)![TestItem.AddressA], Is.EqualTo(3));
    }

    [TestCase("{\"0x12\":1}")]
    [TestCase("{\"not-an-address\":1}")]
    [TestCase("[1]")]
    [TestCase("{\"0x0000000000000000000000000000000000000001\":")]
    public void Invalid_input_throws_what_the_built_in_converter_throws(string json)
    {
        System.Exception? expected = Capture(() => JsonSerializer.Deserialize<Dictionary<Address, int>>(json, BuiltInOptions));
        System.Exception? actual = Capture(() => JsonSerializer.Deserialize<Dictionary<Address, int>>(json, Options));

        Assert.That(expected, Is.Not.Null);
        Assert.That(actual?.GetType(), Is.EqualTo(expected!.GetType()));
    }

    private static System.Exception? Capture(System.Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (System.Exception e)
        {
            return e;
        }
    }
}
