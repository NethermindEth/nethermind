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
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class JsonRpcUrlTests
    {
        [TestCase("http://127.0.0.1:1234|http|eth,web3,net", "http", "127.0.0.1", 1234, RpcEndpoint.Http, new string[] { "eth", "web3", "net" })]
        [TestCase("http://0.0.0.0|ws|eth,web3,net", "http", "0.0.0.0", 80, RpcEndpoint.WebSocket, new string[] { "eth", "web3", "net" })]
        [TestCase("http://localhost:9876|HTTP,wS|eth,Web3,NET", "http", "localhost", 9876, RpcEndpoint.Http | RpcEndpoint.WebSocket, new string[] { "eth", "Web3", "NET" })]
        public void Parse_success(string packedUrlValue, string expectedScheme, string expectedHost, int expectedPort, RpcEndpoint expectedRpcEndpoint, string[] expectedEnabledModules)
        {
            JsonRpcUrl url = JsonRpcUrl.Parse(packedUrlValue);
            Assert.AreEqual(expectedScheme, url.Scheme);
            Assert.AreEqual(expectedHost, url.Host);
            Assert.AreEqual(expectedPort, url.Port);
            Assert.AreEqual(expectedRpcEndpoint, url.RpcEndpoint);
            CollectionAssert.AreEqual(expectedEnabledModules, url.EnabledModules);
        }

        [TestCase(null, typeof(ArgumentNullException))]
        [TestCase("", typeof(FormatException))]
        [TestCase("127.0.0.1:1234|a|a", typeof(UriFormatException))]
        [TestCase("http://127.0.0.1:0|a|a", typeof(UriFormatException))]
        [TestCase("http://127.0.0.1:-1|a|a", typeof(UriFormatException))]
        [TestCase("http://127.0.0.1:1234/test|a|a", typeof(UriFormatException))]
        [TestCase("https://127.0.0.1:1234|a|a", typeof(UriFormatException))]
        [TestCase("a://127.0.0.1:1234|a|a", typeof(UriFormatException))]
        [TestCase("http://127.0.0.1:1234|a|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:1234|http,a|a", typeof(FormatException))]
        [TestCase("http://127.0.0.1:1234|http|NotAModule", typeof(FormatException))]
        public void Parse_fail(string packedUrlValue, Type expectedExceptionType) =>
            Assert.Throws(expectedExceptionType, () => JsonRpcUrl.Parse(packedUrlValue));
    }
}
