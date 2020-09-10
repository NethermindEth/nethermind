/*
 * Copyright (c) 2020 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ethereum.Abi
{
    public class Tests
    {
        [SetUp]
        public void Setup()
        {
        }
        
        private static Dictionary<string, AbiType> _abiTypes = new Dictionary<string, AbiType>
        {
            {"uint256", AbiType.UInt256},
            {"uint32[]", new AbiArray(AbiType.UInt32)},
            {"bytes10", new AbiBytes(10)},
            {"bytes", AbiType.DynamicBytes},
            {"address", AbiType.Address},
        };

        [Test]
        public void Test_abi_encoding()
        {
            string text = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "basic_abi_tests.json"));
            Dictionary<string, AbiTest> tests = JsonConvert.DeserializeObject<Dictionary<string, AbiTest>>(text);
            foreach ((string testName, AbiTest abiTest) in tests)
            {
                AbiSignature signature = new AbiSignature(
                    testName,
                    abiTest.Types.Select(t => _abiTypes[t]).ToArray());
                
                AbiEncoder encoder = new AbiEncoder();
                byte[] abi = encoder.Encode(AbiEncodingStyle.None, signature, abiTest.Args.Select(JsonToObject).ToArray());
                abi.Should().BeEquivalentTo(Bytes.FromHexString(abiTest.Result));
            }
        }
        
        public object JsonToObject(object jsonObject)
        {
            if (jsonObject is JArray array)
            {
                return array.Select(t => t.Value<long>()).ToArray();
            }
            
            return jsonObject;
        }
    }
}