// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using NSubstitute;
using RpcEndpoint = Nethermind.LightClient.Rpc.RpcEndpoint;
using RpcTransport = Nethermind.JsonRpc.Modules.RpcEndpoint;

namespace Nethermind.LightClient.Test;

public class RpcModuleTests
{
    private static readonly Address Address = new("0x1000000000000000000000000000000000000001");
    private static readonly VerifiedHead Head = new(400, 200, Keccak.Compute("typed head"), Keccak.Compute("typed state"));

    [Test]
    public async Task Shared_service_returns_a_typed_verified_result_with_the_original_id()
    {
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        source.GetAccountAsync(Head, Address, Arg.Any<CancellationToken>()).Returns(Task.FromResult(new Account(7, 42)));
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(backend, NullLogManager.Instance);
        JsonRpcRequest request = Request("eth_getBalance", JsonSerializer.SerializeToElement(new[] { Address.ToString(), "latest" }));

        using JsonRpcResponse response = await rpc.Service.SendRequestAsync(request, new JsonRpcContext(RpcTransport.Http));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.TypeOf<ResultWrapper<UInt256>>());
            Assert.That(((ResultWrapper<UInt256>)response).Data, Is.EqualTo((UInt256)42));
            Assert.That(response.Id, Is.EqualTo(request.Id));
        }
        await source.Received(1).GetAccountAsync(Head, Address, CancellationToken.None);
    }

    [TestCase("eth_getBalance", "[\"1000000000000000000000000000000000000001\"]")]
    [TestCase("eth_getBlockByHash", "[\"1000000000000000000000000000000000000000000000000000000000000000\",false]")]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", "[\"latest\",\"0x00\"]")]
    [TestCase("eth_feeHistory", "[\"0x00\",\"latest\",[]]")]
    [TestCase("eth_feeHistory", "[\"1\",\"latest\",[]]")]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", "[\"latest\",\"1\"]")]
    [TestCase("eth_sendRawTransaction", "[\"0x0\"]")]
    [TestCase("eth_getBlockByNumber", "[\"latest\",\"false\"]")]
    [TestCase("eth_getProof", "[\"0x1000000000000000000000000000000000000001\",\"[]\"]")]
    [TestCase("eth_feeHistory", "[\"0x1\",\"latest\",\"[]\"]")]
    [TestCase("eth_unsubscribe", "[42]")]
    [TestCase("eth_getBalance", "[null]")]
    [TestCase("eth_getBalance", "[\"0x1000000000000000000000000000000000000001\",null]")]
    [TestCase("eth_chainId", "null")]
    [TestCase("eth_chainId", "{}")]
    public async Task Invalid_typed_parameters_are_rejected_before_reading_state(string method, string parameters)
    {
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(backend, NullLogManager.Instance);
        using JsonDocument input = JsonDocument.Parse(parameters);

        using JsonRpcResponse response = await rpc.Service.SendRequestAsync(Request(method, input.RootElement),
            new JsonRpcContext(method == "eth_unsubscribe" ? RpcTransport.Ws : RpcTransport.Http));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErrorCode(response), Is.EqualTo(-32602));
            Assert.That(source.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public async Task Escaped_hex_parameters_use_the_shared_decoders()
    {
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        source.GetAccountAsync(Head, Address, Arg.Any<CancellationToken>()).Returns(Task.FromResult(new Account(7, 42)));
        source.GetStorageAsync(Head, Address, Arg.Any<Account>(), (UInt256)1, Arg.Any<CancellationToken>()).Returns(Task.FromResult((UInt256)5));
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(backend, NullLogManager.Instance);
        using JsonDocument input = JsonDocument.Parse("""["\u0030x1000000000000000000000000000000000000001","\u0030x01"]""");

        object result = await rpc.InvokeAsync("eth_getStorageAt", input.RootElement, CancellationToken.None);

        Assert.That(result, Is.EqualTo("0x" + new string('0', 63) + "5"));
    }

    [Test]
    public async Task Invalid_versions_are_rejected_before_waiting_for_startup([Values(null, "", "1.0")] string? version)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using RpcService rpc = new(status, NullLogManager.Instance);
        JsonRpcRequest request = Request("eth_getBalance", default);
        request.JsonRpc = version!;
        ValueTask<JsonRpcResponse> pending = rpc.Service.SendRequestAsync(request, new JsonRpcContext(RpcTransport.Http));
        Assert.That(pending.IsCompleted, Is.True);

        using JsonRpcResponse response = await pending;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErrorCode(response), Is.EqualTo(-32600));
            Assert.That(response.Id.IsNull, Is.True);
            Assert.That(status.Read().RpcInitialized, Is.False);
        }
    }

    [Test]
    public async Task Missing_method_is_rejected_before_waiting_for_startup()
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using RpcService rpc = new(status, NullLogManager.Instance);
        JsonRpcRequest request = Request(null!, default);

        using JsonRpcResponse response = await rpc.Service.SendRequestAsync(request, new JsonRpcContext(RpcTransport.Http));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErrorCode(response), Is.EqualTo(-32600));
            Assert.That(response.Id.IsNull, Is.True);
        }
    }

    [Test]
    public async Task Method_names_are_used_literally([Values("", " eth_chainId", "eth_chainId ", "\teth_chainId")] string method)
    {
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(backend, NullLogManager.Instance);
        JsonRpcRequest request = Request(method, default);

        using JsonRpcResponse response = await rpc.Service.SendRequestAsync(request, new JsonRpcContext(RpcTransport.Http));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErrorCode(response), Is.EqualTo(-32601));
            Assert.That(response.Id, Is.EqualTo(request.Id));
            Assert.That(source.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public async Task Proof_key_limit_is_applied_before_typed_array_decoding()
    {
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(backend, NullLogManager.Instance);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { Address.ToString(), Enumerable.Repeat("0x0", 65).ToArray() });

        using JsonRpcResponse response = await rpc.Service.SendRequestAsync(Request("eth_getProof", parameters), new JsonRpcContext(RpcTransport.Http));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErrorCode(response), Is.EqualTo(-32602));
            Assert.That(source.ReceivedCalls(), Is.Empty);
        }
    }

    [Test]
    public void Composition_exposes_only_supported_modules_and_endpoint_methods()
    {
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(backend, NullLogManager.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.Modules.Check("eth_getBalance", new JsonRpcContext(RpcTransport.Http)), Is.EqualTo(ModuleResolution.Enabled));
            Assert.That(rpc.Modules.Check("eth_subscribe", new JsonRpcContext(RpcTransport.Ws)), Is.EqualTo(ModuleResolution.Enabled));
            Assert.That(rpc.Modules.Check("eth_subscribe", new JsonRpcContext(RpcTransport.Http)), Is.Not.EqualTo(ModuleResolution.Enabled));
            Assert.That(rpc.Modules.Check("debug_traceTransaction", new JsonRpcContext(RpcTransport.Http)), Is.Not.EqualTo(ModuleResolution.Enabled));
            Assert.That(rpc.Modules.Check("admin_addPeer", new JsonRpcContext(RpcTransport.Http)), Is.Not.EqualTo(ModuleResolution.Enabled));
        }
    }

    [Test]
    public async Task Context_deadline_is_an_lc_timeout_without_cancelling_the_transport()
    {
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        using CancellationTokenSource deadline = new();
        source.GetAccountAsync(Head, Address, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await deadline.CancelAsync();
            call.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
            return Account.TotallyEmpty;
        });
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(backend, NullLogManager.Instance);
        JsonRpcContext context = new(RpcTransport.Ws) { RequestAborted = deadline.Token };

        using JsonRpcResponse response = await rpc.Service.SendRequestAsync(
            Request("eth_getBalance", JsonSerializer.SerializeToElement(new[] { Address.ToString() })), context, CancellationToken.None);

        Assert.That(ErrorCode(response), Is.EqualTo(-32002));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Context_deadline_expires_while_waiting_for_backend_initialization(CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => false, _ => false);
        using RpcService rpc = new(status, NullLogManager.Instance);
        using CancellationTokenSource deadline = new();
        JsonRpcContext context = new(RpcTransport.Ws) { RequestAborted = deadline.Token };
        Task<JsonRpcResponse> pending = rpc.Service.SendRequestAsync(
            Request("eth_blockNumber", default), context, token).AsTask();
        Assert.That(pending.IsCompleted, Is.False);

        await deadline.CancelAsync();
        using JsonRpcResponse response = await pending.WaitAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErrorCode(response), Is.EqualTo(-32002));
            Assert.That(token.IsCancellationRequested, Is.False);
            Assert.That(status.Read().RpcInitialized, Is.False);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Processor_observer_sees_startup_waits_and_metadata_once(CancellationToken token)
    {
        LightClientStatus status = new(1, [], _ => true, _ => true);
        IExecutionStateSource source = Substitute.For<IExecutionStateSource>();
        using RpcBackend backend = new(source, () => Head, 1, getLatestHead: () => Head);
        using RpcService rpc = new(status, NullLogManager.Instance);
        IJsonRpcProcessor defaultProcessor = rpc.Processor;
        RecordingService observer = new(rpc.Service);
        IJsonRpcProcessor processor = rpc.CreateProcessor(observer);
        using RpcResponseBuffer metadata = new(RpcEndpoint.BatchResponseLimit, RpcEndpoint.BatchLimitMessage);
        JsonRpcContext context = new(RpcTransport.Http) { RequestAborted = token };
        await processor.ProcessAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}"""),
            context, metadata, new(JsonRpcInputMode.SingleDocument), token);
        using RpcResponseBuffer read = new(RpcEndpoint.BatchResponseLimit, RpcEndpoint.BatchLimitMessage);
        Task pending = processor.ProcessAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":2,"method":"eth_blockNumber","params":[]}"""),
            context, read, new(JsonRpcInputMode.SingleDocument), token).AsTask();
        await observer.ReadStarted.Task.WaitAsync(token);
        bool waitsForBackend = !pending.IsCompleted;

        status.Initialize(backend, static _ => Task.CompletedTask, () => Head, () => Head,
            static _ => [], static () => new(null, null, null, null, false, null));
        await pending.WaitAsync(token);
        using JsonDocument metadataResponse = JsonDocument.Parse(metadata.WrittenMemory);
        using JsonDocument readResponse = JsonDocument.Parse(read.WrittenMemory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor, Is.Not.SameAs(defaultProcessor));
            Assert.That(waitsForBackend, Is.True);
            Assert.That(observer.Methods, Is.EqualTo(new[] { "eth_chainId", "eth_blockNumber" }));
            Assert.That(metadataResponse.RootElement.GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(readResponse.RootElement.GetProperty("result").GetString(), Is.EqualTo("0xc8"));
        }
    }

    private sealed class RecordingService(IJsonRpcService inner) : IJsonRpcService
    {
        internal List<string> Methods { get; } = [];
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken token)
        {
            Methods.Add(request.Method);
            try { return inner.SendRequestAsync(request, context, token); }
            finally { if (request.Method == "eth_blockNumber") ReadStarted.TrySetResult(); }
        }
        public JsonRpcErrorResponse GetErrorResponse(int code, string message, in JsonRpcId id, string? methodName = null) =>
            inner.GetErrorResponse(code, message, in id, methodName);
        public JsonRpcErrorResponse GetErrorResponse(int code, string message, string? methodName = null) =>
            inner.GetErrorResponse(code, message, methodName);
    }

    private static int ErrorCode(JsonRpcResponse response) => response switch
    {
        JsonRpcErrorResponse { Error: { } error } => error.Code,
        IResultWrapper { ErrorCode: var code } => code,
        _ => throw new AssertionException("Expected an RPC error."),
    };

    private static JsonRpcRequest Request(string method, JsonElement parameters) => new()
    {
        JsonRpc = "2.0",
        Id = new JsonRpcId(7L),
        Method = method,
        Params = parameters,
    };
}
