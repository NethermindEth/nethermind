//  Copyright (c) 2021 Demerzel Solutions Limited
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
        protected void TestRoundtrip<T>(T item, Func<T, T, bool> equalityComparer, JsonConverter<T> converter = null, string description = null)
        {
            IJsonSerializer serializer = BuildSerializer();
            if (converter != null)
            {
                serializer.RegisterConverter(converter);
            }

            string result = serializer.Serialize(item);
            T deserialized = serializer.Deserialize<T>(result);

            if (equalityComparer == null)
            {
                Assert.AreEqual(item, deserialized, description);
            }
            else
            {
                Assert.True(equalityComparer(item, deserialized), description);    
            }
        }
        
        protected void TestRoundtrip<T>(T item, JsonConverter<T> converter = null, string description = null)
        {
            TestRoundtrip(item, (a,b) => a.Equals(b), converter, description);
        }
        
        protected void TestRoundtrip<T>(T item, string description)
        {
            TestRoundtrip(item, null, null, description);
        }
        
        protected void TestRoundtrip<T>(T item, Func<T, T, bool> equalityComparer, string description = null)
        {
            TestRoundtrip(item, equalityComparer, null, description);
        }

        protected void TestRoundtrip<T>(string json, JsonConverter converter = null)
        {
            IJsonSerializer serializer = BuildSerializer();
            if (converter != null)
            {
                serializer.RegisterConverter(converter);
            }

            T deserialized = serializer.Deserialize<T>(json);
            string result = serializer.Serialize(deserialized);
            Assert.AreEqual(json, result);
        }

        private void TestToJson<T>(T item, JsonConverter<T> converter, string expectedResult)
        {
            IJsonSerializer serializer = BuildSerializer();
            if (converter != null)
            {
                serializer.RegisterConverter(converter);
            }

            string result = serializer.Serialize(item);
            Assert.AreEqual(expectedResult, result, result.Replace("\"", "\\\""));
        }

        protected void TestToJson<T>(T item, string expectedResult)
        {
            TestToJson(item, null, expectedResult);
        }

        private static IJsonSerializer BuildSerializer()
        {
            IJsonSerializer serializer = new EthereumJsonSerializer();
            serializer.RegisterConverters(EthModuleFactory.Converters);
            serializer.RegisterConverters(TraceModuleFactory.Converters);
            serializer.RegisterConverter(new BlockParameterConverter());
            return serializer;
        }
    }
}
