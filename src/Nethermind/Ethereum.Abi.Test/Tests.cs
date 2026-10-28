// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Ethereum.Abi.Test
{
    public class Tests
    {
        /// <summary>Typed signature of each test in <c>basic_abi_tests.json</c>, with the conversion of its JSON arguments.</summary>
        private static IEnumerable<TestCaseData> EncodingTests()
        {
            yield return Case("GithubWikiTest",
                new AbiSignature().With(AbiType.UInt256, AbiType.Array(AbiType.UInt32), AbiType.BytesM(10), AbiType.Bytes),
                static args => (ToUInt256(args[0]), args[1].EnumerateArray().Select(static e => e.GetUInt32()).ToArray(), ToUtf8(args[2]), ToUtf8(args[3])));
            yield return Case("SingleInteger",
                new AbiSignature().With(AbiType.UInt256),
                static args => ToUInt256(args[0]));
            yield return Case("IntegerAndAddress",
                new AbiSignature().With(AbiType.UInt256, AbiType.Address),
                static args => (ToUInt256(args[0]), new Address(args[1].GetString()!)));
        }

        [TestCaseSource(nameof(EncodingTests))]
        public void Test_abi_encoding(string testName, string types, Func<JsonElement[], byte[]> encode)
        {
            AbiTest abiTest = LoadTests()[testName];

            using (Assert.EnterMultipleScope())
            {
                Assert.That($"({string.Join(',', abiTest.Types)})", Is.EqualTo(types));
                Assert.That(encode(abiTest.Args), Is.EqualTo(Bytes.FromHexString(abiTest.Result)));
            }
        }

        [Test]
        public void Encoding_tests_cover_every_fixture_test() =>
            Assert.That(EncodingTests().Select(static t => t.TestName), Is.EquivalentTo(LoadTests().Keys));

        private static TestCaseData Case<T>(string testName, AbiSignature<T> signature, Func<JsonElement[], T> toArgs) =>
            new TestCaseData(testName, signature.Abi.Name, (Func<JsonElement[], byte[]>)(args => AbiCodec.Encode(signature, toArgs(args))))
                .SetName(testName);

        private static UInt256 ToUInt256(JsonElement element) => element.GetUInt64();

        private static byte[] ToUtf8(JsonElement element) => Encoding.UTF8.GetBytes(element.GetString()!);

        private static Dictionary<string, AbiTest> LoadTests()
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
                    TestContext.Out.WriteLine($"Could not find test in {potentialLocation}");
                }
            }

            return JsonSerializer.Deserialize<Dictionary<string, AbiTest>>(text);
        }
    }
}
