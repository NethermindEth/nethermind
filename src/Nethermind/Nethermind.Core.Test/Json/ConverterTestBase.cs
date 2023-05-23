// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    public class ConverterTestBase<T>
    {
        protected void TestConverter(T item, Func<T, T, bool> equalityComparer, JsonConverter<T> converter)
        {
            JsonSerializer serializer = new();
            serializer.Converters.Add(converter);
            StringBuilder builder = new();
            StringWriter writer = new(builder);
            serializer.Serialize(writer, item);
            string result = builder.ToString();
            JsonReader reader = new JsonTextReader(new StringReader(result));
            T? deserialized = serializer.Deserialize<T>(reader);

#pragma warning disable CS8604
            Assert.True(equalityComparer(item, deserialized));
#pragma warning restore CS8604
        }
    }
}
