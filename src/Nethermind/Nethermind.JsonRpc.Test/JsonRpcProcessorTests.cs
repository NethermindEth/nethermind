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

using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Core.Json;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcProcessorTests
    {
        [SetUp]
        public void Initialize()
        {
            IJsonRpcService service = Substitute.For<IJsonRpcService>();
            service.SendRequestAsync(Arg.Any<JsonRpcRequest>()).Returns(ci => new JsonRpcResponse {Id = ci.Arg<JsonRpcRequest>().Id, Result = null, JsonRpc = ci.Arg<JsonRpcRequest>().JsonRpc});
            _jsonRpcProcessor = new JsonRpcProcessor(service, new EthereumJsonSerializer(), new JsonRpcConfig(), LimboLogs.Instance);
        }

        private JsonRpcProcessor _jsonRpcProcessor;

        [Test]
        public async Task Can_process_guid_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":\"840b55c4-18b0-431c-be1d-6d22198b53f2\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            Assert.AreEqual("840b55c4-18b0-431c-be1d-6d22198b53f2", result.Response.Id);
        }

        [Test]
        public async Task Can_process_non_hex_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":12345678901234567890,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            Assert.AreEqual(BigInteger.Parse("12345678901234567890"), result.Response.Id);
        }

        [Test]
        public async Task Can_process_hex_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":\"0xa1aa12434\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            Assert.AreEqual("0xa1aa12434", result.Response.Id);
        }
        
        [Test]
        public async Task Can_process_int()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            Assert.AreEqual(67,result.Response.Id);
        }
        
        [Test]
        public async Task Can_process_long()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":9223372036854775807,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            Assert.AreEqual(long.MaxValue,result.Response.Id);
        }
        
        [Test]
        public async Task Can_process_special_characters()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":\";\\\\\\\"\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            Assert.AreEqual(";\\\"",result.Response.Id);
        }
    }
}