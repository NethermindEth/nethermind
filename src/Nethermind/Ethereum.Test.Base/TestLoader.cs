// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

            if (input is JArray)
            {
                input = ((JArray)input).Select(PrepareInput).ToArray();
            }

            JToken token = input as JToken;
            if (token != null)
            {
                if (token.Type == JTokenType.String)
                {
                    return token.Value<string>();
                }

                if (token.Type == JTokenType.Integer)
                {
                    return token.Value<long>();
                }
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

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                Assert.NotNull(stream);
                using (StreamReader reader = new(stream))
                {
                    string testJson = reader.ReadToEnd();
                    TContainer testSpecs =
                        JsonConvert.DeserializeObject<TContainer>(testJson);
                    return testExtractor(testSpecs);
                }
            }
        }
    }
}
