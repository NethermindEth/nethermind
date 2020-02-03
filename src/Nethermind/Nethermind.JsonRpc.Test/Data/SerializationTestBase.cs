//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NSubstitute;
using NUnit.Framework;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc.Test.Data
{
    public class SerializationTestBase
    {
        protected void TestConverter<T>(T item, Func<T, T, bool> equalityComparer, JsonConverter<T> converter)
        {
            JsonSerializer serializer = new JsonSerializer();
            serializer.Converters.Add(converter);
            StringBuilder builder = new StringBuilder();
            StringWriter writer = new StringWriter(builder);
            serializer.Serialize(writer, item);
            string result = builder.ToString();
            JsonReader reader = new JsonTextReader(new StringReader(result));
            T deserialized = serializer.Deserialize<T>(reader);

            Assert.True(equalityComparer(item, deserialized));
        }
        
        protected void TestConverter<T>(T item, JsonConverter<T> converter, string expectedResult)
        {
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            serializer.RegisterConverter(converter);
            string result = serializer.Serialize(item);
            Assert.AreEqual(expectedResult, result, result.Replace("\"", "\\\""));
        }

        protected void TestSerialization<T>(T item, Func<T, T, bool> equalityComparer, string description = "")
        {
            JsonSerializer serializer = BuildSerializer<T>();

            StringBuilder builder = new StringBuilder();
            StringWriter writer = new StringWriter(builder);
            serializer.Serialize(writer, item);
            string result = builder.ToString();
            JsonReader reader = new JsonTextReader(new StringReader(result));
            T deserialized = serializer.Deserialize<T>(reader);

            Assert.True(equalityComparer(item, deserialized), description);
        }

        private static JsonSerializer BuildSerializer<T>()
        {
            JsonSerializerSettings jsonSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
            
            JsonSerializer serializer = JsonSerializer.Create(jsonSettings);
            foreach (JsonConverter converter in EthModuleFactory.Converters)
            {
                serializer.Converters.Add(converter);
            }            
            
            foreach (JsonConverter converter in TraceModuleFactory.Converters)
            {
                serializer.Converters.Add(converter);
            }

            foreach (JsonConverter converter in EthereumJsonSerializer.BasicConverters)
            {
                serializer.Converters.Add(converter);
            }
            
            serializer.Converters.Add(new BlockParameterConverter());

            return serializer;
        }

        protected void TestOneWaySerialization<T>(T item, string expectedResult)
        {
            JsonSerializer serializer = BuildSerializer<T>();

            StringBuilder builder = new StringBuilder();
            StringWriter writer = new StringWriter(builder);
            serializer.Serialize(writer, item);
            string result = builder.ToString();
            Assert.AreEqual(expectedResult, result, result.Replace("\"", "\\\""));
        }
    }
}