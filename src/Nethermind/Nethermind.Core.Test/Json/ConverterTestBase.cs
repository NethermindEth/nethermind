// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class ConverterTestBase<T>
{
    protected void TestConverter(T? item, Func<T, T, bool> equalityComparer, JsonConverter<T> converter) =>
        TestConverter(item, converter, equalityComparer);

    protected void TestConverter<TItem>(TItem? item, JsonConverter<T> converter, Func<TItem, TItem, bool>? equalityComparer = null)
    {
        JsonSerializerOptions options = BuildOptions(converter);

        AssertRoundtrip(item, JsonSerializer.Serialize(item, options), options, equalityComparer);
    }

    /// <summary>Asserts the exact serialized JSON before the roundtrip check.</summary>
    protected void TestConverter<TItem>(TItem? item, string expectedJson, JsonConverter<T> converter, Func<TItem, TItem, bool>? equalityComparer = null)
    {
        JsonSerializerOptions options = BuildOptions(converter);

        string result = JsonSerializer.Serialize(item, options);
        Assert.That(result, Is.EqualTo(expectedJson), result.Replace("\"", "\\\""));

        AssertRoundtrip(item, result, options, equalityComparer);
    }

    private static JsonSerializerOptions BuildOptions(JsonConverter<T> converter) => new()
    {
        Converters =
        {
            converter
        }
    };

    private static void AssertRoundtrip<TItem>(TItem? item, string json, JsonSerializerOptions options, Func<TItem, TItem, bool>? equalityComparer)
    {
        TItem? deserialized = JsonSerializer.Deserialize<TItem>(json, options);

#pragma warning disable CS8604
        if (equalityComparer is not null)
            Assert.That(equalityComparer(item, deserialized), Is.True);
        else if (item is IEnumerable itemE && deserialized is IEnumerable deserializedE)
            Assert.That(deserializedE, Is.EquivalentTo(itemE));
        else
            Assert.That(deserialized, Is.EqualTo(item));
#pragma warning restore CS8604
    }
}
