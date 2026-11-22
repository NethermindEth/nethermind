// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Memory;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using System.IO.Abstractions;
using Testably.Abstractions;

namespace Nethermind.LightClient.Rpc;

/// <summary>Hosts the supported typed RPC modules over the shared JSON-RPC service and processor.</summary>
internal sealed class RpcService : IDisposable
{
    private readonly RpcBackend? _backend;
    private readonly LightClientStatus? _status;
    private readonly IContainer _container;
    private readonly Lock _processorLock = new();
    private IJsonRpcProcessor? _processor;

    internal RpcService(RpcBackend backend, ILogManager logs)
    {
        _backend = backend;
        ChainId = backend.ChainId;
        _container = Build(logs);
        Modules = _container.Resolve<IRpcModuleProvider>();
        Service = _container.Resolve<IJsonRpcService>();
    }

    internal RpcService(LightClientStatus status, ILogManager logs)
    {
        _status = status;
        ChainId = status.ChainId;
        _container = Build(logs);
        Modules = _container.Resolve<IRpcModuleProvider>();
        Service = _container.Resolve<IJsonRpcService>();
    }

    internal ulong ChainId { get; }
    internal bool Listening => _status?.Listening ?? true;
    internal IRpcModuleProvider Modules { get; }
    internal IJsonRpcService Service { get; }
    internal IJsonRpcConfig Config { get; } = new JsonRpcConfig
    {
        EnabledModules = [ModuleType.Eth, ModuleType.Net, ModuleType.Web3, ModuleType.Subscribe],
        CallsFilterFilePath = string.Empty,
        Timeout = 90_000,
        MaxBatchSize = int.MaxValue,
        MaxRequestBodySize = 4 * 1024 * 1024 + 64 * 1024,
        MaxBatchResponseBodySize = 16 * 1024 * 1024,
        BatchProcessingConcurrency = 1,
        EthCallMaxConcurrentRequests = 0,
        StrictHexFormat = true,
    };

    internal IJsonRpcProcessor Processor
    {
        get
        {
            lock (_processorLock) return _processor ??= CreateProcessor(Service);
        }
    }

    internal IJsonRpcProcessor CreateProcessor(IJsonRpcService service) =>
        _container.Resolve<IJsonRpcProcessor>(new TypedParameter(typeof(IJsonRpcService), service));

    internal SyncingResult GetSyncing() => _status?.GetSyncing() ?? _backend!.GetSyncing();

    internal ValueTask<RpcBackend> GetBackendAsync(CancellationToken token, bool waitForStartup = true)
    {
        token.ThrowIfCancellationRequested();
        return _status is null ? ValueTask.FromResult(_backend!) : _status.GetBackendAsync(token, waitForStartup);
    }

    private IContainer Build(ILogManager logs) => new ContainerBuilder()
        .AddSingleton(this)
        .AddSingleton(Config)
        .AddSingleton(logs)
        .AddSingleton<IFileSystem, RealFileSystem>()
        .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()
        .AddSingleton<IGCStrategy>(NoGCStrategy.Instance)
        .AddSingleton<GCKeeper>()
        .RegisterSingletonJsonRpcModule<IEthRpcModule, EthRpcModule>()
        .RegisterSingletonJsonRpcModule<INetRpcModule, NetRpcModule>()
        .RegisterSingletonJsonRpcModule<IWeb3RpcModule, Web3RpcModule>()
        .RegisterSingletonJsonRpcModule<ISubscribeRpcModule, SubscribeRpcModule>()
        .AddSingleton<IRpcModuleProvider, RpcModuleProvider>()
        .AddSingleton<IJsonRpcService, JsonRpcService>()
        .AddDecorator<IJsonRpcService, PolicyService>()
        .Add<IJsonRpcProcessor, JsonRpcProcessor>()
        .Build();

    public void Dispose() => _container.Dispose();

