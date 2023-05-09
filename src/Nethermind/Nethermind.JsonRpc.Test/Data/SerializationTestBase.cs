// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;

using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    public class SerializationTestBase
    {
        protected void TestRoundtrip<T>(T item, Func<T, T, bool>? equalityComparer, JsonConverter<T>? converter = null, string? description = null)
        {
            IJsonSerializer serializer = BuildSerializer();
            //if (converter is not null)
            //{
            //    serializer.RegisterConverter(converter);
            //}

            string result = serializer.Serialize(item);
            T deserialized = serializer.Deserialize<T>(result);

            if (equalityComparer is null)
            {
                Assert.That(deserialized, Is.EqualTo(item), description);
            }
            else
            {
                Assert.True(equalityComparer(item, deserialized), description);
            }
        }

        protected void TestRoundtrip<T>(T item, JsonConverter<T>? converter = null, string? description = null)
        {
            TestRoundtrip(item, (a, b) => a!.Equals(b), converter, description);
        }

        protected void TestRoundtrip<T>(T item, string description)
        {
            TestRoundtrip(item, null, null, description);
        }

        protected void TestRoundtrip<T>(T item, Func<T, T, bool>? equalityComparer, string? description = null)
        {
            TestRoundtrip(item, equalityComparer, null, description);
        }

        protected void TestRoundtrip<T>(string json, JsonConverter? converter = null)
        {
            IJsonSerializer serializer = BuildSerializer();
            //if (converter is not null)
            //{
            //    serializer.RegisterConverter(converter);
            //}

            T deserialized = serializer.Deserialize<T>(json);
            string result = serializer.Serialize(deserialized);
            Assert.That(result, Is.EqualTo(json));
        }

        private void TestToJson<T>(T item, JsonConverter<T>? converter, string expectedResult)
        {
            IJsonSerializer serializer = BuildSerializer();

            string result = serializer.Serialize(item);
            Assert.AreEqual(expectedResult.Replace("+", "\\u002B"), result, result.Replace("\"", "\\\""));
        }

        protected void TestToJson<T>(T item, string expectedResult)
        {
            TestToJson(item, null, expectedResult);
        }

        private static IJsonSerializer BuildSerializer()
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();
            return serializer;
        }
    }
}
