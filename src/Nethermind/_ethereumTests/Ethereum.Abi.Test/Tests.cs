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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ethereum.Abi.Test
{
    public class Tests
    {
        [SetUp]
        public void Setup()
        {
        }
        
        private static Dictionary<string, AbiType> _abiTypes = new()
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
            string text = string.Empty;
            
            string[] potentialLocations = new string[]
            {
                Path.Combine(TestContext.CurrentContext.TestDirectory, "basic_abi_tests.json"),
                Path.Combine(TestContext.CurrentContext.WorkDirectory, "basic_abi_tests.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? string.Empty, "basic_abi_tests.json"),
                Path.Combine(AppDomain.CurrentDomain.DynamicDirectory ?? string.Empty, "basic_abi_tests.json"),
            };

            foreach (string potentialLocation in potentialLocations)
            {
                try
                {
                    text = File.ReadAllText(potentialLocation);
                    break;
                }
                catch (IOException)
                {
                    TestContext.WriteLine($"Could not find test in {potentialLocation}");
                }    
            }

            Dictionary<string, AbiTest> tests = JsonConvert.DeserializeObject<Dictionary<string, AbiTest>>(text);
            foreach ((string testName, AbiTest abiTest) in tests)
            {
                AbiSignature signature = new(
                    testName,
                    abiTest.Types.Select(t => _abiTypes[t]).ToArray());
                
                AbiEncoder encoder = new();
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