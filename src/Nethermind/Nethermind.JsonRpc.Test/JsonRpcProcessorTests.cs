// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.JsonRpc.Modules;

using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture(true)]
    [TestFixture(false)]
    public class JsonRpcProcessorTests
    {
        private readonly bool _returnErrors;
        private readonly JsonRpcErrorResponse _errorResponse = new();

        public JsonRpcProcessorTests(bool returnErrors)
        {
            _returnErrors = returnErrors;
        }

        private JsonRpcProcessor Initialize(JsonRpcConfig? config = null)
        {
            IJsonRpcService service = Substitute.For<IJsonRpcService>();
            service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>()).Returns(ci => _returnErrors ? new JsonRpcErrorResponse { Id = ci.Arg<JsonRpcRequest>().Id } : new JsonRpcSuccessResponse { Id = ci.Arg<JsonRpcRequest>().Id });
            service.GetErrorResponse(0, null!).ReturnsForAnyArgs(_errorResponse);
            service.GetErrorResponse(null!, 0, null!, null!).ReturnsForAnyArgs(_errorResponse);

            IFileSystem fileSystem = Substitute.For<IFileSystem>();

            /* we enable recorder always to have an easy smoke test for recording
             * and this is fine because recorder is non-critical component
             */
            config ??= new JsonRpcConfig();
            config.RpcRecorderState = RpcRecorderState.All;

            return new JsonRpcProcessor(service, config, fileSystem, LimboLogs.Instance);
        }

        [Test]
        public async Task Can_process_guid_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":\"840b55c4-18b0-431c-be1d-6d22198b53f2\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual("840b55c4-18b0-431c-be1d-6d22198b53f2", result[0].Response!.Id);
        }

        private ValueTask<List<JsonRpcResult>> ProcessAsync(string request, JsonRpcContext? context = null, JsonRpcConfig? config = null) =>
            Initialize(config).ProcessAsync(request, context ?? new JsonRpcContext(RpcEndpoint.Http)).ToListAsync();

        [Test]
        public async Task Can_process_non_hex_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":12345678901234567890,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(BigInteger.Parse("12345678901234567890"), result[0].Response!.Id);
        }

        [Test]
        public async Task Can_process_hex_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":\"0xa1aa12434\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual("0xa1aa12434", result[0].Response!.Id);
        }

        [Test]
        public async Task Can_process_int()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(67, result[0].Response!.Id);
        }

        [Test]
        public async Task Can_process_uppercase_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"Params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(67, result[0].Response!.Id);
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
            Assert.AreEqual(long.MaxValue, result[0].Response!.Id);
        }

        [Test]
        public async Task Can_process_special_characters_in_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":\";\\\\\\\"\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(";\\\"", result[0].Response!.Id);
        }

        [Test]
        public async Task Can_process_null_in_ids()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":null,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(1);
            Assert.AreEqual(null, result[0].Response!.Id);
        }

        [Test]
        public async Task Can_process_batch_request_with_nested_object_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]}]");
            result.Should().HaveCount(1);
            result[0].BatchedResponses.Should().NotBeNull();
            if (_returnErrors)
            {
                (await result[0].BatchedResponses!.Select(r => r.Response).ToListAsync()).Should().AllBeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                (await result[0].BatchedResponses!.Select(r => r.Response).ToListAsync()).Should().AllBeOfType<JsonRpcSuccessResponse>();
            }
        }

        [Test]
        public async Task Can_process_batch_request_with_nested_array_params()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}]]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[[{\"a\":\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"b\":\"0x668c24\"}, 1]]}]");
            result.Should().HaveCount(1);
            result[0].BatchedResponses.Should().NotBeNull();
            if (_returnErrors)
            {
                (await result[0].BatchedResponses!.Select(r => r.Response).ToListAsync()).Should().AllBeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                (await result[0].BatchedResponses!.Select(r => r.Response).ToListAsync()).Should().AllBeOfType<JsonRpcSuccessResponse>();
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
            result[0].Response.Should().BeNull();
            result[0].BatchedResponses.Should().NotBeNull();
            var resultList = await result[0].BatchedResponses!.ToListAsync();
            resultList.Should().HaveCount(2);
            Assert.IsTrue(resultList.All(r => r.Response != _errorResponse));
        }

        [Test]
        public async Task Can_process_batch_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}]");
            result.Should().HaveCount(1);
            result[0].BatchedResponses.Should().NotBeNull();
            result[0].Response.Should().BeNull();
        }

        [Test]
        public async Task Can_process_batch_request_with_some_params_missing()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\"}]");
            result.Should().HaveCount(1);
            result[0].BatchedResponses.Should().NotBeNull();
            result[0].Response.Should().BeNull();
        }

        [Test]
        public async Task Can_process_batch_request_with_two_requests()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}{\"id\":68,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].BatchedResponses.Should().BeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
            result[1].Response.Should().NotBeNull();
            result[1].BatchedResponses.Should().BeNull();
            result[1].Response.Should().NotBeSameAs(_errorResponse);
        }

        [Test]
        public async Task Can_process_batch_request_with_single_request_and_array_with_two()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}]");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
            result[0].BatchedResponses.Should().BeNull();
            result[1].Response.Should().BeNull();
            result[1].BatchedResponses.Should().NotBeNull();
            List<JsonRpcResult.Entry> resultList = await result[1].BatchedResponses!.ToListAsync();
            resultList.Should().HaveCount(2);
            Assert.IsTrue(resultList.All(r => r.Response != _errorResponse));
        }

        [Test]
        public async Task Can_process_batch_request_with_second_not_closed_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}{\"id\":68,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].BatchedResponses.Should().BeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
            result[1].Response.Should().NotBeNull();
            result[1].BatchedResponses.Should().BeNull();
            result[1].Response.Should().BeSameAs(_errorResponse);
        }

        [Test]
        public async Task Can_process_batch_request_with_single_request_and_incorrect()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}{aaa}");
            result.Should().HaveCount(2);
            result[0].Response.Should().NotBeNull();
            result[0].BatchedResponses.Should().BeNull();
            result[1].Response.Should().BeSameAs(_errorResponse);
            result[1].BatchedResponses.Should().BeNull();
        }

        [Test]
        public async Task Will_return_error_when_batch_request_is_too_large()
        {
            StringBuilder request = new();
            int maxBatchSize = new JsonRpcConfig().MaxBatchSize;
            request.Append("[");
            for (int i = 0; i < maxBatchSize + 1; i++)
            {
                if (i != 0) request.Append(",");
                request.Append(
                    "{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            }
            request.Append("]");

            IList<JsonRpcResult> result = await ProcessAsync(request.ToString());
            result.Should().HaveCount(1);
            result[0].Response.Should().BeAssignableTo<JsonRpcErrorResponse>();
        }

        [Test]
        public async Task Will_not_return_error_when_batch_request_is_too_large_but_endpoint_is_authenticated()
        {
            StringBuilder request = new();
            int maxBatchSize = new JsonRpcConfig().MaxBatchSize;
            request.Append("[");
            for (int i = 0; i < maxBatchSize + 1; i++)
            {
                if (i != 0) request.Append(",");
                request.Append(
                    "{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
            }
            request.Append("]");

            JsonRpcUrl url = new(string.Empty, string.Empty, 0, RpcEndpoint.Http, true, Array.Empty<string>());
            JsonRpcContext context = new(RpcEndpoint.Http, url: url);
            IList<JsonRpcResult> result = await ProcessAsync(request.ToString(), context, new JsonRpcConfig() { MaxBatchResponseBodySize = 1 });
            result.Should().HaveCount(1);
            List<JsonRpcResult.Entry> batchedResults = await result[0].BatchedResponses!.ToListAsync();
            batchedResults.Should().HaveCount(maxBatchSize + 1);
            batchedResults.Should().AllSatisfy(rpcResult =>
                rpcResult.Response.Should().BeOfType(_returnErrors ? typeof(JsonRpcErrorResponse) : typeof(JsonRpcSuccessResponse))
            );
        }

        [Test]
        public async Task Can_process_batch_request_with_result_limit([Values(false, true)] bool limit)
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]},{\"id\":68,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}]");
            result[0].IsCollection.Should().BeTrue();
            result[0].BatchedResponses.Should().NotBeNull();
            JsonRpcBatchResultAsyncEnumerator enumerator = result[0].BatchedResponses!.GetAsyncEnumerator(CancellationToken.None);
            (await enumerator.MoveNextAsync()).Should().BeTrue();
            if (_returnErrors)
            {
                enumerator.Current.Response.Should().BeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                enumerator.Current.Response.Should().NotBeOfType<JsonRpcErrorResponse>();
            }

            enumerator.IsStopped = limit; // limiting
            (await enumerator.MoveNextAsync()).Should().BeTrue();
            if (limit || _returnErrors)
            {
                enumerator.Current.Response.Should().BeOfType<JsonRpcErrorResponse>();
            }
            else
            {
                enumerator.Current.Response.Should().NotBeOfType<JsonRpcErrorResponse>();
            }

            (await enumerator.MoveNextAsync()).Should().BeFalse();
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
            result[0].BatchedResponses.Should().NotBeNull();
            Assert.IsTrue((await result[0].BatchedResponses!.ToListAsync()).All(r => r.Response != _errorResponse));
        }

        [Test]
        public async Task Can_handle_empty_object_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("{}");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].BatchedResponses.Should().BeNull();
            result[0].Response.Should().NotBeSameAs(_errorResponse);
        }

        [Test]
        public async Task Can_handle_array_of_empty_requests()
        {
            IList<JsonRpcResult> result = await ProcessAsync("[{},{},{}]");
            result.Should().HaveCount(1);
            result[0].Response.Should().BeNull();
            result[0].BatchedResponses.Should().NotBeNull();
            IList<JsonRpcResult.Entry> resultList = (await result[0].BatchedResponses!.ToListAsync());
            resultList.Should().HaveCount(3);
            Assert.IsTrue(resultList.All(r => r.Response != _errorResponse));
        }

        [Test]
        public async Task Can_handle_value_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("\"aaa\"");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].BatchedResponses.Should().BeNull();
            result[0].Response.Should().BeSameAs(_errorResponse);
        }

        [Test]
        public async Task Can_handle_null_request()
        {
            IList<JsonRpcResult> result = await ProcessAsync("null");
            result.Should().HaveCount(1);
            result[0].Response.Should().NotBeNull();
            result[0].BatchedResponses.Should().BeNull();
            result[0].Response.Should().BeSameAs(_errorResponse);
        }

        [Test]
        public void Cannot_accept_null_file_system()
        {
            Assert.Throws<ArgumentNullException>(() => new JsonRpcProcessor(Substitute.For<IJsonRpcService>(),
                Substitute.For<IJsonRpcConfig>(),
                null, LimboLogs.Instance));
        }
    }
}
