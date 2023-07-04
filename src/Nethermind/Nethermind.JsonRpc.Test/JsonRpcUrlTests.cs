// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class JsonRpcUrlTests
    {
        [TestCase("http://127.0.0.1:1234|http|eth;web3;net", "http", "127.0.0.1", 1234, RpcEndpoint.Http, new string[] { "eth", "web3", "net" })]
        [TestCase("http://0.0.0.0|ws|eth ;web3; net", "http", "0.0.0.0", 80, RpcEndpoint.Ws, new string[] { "eth", "web3", "net" })]
        [TestCase("https://localhost:9876|HTTpS; wSs|eth;Web3;NET", "https", "localhost", 9876, RpcEndpoint.Http | RpcEndpoint.Ws, new string[] { "eth", "web3", "net" })]
        public void Parse_success(string packedUrlValue, string expectedScheme, string expectedHost, int expectedPort, RpcEndpoint expectedRpcEndpoint, string[] expectedEnabledModules)
        {
            JsonRpcUrl url = JsonRpcUrl.Parse(packedUrlValue);
            Assert.That(url.Scheme, Is.EqualTo(expectedScheme));
            Assert.That(url.Host, Is.EqualTo(expectedHost));
            Assert.That(url.Port, Is.EqualTo(expectedPort));
            Assert.That(url.RpcEndpoint, Is.EqualTo(expectedRpcEndpoint));
            CollectionAssert.AreEqual(expectedEnabledModules, url.EnabledModules, StringComparer.InvariantCultureIgnoreCase);
        }

        [TestCase(null, typeof(ArgumentNullException))]
        [TestCase("", typeof(FormatException))]
        [TestCase("127.0.0.1:1234|a|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:0|a|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:-1|a|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:1234||", typeof(FormatException))]
        [TestCase("http://127.0.0.1:1234/test|a|a", typeof(FormatException))]
        [TestCase("a://127.0.0.1:1234|a|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:1234|a|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:1234|ipc|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:1234|http|", typeof(FormatException))]
        public void Parse_fail(string packedUrlValue, Type expectedExceptionType) =>
            Assert.Throws(expectedExceptionType, () => JsonRpcUrl.Parse(packedUrlValue));
    }
}
