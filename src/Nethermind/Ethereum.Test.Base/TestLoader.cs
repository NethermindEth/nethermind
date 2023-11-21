// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using NUnit.Framework;

namespace Ethereum.Test.Base
{
    public static class TestLoader
    {
        public static object PrepareInput(object input)
        {
            string s = input as string;
            if (s != null && s.StartsWith("#"))
            {
                BigInteger bigInteger = BigInteger.Parse(s.Substring(1));
                input = bigInteger;
            }

            JsonElement token = (JsonElement)input;

            if (token.ValueKind == JsonValueKind.Array)
            {
                int length = token.GetArrayLength();
                object[] array = new object[length];
                for (int i = 0; i < array.Length; i++)
                {
                    array[i] = PrepareInput(token[i]);
                }

                input = array;
            }

            if (token.ValueKind == JsonValueKind.String)
            {
                return token.GetString();
            }

            if (token.ValueKind == JsonValueKind.Number)
            {
                return token.GetInt64();
            }

            return input;
        }

        public static IEnumerable<TTest> LoadFromFile<TContainer, TTest>(
            string testFileName,
            Func<TContainer, IEnumerable<TTest>> testExtractor)
        {
            Assembly assembly = typeof(TTest).Assembly;
            string[] resourceNames = assembly.GetManifestResourceNames();
            string resourceName = resourceNames.SingleOrDefault(r => r.Contains(testFileName));
            if (resourceName == null)
            {
                throw new ArgumentException($"Cannot find test resource: {testFileName}");
            }

            var jsonOptions = new JsonSerializerOptions()
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                Assert.NotNull(stream);
                using (StreamReader reader = new(stream))
                {
                    string testJson = reader.ReadToEnd();
                    TContainer testSpecs =
                        JsonSerializer.Deserialize<TContainer>(testJson, jsonOptions);
                    return testExtractor(testSpecs);
                }
            }
        }
    }
}
