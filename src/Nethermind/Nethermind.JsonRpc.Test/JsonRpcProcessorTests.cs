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
using System.IO.Abstractions;
using System.Numerics;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
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
            JsonRpcConfig configWithRecorder = new JsonRpcConfig {RpcRecorderState = RpcRecorderState.All};

            _jsonRpcProcessor = new JsonRpcProcessor(service, new EthereumJsonSerializer(), configWithRecorder, _fileSystem, LimboLogs.Instance);
        }

        private JsonRpcProcessor _jsonRpcProcessor;

        [Test]
        public async Task Can_process_guid_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":\"840b55c4-18b0-431c-be1d-6d22198b53f2\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual("840b55c4-18b0-431c-be1d-6d22198b53f2", result.Response.Id);
        }

        [Test]
        public async Task Can_process_non_hex_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":12345678901234567890,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual(BigInteger.Parse("12345678901234567890"), result.Response.Id);
        }

        [Test]
        public async Task Can_process_hex_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":\"0xa1aa12434\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual("0xa1aa12434", result.Response.Id);
        }

        [Test]
        public async Task Can_process_int()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual(67, result.Response.Id);
        }

        [Test]
        public async Task Can_process_uppercase_params()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"Params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual(67, result.Response.Id);
            if (_returnErrors)
            {
                result.Response.Should().BeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                result.Response.Should().BeOfType<JsonRpcSuccessResponse>();
            }
        }

        
        [Test]
        public async Task Can_process_long_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":9223372036854775807,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual(long.MaxValue, result.Response.Id);
        }

        [Test]
        public async Task Can_process_special_characters_in_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":\";\\\\\\\"\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual(";\\\"", result.Response.Id);
        }

        [Test]
        public async Task Can_process_null_in_ids()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{\"id\":null,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}", JsonRpcContext.Http);
            Assert.AreEqual(null, result.Response.Id);
        }

        [Test]
        public async Task Can_process_batch_request_with_nested_object_params()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]}]", JsonRpcContext.Http);
            result.Responses.Should().NotBeNull();
            if (_returnErrors)
            {
                result.Responses.Should().AllBeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                result.Responses.Should().AllBeOfType<JsonRpcSuccessResponse>();
            }
        }

        [Test]
        public async Task Can_process_batch_request_with_nested_array_params()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}, 1]]}]", JsonRpcContext.Http);
            result.Responses.Should().NotBeNull();
            if (_returnErrors)
            {
                result.Responses.Should().AllBeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                result.Responses.Should().AllBeOfType<JsonRpcSuccessResponse>();
            }
        }

        [Test]
        public async Task Can_process_batch_request_with_object_params()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"}},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"}}]", JsonRpcContext.Http);
            result.Response.Should().NotBeNull();
            result.Response.Should().BeOfType<JsonRpcErrorResponse>();
        }

        [Test]
        public async Task Can_process_batch_request_with_value_params()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\"},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":\"0x668c24\"}]", JsonRpcContext.Http);
            result.Response.Should().NotBeNull();
            result.Response.Should().BeOfType<JsonRpcErrorResponse>();
        }

        [Test]
        public async Task Can_process_batch_request()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}]", JsonRpcContext.Http);
            result.Responses.Should().NotBeNull();
            result.Response.Should().BeNull();
        }

        [Test]
        public async Task Can_process_batch_request_with_some_params_missing()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\"}]", JsonRpcContext.Http);
            result.Responses.Should().NotBeNull();
            result.Response.Should().BeNull();
        }

        private JsonRpcErrorResponse _errorResponse = new JsonRpcErrorResponse();

        [Test]
        public async Task Can_handle_invalid_request()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("invalid", JsonRpcContext.Http);
            result.Response.Should().BeSameAs(_errorResponse);
        }

        [Test]
        public async Task Can_handle_empty_array_request()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("[]", JsonRpcContext.Http);
            result.Response.Should().BeNull();
            result.Responses.Should().NotBeNull();
        }

        [Test]
        public async Task Can_handle_empty_object_request()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("{}", JsonRpcContext.Http);
            result.Response.Should().NotBeNull();
            result.Responses.Should().BeNull();
        }

        [Test]
        public async Task Can_handle_value_request()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("\"aaa\"", JsonRpcContext.Http);
            result.Response.Should().NotBeNull();
            result.Responses.Should().BeNull();
            result.Response.Should().BeSameAs(_errorResponse);
        }

        [Test]
        public async Task Can_handle_null_request()
        {
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync("null", JsonRpcContext.Http);
            result.Response.Should().NotBeNull();
            result.Responses.Should().BeNull();
            result.Response.Should().BeSameAs(_errorResponse);
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
