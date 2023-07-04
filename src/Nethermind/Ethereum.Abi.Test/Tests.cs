// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            { "uint256", AbiType.UInt256 },
            { "uint32[]", new AbiArray(AbiType.UInt32) },
            { "bytes10", new AbiBytes(10) },
            { "bytes", AbiType.DynamicBytes },
            { "address", AbiType.Address },
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
