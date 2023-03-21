// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    public class ConverterTestBase<T>
    {
        protected void TestConverter(T item, Func<T, T, bool> equalityComparer, JsonConverter<T> converter)
        {
            var options = new JsonSerializerOptions
            {
                Converters =
                {
                    converter
                }
            };

            string result = JsonSerializer.Serialize(item, options);

            T? deserialized = JsonSerializer.Deserialize<T>(result, options);

#pragma warning disable CS8604
            Assert.True(equalityComparer(item, deserialized));
#pragma warning restore CS8604
        }
    }
}
