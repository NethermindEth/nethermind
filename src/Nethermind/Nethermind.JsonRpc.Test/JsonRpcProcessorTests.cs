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
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.JsonRpc.Modules;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture(true)]
    [TestFixture(false)]
    public class JsonRpcProcessorTests
    {
        private readonly bool _returnErrors;
        private IFileSystem _fileSystem;
        private JsonRpcContext _context;

        private JsonRpcErrorResponse _errorResponse = new();
        
        public JsonRpcProcessorTests(bool returnErrors)
        {
            _returnErrors = returnErrors;
        }

        [SetUp]
        public void Initialize()
        {
            IJsonRpcService service = Substitute.For<IJsonRpcService>();
            service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>()).Returns(ci => _returnErrors ? (JsonRpcResponse) new JsonRpcErrorResponse {Id = ci.Arg<JsonRpcRequest>().Id} : new JsonRpcSuccessResponse {Id = ci.Arg<JsonRpcRequest>().Id});
            service.GetErrorResponse(0, null).ReturnsForAnyArgs(_errorResponse);
            service.Converters.Returns(new JsonConverter[] { new AddressConverter() }); // just to test converter loader

            _fileSystem = Substitute.For<IFileSystem>();

            /* we enable recorder always to have an easy smoke test for recording
             * and this is fine because recorder is non-critical component
             */
            JsonRpcConfig configWithRecorder = new() {RpcRecorderState = RpcRecorderState.All};

            _jsonRpcProcessor = new JsonRpcProcessor(service, new EthereumJsonSerializer(), configWithRecorder, _fileSystem, LimboLogs.Instance);
            _context = new JsonRpcContext(RpcEndpoint.Http);
        }

        private JsonRpcProcessor _jsonRpcProcessor;

        [Test]
        public async Task Can_process_guid_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":\"840b55c4-18b0-431c-be1d-6d22198b53f2\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual("840b55c4-18b0-431c-be1d-6d22198b53f2", result[0].Response.Id);
        }

        private Task<List<JsonRpcResult>> ProcessAsync(string request) => _jsonRpcProcessor.ProcessAsync(request, _context).ToListAsync();

        [Test]
        public async Task Can_process_non_hex_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":12345678901234567890,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(BigInteger.Parse("12345678901234567890"), result[0].Response.Id);
        }
        
        [Test]
        public async Task Can_process_hex_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":\"0xa1aa12434\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual("0xa1aa12434", result[0].Response.Id);
        }
        
        [Test]
        public async Task Can_process_int()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(67, result[0].Response.Id);
        }
        
        [Test]
        public async Task Can_process_uppercase_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"Params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(67, result[0].Response.Id);
            if (_returnErrors)
            {
                result[0].Response.Should().BeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                result[0].Response.Should().BeOfType<JsonRpcSuccessResponse>();
            }
        }
        
        
        [Test]
        public async Task Can_process_long_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":9223372036854775807,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(long.MaxValue, result[0].Response.Id);
        }
        
        [Test]
        public async Task Can_process_special_characters_in_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":\";\\\\\\\"\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(";\\\"", result[0].Response.Id);
        }
        
        [Test]
        public async Task Can_process_null_in_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":null,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(null, result[0].Response.Id);
        }
        
        [Test]
        public async Task Can_process_batch_request_with_nested_object_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]}]");
            result.Should().HaveCount(1);
            result[0].Responses.Should().NotBeNull();
            if (_returnErrors)
            {
                result[0].Responses.Should().AllBeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                result[0].Responses.Should().AllBeOfType<JsonRpcSuccessResponse>();
            }
        }
        
        [Test]
        public async Task Can_process_batch_request_with_nested_array_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}, 1]]}]");
            result.Should().HaveCount(1);
            result[0].Responses.Should().NotBeNull();
            if (_returnErrors)
            {
                result[0].Responses.Should().AllBeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                result[0].Responses.Should().AllBeOfType<JsonRpcSuccessResponse>();
            }
        }
        
        [Test]
        public async Task Can_process_batch_request_with_object_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"}},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"}}]");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].Response.Should().BeOfType<JsonRpcErrorResponse>();
        }
        
        [Test]
        public async Task Can_process_batch_request_with_value_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\"},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":\"0x668c24\"}]");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].Response.Should().BeOfType<JsonRpcErrorResponse>();
        }
        
        [Test]
        public async Task Can_process_batch_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}]");
            result.Should().HaveCount(1);
            result[0].Responses.Should().NotBeNull();
            result[0].Response.Should().BeNull();
        }
        
        [Test]
        public async Task Can_process_batch_request_with_some_params_missing()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\"}]");
            result.Should().HaveCount(1);
            result[0].Responses.Should().NotBeNull();
            result[0].Response.Should().BeNull();
        }

        [Test]
        public async Task Can_process_batch_request_with_two_requests()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}{\"id\":68,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].Responses.Should().BeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
            result[1].Response.Should().NotBeNull();
            result[1].Responses.Should().BeNull();
            result[1].Response.Should().NotBeSameAs(_errorResponse);
        }
        
        [Test]
        public async Task Can_process_batch_request_with_single_request_and_array_with_two()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}]");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
            result[0].Responses.Should().BeNull();
            result[1].Response.Should().BeNull();
            result[1].Responses.Should().NotBeNull();
            result[1].Responses.Should().HaveCount(2);
            Assert.IsTrue(result[1].Responses.All(r => r != _errorResponse));
        }
        
        [Test]
        public async Task Can_process_batch_request_with_second_not_closed_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}{\"id\":68,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].Responses.Should().BeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
            result[1].Response.Should().NotBeNull();
            result[1].Responses.Should().BeNull();
            result[1].Response.Should().BeSameAs(_errorResponse);
        }
        
        [Test]
        public async Task Can_process_batch_request_with_single_request_and_incorrect()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}{aaa}");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].Responses.Should().BeNull();
            result[1].Response.Should().BeSameAs(_errorResponse);
            result[1].Responses.Should().BeNull();
        }

        [Test]
        public async Task Can_handle_invalid_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("invalid");
            result.Should().HaveCount(1);
            result[0].Response.Should().BeSameAs(_errorResponse);
        }
        
        [Test]
        public async Task Can_handle_empty_array_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[]");
            result.Should().HaveCount(1);
            result[0].Response.Should().BeNull();
            result[0].Responses.Should().NotBeNull();
            Assert.IsTrue(result[0].Responses.All(r => r != _errorResponse));
        }
        
        [Test]
        public async Task Can_handle_empty_object_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{}");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].Responses.Should().BeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
        }
        
        [Test]
        public async Task Can_handle_array_of_empty_requests()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{},{},{}]");
            result.Should().HaveCount(1);
            result[0].Response.Should().BeNull();
            result[0].Responses.Should().NotBeNull();
            result[0].Responses.Should().HaveCount(3);
            Assert.IsTrue(result[0].Responses.All(r => r != _errorResponse));
        }
        
        [Test]
        public async Task Can_handle_value_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("\"aaa\"");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].Responses.Should().BeNull();
            result[0].Response.Should().BeSameAs(_errorResponse);
        }
        
        [Test]
        public async Task Can_handle_null_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("null");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].Responses.Should().BeNull();
            result[0].Response.Should().BeSameAs(_errorResponse);
        }
        
        [Test]
        public void Cannot_accept_null_file_system()
        {
            Assert.Throws<ArgumentNullException>(() => new JsonRpcProcessor(Substitute.For<IJsonRpcService>(),
                Substitute.For<IJsonSerializer>(),
                Substitute.For<IJsonRpcConfig>(),
                null, LimboLogs.Instance));
        }
    }
}