    private sealed class PolicyService(IJsonRpcService inner, RpcService owner) : IJsonRpcService
    {
        public async ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request is null || request.JsonRpc != "2.0" || request.Method is null)
                return inner.GetErrorResponse(-32600, "Invalid Request", JsonRpcId.Null);
            CancellationToken requestToken = context.RequestAborted.CanBeCanceled ? context.RequestAborted : token;
            try
            {
                requestToken.ThrowIfCancellationRequested();
                if (owner._status is not null && !IsLocal(request.Method))
                {
                    ValueTask<RpcBackend> startup = owner.GetBackendAsync(requestToken, waitForStartup: request.Method != "eth_sendRawTransaction");
                    if (!startup.IsCompletedSuccessfully) await startup.AsTask().WaitAsync(token);
                }
                bool enabled = owner.Modules.Check(request.Method, context, out _, out RpcModuleProvider.ResolvedMethodInfo? method) == ModuleResolution.Enabled;
                ValidateParameters(request, enabled ? method : null);
                if (request.Method.Length == 0 || char.IsWhiteSpace(request.Method[0]) || char.IsWhiteSpace(request.Method[^1]))
                    return inner.GetErrorResponse(-32601, "Method is not supported by the verified RPC.", request.Id, request.Method);
                if (enabled && request.Method == "eth_unsubscribe" && IsEmptySubscriptionId(request))
                {
                    ResultWrapper<bool> removed = RpcSubscriptions.Unsubscribe(context.DuplexClient, string.Empty);
                    removed.Id = request.Id;
                    return removed;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { throw new OperationCanceledException(token); }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            { return TimedOut(request); }
            catch (RpcException exception)
            {
                JsonRpcId id = request.Id;
                return inner.GetErrorResponse(exception.Code, exception.Message, in id, request.Method);
            }
            JsonRpcResponse response = await inner.SendRequestAsync(request, context, token);
            if (token.IsCancellationRequested)
            {
                response.Dispose();
                token.ThrowIfCancellationRequested();
            }
            if (context.RequestAborted.IsCancellationRequested)
            {
                response.Dispose();
                return TimedOut(request);
            }
            return response;
        }

        private JsonRpcErrorResponse TimedOut(JsonRpcRequest request)
        {
            JsonRpcId id = request.Id;
            return inner.GetErrorResponse(-32002, "Execution request timed out.", in id, request.Method);
        }

        private static bool IsEmptySubscriptionId(JsonRpcRequest request)
        {
            if (!request.ParamsUtf8.IsEmpty)
            {
                Utf8JsonReader reader = new(request.ParamsUtf8.Span);
                return reader.Read() && reader.TokenType == JsonTokenType.StartArray
                    && reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ReadOnlySpan<byte>.Empty)
                    && reader.Read() && reader.TokenType == JsonTokenType.EndArray;
            }
            JsonElement parameters = request.Params;
            return parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() == 1
                && parameters[0].ValueKind == JsonValueKind.String && parameters[0].ValueEquals(ReadOnlySpan<byte>.Empty);
        }

        private static bool IsLocal(string method) => method is "eth_chainId" or "net_version" or "web3_clientVersion"
            or "eth_accounts" or "net_listening" or "eth_syncing";

        private static void ValidateParameters(JsonRpcRequest request, RpcModuleProvider.ResolvedMethodInfo? method)
        {
            if (!request.ParamsUtf8.IsEmpty)
            {
                Utf8JsonReader reader = new(request.ParamsUtf8.Span);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw InvalidParameters();
                if (method is null) return;
                int argument = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.Null || reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ReadOnlySpan<byte>.Empty) && request.Method != "eth_unsubscribe")
                        throw InvalidParameters();
                    if (argument < method.ExpectedParameters.Length)
                        ValidateArgument(method.ExpectedParameters[argument].ParameterType, reader.TokenType);
                    if (request.Method == "eth_getProof" && argument == 1 && reader.TokenType == JsonTokenType.StartArray)
                    {
                        Utf8JsonReader keys = reader;
                        int count = 0;
                        while (keys.Read() && keys.TokenType != JsonTokenType.EndArray)
                        {
                            if (++count > 64) throw InvalidParameters();
                            keys.Skip();
                        }
                    }
                    reader.Skip();
                    argument++;
                }
                return;
            }
            JsonElement parameters = request.Params;
            if (parameters.ValueKind == JsonValueKind.Undefined) return;
            if (parameters.ValueKind != JsonValueKind.Array) throw InvalidParameters();
            if (method is null) return;
            int index = 0;
            foreach (JsonElement argument in parameters.EnumerateArray())
            {
                if (argument.ValueKind == JsonValueKind.Null || argument.ValueKind == JsonValueKind.String && argument.ValueEquals(ReadOnlySpan<byte>.Empty) && request.Method != "eth_unsubscribe")
                    throw InvalidParameters();
                if (index < method.ExpectedParameters.Length)
                    ValidateArgument(method.ExpectedParameters[index].ParameterType, argument.ValueKind switch
                    {
                        JsonValueKind.String => JsonTokenType.String,
                        JsonValueKind.Array => JsonTokenType.StartArray,
                        JsonValueKind.True => JsonTokenType.True,
                        JsonValueKind.False => JsonTokenType.False,
                        _ => JsonTokenType.None,
                    });
                if (request.Method == "eth_getProof" && index == 1 && argument.ValueKind == JsonValueKind.Array && argument.GetArrayLength() > 64)
                    throw InvalidParameters();
                index++;
            }
        }

        private static void ValidateArgument(Type type, JsonTokenType token)
        {
            if (type == typeof(bool) && token is not (JsonTokenType.True or JsonTokenType.False)
                || type == typeof(string) && token != JsonTokenType.String
                || type.IsArray && type != typeof(byte[]) && token != JsonTokenType.StartArray)
                throw InvalidParameters();
        }

        private static RpcException InvalidParameters() => new(-32602, "Invalid verified RPC parameters.");

        public JsonRpcErrorResponse GetErrorResponse(int code, string message, in JsonRpcId id, string? methodName = null) =>
            inner.GetErrorResponse(code, message, in id, methodName);
        public JsonRpcErrorResponse GetErrorResponse(int code, string message, string? methodName = null) =>
            inner.GetErrorResponse(code, message, methodName);
    }
}
