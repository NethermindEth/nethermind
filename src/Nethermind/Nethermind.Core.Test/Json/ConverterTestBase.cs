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

    protected void TestConverter<R>(R? item, JsonConverter<T> converter, Func<R, R, bool>? equalityComparer = null)
    {
        var options = new JsonSerializerOptions
        {
            Converters =
            {
                converter
            }
        };

        string result = JsonSerializer.Serialize(item, options);

        R? deserialized = JsonSerializer.Deserialize<R>(result, options);

#pragma warning disable CS8604
        if (equalityComparer != null)
            Assert.That(equalityComparer(item, deserialized), Is.True);
        else if (item is IEnumerable itemE && deserialized is IEnumerable deserializedE)
            Assert.That(deserializedE, Is.EquivalentTo(itemE));
        else
            Assert.That(deserialized, Is.EqualTo(item));
#pragma warning restore CS8604
    }
}
