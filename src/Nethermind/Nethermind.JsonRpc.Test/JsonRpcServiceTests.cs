// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Test.Threading;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Trie;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using static Nethermind.JsonRpc.EvmAdmissionGate;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class JsonRpcServiceTests
{
    [TestCase("engine_newPayloadV1", true, true, RpcEndpoint.Http)]
    [TestCase("engine_newPayloadV5", true, true, RpcEndpoint.Http)]
    [TestCase("engine_newPayloadV99", true, true, RpcEndpoint.Http)]
    [TestCase("engine_newPayloadWithWitnessV5", true, true, RpcEndpoint.Http)]
    [TestCase("engine_newPayloadV5", false, false, RpcEndpoint.Http)]
    [TestCase("engine_forkchoiceUpdatedV4", true, false, RpcEndpoint.Http)]
    [TestCase("eth_call", true, false, RpcEndpoint.Http)]
    [TestCase("Engine_newPayloadV5", true, false, RpcEndpoint.Http)]
    [TestCase(null, true, false, RpcEndpoint.Http)]
    [TestCase("engine_newPayloadV5", false, true, RpcEndpoint.IPC)]
    [TestCase("eth_call", false, false, RpcEndpoint.IPC)]
    public async Task New_payload_cancels_pending_collection_before_dispatch(string? method, bool authenticated, bool cancels, RpcEndpoint endpoint)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.PostBlockDelayMs.Returns(60_000);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.Yes));
        using GCKeeper keeper = new(strategy, NullLogManager.Instance);
        Task pending = keeper.ScheduleGCInternal(throttle: false);
        IRpcModuleProvider provider = Substitute.For<IRpcModuleProvider>();
        provider.Check(Arg.Any<string>(), Arg.Any<JsonRpcContext>(), out Arg.Any<string?>(), out Arg.Any<RpcModuleProvider.ResolvedMethodInfo?>())
            .Returns(ModuleResolution.Unknown);
        JsonRpcService service = new(provider, NullLogManager.Instance, new JsonRpcConfig(), keeper);
        using JsonRpcContext context = new(endpoint, url: new JsonRpcUrl("http", "localhost", 8551, RpcEndpoint.Http, authenticated, ["engine"]));
        using JsonRpcResponse response = await service.SendRequestAsync(new JsonRpcRequest { Method = method! }, context);
        if (cancels) await pending.WaitAsync(TimeSpan.FromSeconds(5));
        else Assert.That(pending.IsCompleted, Is.False);
        keeper.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [SetUp]
    public void Initialize()
    {
        _configurationProvider = new ConfigProvider();
        _logManager = LimboLogs.Instance;
        _gcKeeper = new GCKeeper(NoGCStrategy.Instance, _logManager);
        _context = new JsonRpcContext(RpcEndpoint.Http);
        // StrictHexFormat is pinned for the whole assembly by StrictHexFormatAssemblySetup; no fixture may touch
        // that static, because it is process-global and every concurrent block-parameter parse reads it (#13204).
        _timeProvider = new ManualTimeProvider();
        UseGate(_configurationProvider.GetConfig<IJsonRpcConfig>());
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
        _gate.Dispose();
        _serviceContainer?.Dispose();
        _gcKeeper.Dispose();
    }

    private GCKeeper _gcKeeper = null!;
    private IJsonRpcService _jsonRpcService = null!;
    private IConfigProvider _configurationProvider = null!;
    private ILogManager _logManager = null!;
    private JsonRpcContext _context = null!;
    private EvmAdmissionGate _gate = null!;
    private ManualTimeProvider _timeProvider = null!;
    private IContainer? _serviceContainer;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static HexBytes ToHexBytes(string value) => new(Bytes.FromHexString(value));

    private static PolymorphicDerivedPayload CreatePolymorphicPayload() =>
        new() { BaseValue = "base", DerivedValue = "derived" };

    private static ResultWrapper<T> AssertWrapperResponse<T>(JsonRpcResponse response)
    {
        Assert.That(response, Is.InstanceOf<ResultWrapper<T>>());
        return (ResultWrapper<T>)response;
    }

    private static IEnumerable<TestCaseData> EthCallNullableTrailingArgumentCases()
    {
        yield return new TestCaseData((object)new object?[] { new LegacyTransactionForRpc() }).SetName("Implicit null");
        yield return new TestCaseData((object)new object?[] { new LegacyTransactionForRpc(), "" }).SetName("Explicit empty string");
        yield return new TestCaseData((object)new object?[] { new LegacyTransactionForRpc(), null }).SetName("Explicit null");
    }

    private static IEnumerable<TestCaseData> InvalidRawUtf8ParamCases()
    {
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBlockByNumber),
            """[{"blockNumber":{}},false]""",
            "unknown block parameter type",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>())))
            .SetName("Malformed typed argument");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBlockByNumber),
            """["",false]""",
            "missing value for required argument 0",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>())))
            .SetName("Empty string for non-trailing required argument");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBlockByNumber),
            """[null,false]""",
            "missing value for required argument 0",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>())))
            .SetName("Null for non-trailing required argument");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_feeHistory),
            """[{},"latest"]""",
            "missing value for required argument 2",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_feeHistory(Arg.Any<ulong>(), Arg.Any<BlockParameter>(), Arg.Any<double[]>())))
            .SetName("Missing required argument");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBlockByNumber),
            """["0x1",false,"extra"]""",
            "Invalid params",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>())))
            .SetName("Extra argument");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBlockByNumber),
            """["",false]""",
            "missing value for required argument 0",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>())))
            .SetName("Required argument marked missing before another");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBlockByNumber),
            """["",false,"extra"]""",
            "Invalid params",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>())))
            .SetName("Extra argument alongside a missing marker");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBalance),
            """["cf1dc766fc2c62bef0b67a8de666c8e67acf35f6","0x1036640"]""",
            "hex string without 0x prefix",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())))
            .SetName("Address without 0x prefix");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBalance),
            """["0xcf1dc766fc2c62bef0b67a8de666c8e67acf35f6","0x00"]""",
            "hex number with leading zero digits",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())))
            .SetName("Block number boundary leading zero");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBalance),
            """["0xcf1dc766fc2c62bef0b67a8de666c8e67acf35f6","0x01"]""",
            "hex number with leading zero digits",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())))
            .SetName("Block number single digit leading zero one");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBalance),
            """["0xcf1dc766fc2c62bef0b67a8de666c8e67acf35f6","0x0f"]""",
            "hex number with leading zero digits",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())))
            .SetName("Block number single digit leading zero f");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBalance),
            """["0xcf1dc766fc2c62bef0b67a8de666c8e67acf35f6","0x00001036640"]""",
            "hex number with leading zero digits",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())))
            .SetName("Block number with leading zeros");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBalance),
            """["0x0000000000000000000000000000000000000000","0x"]""",
            "hex string \"0x\"",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())))
            .SetName("Empty hex block quantity");
        yield return new TestCaseData(
            nameof(IEthRpcModule.eth_getBalance),
            """["0xcf1dc766fc2c62bef0b67a8de666c8e67acf35f6",{"blockNumber":"0x1036640","blockHash":"0x96cfa0fb5e50b0a3f6cc76f3299cfbf48f17e8b41798d1394474e67ec8a97e9f"}]""",
            "cannot specify both BlockHash and BlockNumber, choose one or the other",
            (Action<IEthRpcModule>)(static module => module.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())))
            .SetName("EIP-1898 mutually exclusive block fields");
    }

    private static IEnumerable<TestCaseData> RuntimePolymorphicPayloadCases()
    {
        yield return new TestCaseData(
            ResultWrapper<PolymorphicBasePayload>.Success(CreatePolymorphicPayload()),
            new Func<JsonElement, JsonElement>(static root => root.GetProperty("result"))).SetName("Success payload");
        yield return new TestCaseData(
            ResultWrapper<PolymorphicBasePayload[]>.Success(new PolymorphicDerivedPayload[] { CreatePolymorphicPayload() }),
            new Func<JsonElement, JsonElement>(static root => root.GetProperty("result")[0])).SetName("Success array payload");
        yield return new TestCaseData(
            ResultWrapper<string, PolymorphicBasePayload>.Fail("typed", ErrorCodes.InvalidParams, CreatePolymorphicPayload()),
            new Func<JsonElement, JsonElement>(static root => root.GetProperty("error").GetProperty("data"))).SetName("Error data payload");
    }

    private static JsonRpcErrorResponse AssertJsonRpcError(JsonRpcResponse response, int expectedCode, string? expectedMessage = null)
    {
        Assert.That(response, Is.InstanceOf<JsonRpcErrorResponse>());
        JsonRpcErrorResponse errorResponse = (JsonRpcErrorResponse)response;
        Assert.That(errorResponse.Error?.Code, Is.EqualTo(expectedCode));
        if (expectedMessage is not null)
        {
            Assert.That(errorResponse.Error?.Message, Is.EqualTo(expectedMessage));
        }

        return errorResponse;
    }

    private static void AssertInvalidParamsWithoutData(JsonRpcResponse response, string expectedMessage)
    {
        JsonRpcErrorResponse errorResponse = AssertJsonRpcError(response, ErrorCodes.InvalidParams, expectedMessage);
        Assert.That(errorResponse.Error?.Data, Is.Null);
    }

    private JsonRpcResponse TestRequest<T>(T module, string method, params object?[]? parameters) where T : IRpcModule =>
        TestRequestWithPool(new SingletonModulePool<T>(new SingletonFactory<T>(module), true), method, parameters);

    private JsonRpcResponse TestRequestWithPool<T>(IRpcModulePool<T> pool, string method, params object?[]? parameters) where T : IRpcModule
    {
        JsonRpcRequest request = RpcTest.BuildJsonRequest(method, parameters);
        return SendRequestWithPool(pool, request);
    }

    private JsonRpcResponse TestRawRequest<T>(T module, string method, string rawParameters) where T : IRpcModule =>
        SendRequestWithPool(new SingletonModulePool<T>(new SingletonFactory<T>(module), true), BuildRawRequest(method, rawParameters));

    private static JsonRpcRequest BuildRawRequest(string method, string rawParameters) =>
        new()
        {
            JsonRpc = "2.0",
            Method = method,
            ParamsUtf8 = Encoding.UTF8.GetBytes(rawParameters),
            ParamsKind = JsonValueKind.Array,
            Id = 67
        };

    private JsonRpcResponse SendRequestWithPool<T>(IRpcModulePool<T> pool, JsonRpcRequest request) where T : IRpcModule
    {
        _jsonRpcService = CreateService(pool);
        JsonRpcResponse response = _jsonRpcService.SendRequestAsync(request, _context).Result;
        Assert.That(response.Id, Is.EqualTo(request.Id));
        return response;
    }

    private IJsonRpcService CreateService<T>(IRpcModulePool<T> pool, IJsonRpcConfig? config = null) where T : IRpcModule
    {
        _serviceContainer?.Dispose();
        _serviceContainer = new ContainerBuilder()
            .AddModule(new TestNethermindModule(_configurationProvider))
            .AddLast<RpcModuleInfo>(_ => new RpcModuleInfo(typeof(T), pool))
            .Build();
        RpcModuleProvider moduleProvider = _serviceContainer.Resolve<RpcModuleProvider>();
        return new JsonRpcService(moduleProvider, _logManager, config ?? _configurationProvider.GetConfig<IJsonRpcConfig>(), _gcKeeper, _gate);
    }

    private IJsonRpcService CreateService<T>(T module) where T : IRpcModule =>
        CreateService(new SingletonModulePool<T>(new SingletonFactory<T>(module), true));

    private IJsonRpcService CreateService<T>(T module, IJsonRpcConfig config) where T : IRpcModule =>
        CreateService(new SingletonModulePool<T>(new SingletonFactory<T>(module), true), config);

    private void UseGate(IJsonRpcConfig config)
    {
        _gate?.Dispose();
        _gate = new EvmAdmissionGate(config, _timeProvider);
    }

    private static JsonRpcConfig SinglePermitConfig(int maxQueueWaitMs) =>
        new() { EthModuleConcurrentInstances = 1, EvmExecutionMaxQueueWaitMs = maxQueueWaitMs };

    private ValueTask<EvmAdmissionGate.Lease> HoldPermitAsync() => _gate.AdmitAsync(paramsUtf8Length: 0, CancellationToken.None);

    private Task<JsonRpcResponse> SendEthCallAsync(IJsonRpcService service) =>
        service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_call", new LegacyTransactionForRpc()), _context).AsTask().WaitAsync(TestTimeout);

    [TestCase(false, 2UL, TestName = "Number")]
    [TestCase(true, 513UL, TestName = "Size")]
    public void Eth_module_populates_block_data(bool assertSize, ulong expected)
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        ethRpcModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x => ResultWrapper<BlockForRpc>.Success(new BlockForRpc(Build.A.Block.WithNumber(2).TestObject, true, specProvider)));
        BlockForRpc result = RpcTest.AssertSuccess<BlockForRpc>(TestRequest(ethRpcModule, "eth_getBlockByNumber", "0x1b4", "true"));
        Assert.That(assertSize ? (ulong)result.Size : result.Number!.Value, Is.EqualTo(expected));
    }

    [Test]
    public void CanRunEthSimulateV1Empty()
    {
        SimulatePayload<TransactionForRpc> payload = new() { BlockStateCalls = [] };
        string serializedCall = new EthereumJsonSerializer().Serialize(payload);
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_simulateV1(payload).ReturnsForAnyArgs(static _ =>
            ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>>.Success([]));
        IReadOnlyList<SimulateBlockResult<SimulateCallResult>> result =
            RpcTest.AssertSuccess<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>>(TestRequest(ethRpcModule, "eth_simulateV1", serializedCall));
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void CanHandleOptionalArguments()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        HexBytes expected = ToHexBytes("0x01");
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(expected));
        HexBytes result = RpcTest.AssertSuccess<HexBytes>(TestRequest(ethRpcModule, "eth_call", new LegacyTransactionForRpc()));
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Value_type_result_failure_without_error_data_does_not_emit_default_data()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Fail("out of gas", ErrorCodes.ExecutionError));

        ResultWrapper<HexBytes> response = AssertWrapperResponse<HexBytes>(TestRequest(ethRpcModule, "eth_call", new LegacyTransactionForRpc()));

        Assert.That(response.ErrorCode, Is.EqualTo(ErrorCodes.ExecutionError));
        Assert.That(response.Result.Error, Is.EqualTo("out of gas"));
        Assert.That(response.HasErrorData, Is.False);
    }

    [Test]
    public void Typed_error_data_false_is_serialized()
    {
        ResultWrapper<string, bool> response = ResultWrapper<string, bool>.Fail("typed", ErrorCodes.InvalidParams, false);
        response.Id = 67;

        string serialized = RpcTest.SerializeResponse(response);

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"typed\",\"data\":false},\"id\":67}"));
    }

    [Test]
    public void Payload_type_shape_classifies_runtime_polymorphic_types()
    {
        Assert.That(RpcPayloadTypeShape<int>.CanHaveDerivedRuntimeType, Is.False);
        Assert.That(RpcPayloadTypeShape<SealedPayload>.CanHaveDerivedRuntimeType, Is.False);
        Assert.That(RpcPayloadTypeShape<object>.CanHaveDerivedRuntimeType, Is.True);
        Assert.That(RpcPayloadTypeShape<PolymorphicBasePayload>.CanHaveDerivedRuntimeType, Is.True);
        Assert.That(RpcPayloadTypeShape<SealedPayload[]>.CanHaveDerivedRuntimeType, Is.False);
        Assert.That(RpcPayloadTypeShape<PolymorphicBasePayload[]>.CanHaveDerivedRuntimeType, Is.True);
        Assert.That(RpcPayloadTypeShape<SealedPayload>.CanBeStreamable, Is.False);
        Assert.That(RpcPayloadTypeShape<PolymorphicBasePayload>.CanBeStreamable, Is.True);
    }

    [TestCaseSource(nameof(RuntimePolymorphicPayloadCases))]
    public void Runtime_polymorphic_payload_uses_runtime_type_info(JsonRpcResponse response, Func<JsonElement, JsonElement> getPayload)
    {
        response.Id = 67;

        string serialized = RpcTest.SerializeResponse(response);

        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement payload = getPayload(document.RootElement);
        Assert.That(payload.GetProperty("baseValue").GetString(), Is.EqualTo("base"));
        Assert.That(payload.GetProperty("derivedValue").GetString(), Is.EqualTo("derived"));
    }

    [Test]
    public void Error_message_serialization_uses_relaxed_json_escaping()
    {
        JsonRpcErrorResponse response = new()
        {
            Error = new Error { Code = ErrorCodes.InvalidInput, Message = "missing \"to\" and 1 < 2" },
            Id = 67
        };

        string serialized = RpcTest.SerializeResponse(response);

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"missing \\\"to\\\" and 1 < 2\"},\"id\":67}"));
    }

    [TestCase(null, "null")]
    [TestCase(1UL, "\"0x1\"")]
    public void Nullable_quantity_result_serializes_null_and_hex_value(ulong? value, string expectedResult)
    {
        ResultWrapper<ulong?> response = ResultWrapper<ulong?>.Success(value);
        response.Id = 67;

        string serialized = RpcTest.SerializeResponse(response);

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}"));
    }

    [Test]
    public void Web3_client_version_serializes_string_result()
    {
        IWeb3RpcModule web3RpcModule = Substitute.For<IWeb3RpcModule>();
        web3RpcModule.web3_clientVersion().Returns(ResultWrapper<string>.Success("Nethermind/test"));

        string serialized = RpcTest.SerializeResponse(TestRequest(web3RpcModule, "web3_clientVersion"));

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"Nethermind/test\",\"id\":67}"));
    }

    [Test]
    public async Task Admin_peers_is_working_with_empty_or_null_params([Values] bool useNullParams)
    {
        IAdminRpcModule adminRpcModule = Substitute.For<IAdminRpcModule>();
        PeerInfo[] expectedPeers = [new PeerInfo { Enode = "enode://expected-peer" }];
        adminRpcModule.admin_peers(false).Returns(ResultWrapper<PeerInfo[]>.Success(expectedPeers));

        JsonRpcResponse response = useNullParams
            ? await RpcTest.TestRequest(adminRpcModule, "admin_peers", (object?[]?)null)
            : await RpcTest.TestRequest(adminRpcModule, "admin_peers");

        PeerInfo[] result = RpcTest.AssertSuccess<PeerInfo[]>(response);
        Assert.That(result, Is.SameAs(expectedPeers));
        adminRpcModule.Received(1).admin_peers(false);
    }

    // Receipt RPCs surface "neither stored nor reproducible" as ResourceNotFoundException; only eth_getLogs has a
    // module-level catch, so every other receipt method depends on this central mapping. Without it the exception
    // would hit the ArgumentException arm (it derives from it) and answer "invalid params".
    [Test]
    public void Resource_not_found_maps_to_pruned_history_unavailable()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_getBlockReceipts(Arg.Any<BlockParameter>())
            .ThrowsForAnyArgs(new ResourceNotFoundException("receipts are neither stored nor reproducible"));

        JsonRpcResponse response = TestRequest(ethRpcModule, "eth_getBlockReceipts", "0x1b4");

        Assert.That(response, Is.InstanceOf<JsonRpcErrorResponse>());
        Assert.That(((JsonRpcErrorResponse)response).Error?.Code, Is.EqualTo(ErrorCodes.PrunedHistoryUnavailable));
    }

    [Test]
    public void Case_sensitivity_test()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_chainId().ReturnsForAnyArgs(ResultWrapper<ulong>.Success(1ul));
        Assert.That(TestRequest(ethRpcModule, "eth_chainID"), Is.InstanceOf<JsonRpcErrorResponse>());
        Assert.That(TestRequest(ethRpcModule, "eth_chainId"), Is.InstanceOf<ResultWrapper<ulong>>());
    }

    [Test]
    public void No_parameter_methods_reject_non_empty_array_params_before_invocation()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_chainId().ReturnsForAnyArgs(ResultWrapper<ulong>.Success(1ul));

        Assert.That(TestRequest(ethRpcModule, "eth_chainId", "0x1"), Is.InstanceOf<JsonRpcErrorResponse>());
        ethRpcModule.DidNotReceive().eth_chainId();
    }

    [Test]
    public void Will_return_to_pool_on_arbitrary_error()
    {
        IRpcModulePool<IEthRpcModule> pool = Substitute.For<IRpcModulePool<IEthRpcModule>>();
        IEthRpcModule rpcModule = Substitute.For<IEthRpcModule>();
        pool.GetModule(false).Returns(rpcModule);

        rpcModule.eth_getLogs(Arg.Any<Filter>())
            .Throws(new Exception("test exception"));

        JsonRpcErrorResponse response = AssertJsonRpcError(TestRequestWithPool(pool, "eth_getLogs", "{}"), ErrorCodes.InternalError);
        rpcModule.Received().eth_getLogs(Arg.Any<Filter>());

        response.Dispose();
        pool.Received().ReturnModule(rpcModule);
    }

    // A streamed trace executes while the response is written, on the module's own overridable env; the module must
    // therefore stay rented until the response is disposed, or the next rental races it on that env.
    [Test]
    public void Returns_module_to_pool_only_after_a_streamed_result_is_disposed([Values] bool streamed)
    {
        IRpcModulePool<ITraceRpcModule> pool = Substitute.For<IRpcModulePool<ITraceRpcModule>>();
        ITraceRpcModule rpcModule = Substitute.For<ITraceRpcModule>();
        pool.GetModule(false).Returns(rpcModule);
        using CancellationTokenSource timeoutCts = new();
        IEnumerable<ParityTxTraceFromReplay> traces = streamed
            ? new ParityTxTraceStreamingResult<ParityTxTraceFromReplay>(static (_, _, _) => { }, timeoutCts, LimboLogs.Instance.GetClassLogger<JsonRpcServiceTests>())
            : [];
        rpcModule.trace_replayBlockTransactions(Arg.Any<BlockParameter>(), Arg.Any<string[]>())
            .Returns(ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(traces));

        JsonRpcResponse response = TestRequestWithPool(pool, "trace_replayBlockTransactions", "latest", new[] { "trace" });

        pool.Received(streamed ? 0 : 1).ReturnModule(rpcModule);
        response.Dispose();
        pool.Received(1).ReturnModule(rpcModule);
    }

    [Test]
    public void Success_response_dispose_disposes_disposable_result()
    {
        DisposableProbe disposable = new();
        JsonRpcSuccessResponse response = new() { Result = disposable };

        response.Dispose();

        Assert.That(disposable.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void Success_response_dispose_runs_registered_disposable_action_without_disposable_result()
    {
        int disposeCount = 0;
        JsonRpcSuccessResponse response = new(() => disposeCount++) { Result = "0x1" };

        response.Dispose();

        Assert.That(disposeCount, Is.EqualTo(1));
    }

    [Test]
    public void GetNewFilterTest()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_newFilter(Arg.Any<Filter>()).ReturnsForAnyArgs(static x => ResultWrapper<UInt256?>.Success(1));

        var parameters = new
        {
            fromBlock = "0x1",
            toBlock = "latest",
            address = "0x1f88f1f195afa192cfee860698584c030f4c9db2",
            topics = new List<object>
            {
                "0x000000000000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b", null!,
                new[]
                {
                    "0x000000000000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b",
                    "0x0000000000000000000000000aff3454fce5edbc8cca8697c15331677e6ebccc"
                }
            }
        };

        UInt256? result = RpcTest.AssertSuccess<UInt256?>(TestRequest(ethRpcModule, "eth_newFilter", JsonSerializer.Serialize(parameters)));
        Assert.That(result, Is.EqualTo(UInt256.One));
    }

    [TestCaseSource(nameof(EthCallNullableTrailingArgumentCases))]
    public void Eth_call_is_working_with_nullable_last_argument(object?[] parameters)
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        HexBytes expected = ToHexBytes("0x");
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(expected));

        HexBytes result = RpcTest.AssertSuccess<HexBytes>(TestRequest(ethRpcModule, "eth_call", parameters));
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Raw_utf8_params_keep_explicit_nullable_trailing_defaults()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule
            .eth_call(
                Arg.Any<SignableTransactionForRpc>(),
                Arg.Any<BlockParameter?>(),
                Arg.Any<Dictionary<Address, AccountOverride>?>(),
                Arg.Any<BlockOverride?>())
            .ReturnsForAnyArgs(static _ => ResultWrapper<HexBytes>.Success(default));

        string transaction = new EthereumJsonSerializer().Serialize(new LegacyTransactionForRpc());
        HexBytes result = RpcTest.AssertSuccess<HexBytes>(TestRawRequest(ethRpcModule, "eth_call", $"[{transaction},null]"));

        Assert.That(result, Is.EqualTo(default(HexBytes)));
    }

    [Test]
    public void Missing_marker_on_an_optional_argument_binds_its_default([Values(false, true)] bool rawUtf8)
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        ethRpcModule
            .eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>())
            .ReturnsForAnyArgs(_ => ResultWrapper<BlockForRpc>.Success(new BlockForRpc(Build.A.Block.WithNumber(2).TestObject, true, specProvider)));

        RpcTest.AssertSuccess<BlockForRpc>(rawUtf8
            ? TestRawRequest(ethRpcModule, "eth_getBlockByNumber", """["0x1b4",""]""")
            : TestRequest(ethRpcModule, "eth_getBlockByNumber", "0x1b4", ""));

        ethRpcModule.Received().eth_getBlockByNumber(Arg.Any<BlockParameter>(), false);
    }

    [Test]
    public void Eth_getTransactionReceipt_properly_fails_given_wrong_parameters()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();

        AssertJsonRpcError(TestRequest(ethRpcModule, "eth_getTransactionReceipt", """["0x80757153e93d1b475e203406727b62a501187f63e23b8fa999279e219ee3be71"]"""), ErrorCodes.InvalidParams);
    }

    [TestCase("eth_getBlockByNumber", new object?[] { }, "missing value for required argument 0", TestName = "FirstArgOmitted")]
    [TestCase("eth_feeHistory", new object?[] { "0x1", "latest" }, "missing value for required argument 2", TestName = "LaterArgOmitted")]
    [TestCase("eth_getBlockByNumber", new object?[] { "", false }, "missing value for required argument 0", TestName = "FirstArgMarkedMissingBeforeAnother")]
    [TestCase("eth_getProof", new object?[] { "0x7F0d15C7FAae65896648C8273B6d7E43f58Fa842", "", "latest" }, "missing value for required argument 1", TestName = "LaterArgMarkedMissingBeforeAnother")]
    [TestCase("eth_feeHistory", new object?[] { "", "latest" }, "missing value for required argument 0", TestName = "MarkedMissingArgIsNamedAheadOfOmittedTrailingOnes")]
    [TestCase("eth_getBlockByNumber", new object?[] { "", false, "" }, "Invalid params", TestName = "ExtraArgumentWinsOverAMarkedMissingOne")]
    [TestCase("eth_getBlockByNumber", new object?[] { "0x1", false, "" }, "Invalid params", TestName = "ExtraTrailingMarkerIsAnExtraArgument")]
    public void MissingRequiredArgument_ReturnsGethStyleError(string method, object?[] parameters, string expectedMessage)
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        AssertInvalidParamsWithoutData(TestRequest(ethRpcModule, method, parameters), expectedMessage);
    }

    [TestCase("eth_getBlockByNumber", new object?[] { "", false }, "missing value for required argument 0", TestName = "EmptyStringNonTrailing")]
    [TestCase("eth_getBlockByNumber", new object?[] { null, false }, "missing value for required argument 0", TestName = "NullNonTrailing")]
    public void MissingRequiredArgument_NonTrailingMarker_ReturnsInvalidParams(string method, object?[] parameters, string expectedMessage)
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        AssertInvalidParamsWithoutData(TestRequest(ethRpcModule, method, parameters), expectedMessage);
        ethRpcModule.DidNotReceive().eth_getBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<bool>());
    }

    // #13156: a parameter the caller got wrong is answered with -32602; it must not also cost the operator a WARN line
    // (with a stack trace) per request. The detail stays available at Debug.
    [Test]
    public void Invalid_params_are_not_logged_at_warn()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        const string rawParameters = """["0x1234","latest"]""";

        TestLogger warnLogger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        _logManager = new OneLoggerLogManager(new(warnLogger));
        AssertJsonRpcError(TestRawRequest(ethRpcModule, nameof(IEthRpcModule.eth_getBalance), rawParameters), ErrorCodes.InvalidParams);

        TestLogger debugLogger = new();
        _logManager = new OneLoggerLogManager(new(debugLogger));
        AssertJsonRpcError(TestRawRequest(ethRpcModule, nameof(IEthRpcModule.eth_getBalance), rawParameters), ErrorCodes.InvalidParams);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnLogger.LogList, Is.Empty, $"WARN/ERROR lines: {string.Join(" | ", warnLogger.LogList)}");
            Assert.That(debugLogger.LogList.Where(l => l.Contains("Incorrect JSON RPC parameters when calling eth_getBalance")), Is.Not.Empty);
            ethRpcModule.DidNotReceive().eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>());
        }
    }

    // The counterpart to the test above: the catch around parameter binding is broad, so it also swallows faults
    // the params cannot cause. Those are a condition of the node and must stay visible - at this site, and at the
    // processor, which would otherwise demote every -32602 from an unauthenticated caller to Debug.
    [Test]
    public void Node_faults_during_binding_stay_visible_and_omit_the_params()
    {
        IMetadataTestRpcModule module = Substitute.For<IMetadataTestRpcModule>();
        const string rawParameters = """[{"secret":"0x1234"}]""";

        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        _logManager = new OneLoggerLogManager(new(logger));
        using JsonRpcErrorResponse response = AssertJsonRpcError(
            TestRawRequest(module, "test_node_fault", rawParameters),
            ErrorCodes.InvalidParams);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Error!.OperatorActionable, Is.True, "the processor must not demote a node fault");
            Assert.That(logger.LogList.Where(static l => l.Contains("Failed to bind JSON RPC parameters for test_node_fault")), Is.Not.Empty);
            Assert.That(logger.LogList.Where(static l => l.Contains("secret")), Is.Empty, "the params must not be formatted on a fault that may be an exhausted heap");
            module.DidNotReceive().test_node_fault(Arg.Any<NodeFaultPayload>());
        }
    }

    [TestCaseSource(nameof(InvalidRawUtf8ParamCases))]
    public void Raw_utf8_params_invalid_arguments_return_invalid_params_before_invocation(
        string method,
        string rawParameters,
        string expectedMessage,
        Action<IEthRpcModule> assertNotInvoked)
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        AssertInvalidParamsWithoutData(TestRawRequest(ethRpcModule, method, rawParameters), expectedMessage);
        assertNotInvoked(ethRpcModule);
    }

    [Test]
    public void IncorrectMethodNameTest() =>
        AssertJsonRpcError(TestRequest(Substitute.For<IEthRpcModule>(), "incorrect_method"), ErrorCodes.MethodNotFound, ErrorMessages.MethodNotFound("incorrect_method"));

    [Test]
    public void NetVersionTest()
    {
        INetRpcModule netRpcModule = Substitute.For<INetRpcModule>();
        netRpcModule.net_version().ReturnsForAnyArgs(static x => ResultWrapper<string>.Success("1"));
        string result = RpcTest.AssertSuccess<string>(TestRequest(netRpcModule, "net_version", null));
        Assert.That(result, Is.EqualTo("1"));
    }

    [Test]
    public void Cached_result_wrapper_is_not_mutated_with_response_context()
    {
        INetRpcModule netRpcModule = Substitute.For<INetRpcModule>();
        ResultWrapper<string> cached = ResultWrapper<string>.Success("1");
        netRpcModule.net_version().Returns(cached);
        SingletonModulePool<INetRpcModule> pool = new(new SingletonFactory<INetRpcModule>(netRpcModule), true);

        JsonRpcRequest firstRequest = RpcTest.BuildJsonRequest("net_version");
        firstRequest.Id = 1;
        ResultWrapper<string> firstResponse = AssertWrapperResponse<string>(SendRequestWithPool(pool, firstRequest));

        JsonRpcRequest secondRequest = RpcTest.BuildJsonRequest("net_version");
        secondRequest.Id = 2;
        ResultWrapper<string> secondResponse = AssertWrapperResponse<string>(SendRequestWithPool(pool, secondRequest));

        Assert.That(firstResponse, Is.Not.SameAs(cached));
        Assert.That(secondResponse, Is.Not.SameAs(cached));
        Assert.That(cached.Id.IsMissing, Is.True);
        Assert.That(firstResponse.Id, Is.EqualTo(new JsonRpcId(1)));
        Assert.That(secondResponse.Id, Is.EqualTo(new JsonRpcId(2)));
    }

    [Test]
    public void Web3ShaTest()
    {
        IWeb3RpcModule web3RpcModule = Substitute.For<IWeb3RpcModule>();
        web3RpcModule.web3_sha3(Arg.Any<byte[]>()).ReturnsForAnyArgs(static _ => ResultWrapper<Hash256>.Success(TestItem.KeccakA));
        Hash256 result = RpcTest.AssertSuccess<Hash256>(TestRequest(web3RpcModule, "web3_sha3", "0x68656c6c6f20776f726c64"));
        Assert.That(result, Is.EqualTo(TestItem.KeccakA));
    }

    [Test]
    public void String_parameter_receives_raw_json_for_non_string_values()
    {
        IMetadataTestRpcModule metadataTestRpcModule = Substitute.For<IMetadataTestRpcModule>();
        string? captured = null;
        metadataTestRpcModule.test_string(Arg.Any<string>()).Returns(callInfo =>
        {
            captured = callInfo.Arg<string>();
            return ResultWrapper<string>.Success("ok");
        });

        string result = RpcTest.AssertSuccess<string>(TestRequest(metadataTestRpcModule, "test_string", new { a = 1 }));

        Assert.That(result, Is.EqualTo("ok"));
        Assert.That(captured, Is.EqualTo("""{"a":1}"""));
    }

    [Test]
    public void Array_parameter_reparses_string_wrapped_json_with_custom_converter()
    {
        IMetadataTestRpcModule metadataTestRpcModule = Substitute.For<IMetadataTestRpcModule>();
        byte[][]? captured = null;
        metadataTestRpcModule.test_byte_arrays(Arg.Any<byte[][]>()).Returns(callInfo =>
        {
            captured = callInfo.Arg<byte[][]>();
            return ResultWrapper<int>.Success(captured.Length);
        });

        int result = RpcTest.AssertSuccess<int>(TestRequest(metadataTestRpcModule, "test_byte_arrays", "[]"));

        Assert.That(result, Is.EqualTo(0));
        Assert.That(captured, Is.Empty);
    }

    [TestCaseSource(nameof(BlockForRpcTestSource))]
    public void BlockForRpc_should_expose_withdrawals_if_any(bool expected, Block block)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlockForRpc rpcBlock = new(block, false, specProvider);

        Assert.That(rpcBlock.WithdrawalsRoot, Is.EqualTo(block.WithdrawalsRoot));
        Assert.That(rpcBlock.Withdrawals, Is.EqualTo(block.Withdrawals));

        string json = new EthereumJsonSerializer().Serialize(rpcBlock);

        Assert.That(json.Contains("withdrawals\"", StringComparison.Ordinal), Is.EqualTo(expected));
        Assert.That(json.Contains("withdrawalsRoot", StringComparison.Ordinal), Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> BlockForRpcTestSource()
    {
        yield return new TestCaseData(
            true,
            Build.A.Block
                .WithWithdrawals(Build.A.Withdrawal
                    .WithAmount(1)
                    .WithRecipient(TestItem.AddressA)
                    .TestObject)
                .TestObject);
        yield return new TestCaseData(false, Build.A.Block.WithWithdrawals(null).TestObject);
    }

    [TestCase(false, TestName = "Unhandled_exception_returns_InternalError")]
    [TestCase(true, TestName = "Unhandled_operation_cancellation_without_request_cancellation_returns_InternalError")]
    public void Unhandled_exception_without_request_cancellation_returns_InternalError(bool operationCancellation)
    {
        IRpcModulePool<IEthRpcModule> pool = Substitute.For<IRpcModulePool<IEthRpcModule>>();
        Exception exception = operationCancellation
            ? new OperationCanceledException("module stopped")
            : new Exception("test");
        pool.GetModule(Arg.Any<bool>()).Returns(Task.FromException<IEthRpcModule>(exception));

        AssertJsonRpcError(TestRequestWithPool(pool, "eth_blockNumber"), ErrorCodes.InternalError);
    }

    [Test]
    public async Task Unhandled_exception_resolving_the_module_returns_InternalError()
    {
        IRpcModuleProvider moduleProvider = Substitute.For<IRpcModuleProvider>();
        moduleProvider.Resolve(Arg.Any<string>()).Throws(new Exception("test"));

        JsonRpcService service = new(moduleProvider, _logManager, _configurationProvider.GetConfig<IJsonRpcConfig>(), _gcKeeper);
        JsonRpcRequest request = RpcTest.BuildJsonRequest("eth_test");
        JsonRpcResponse response = await service.SendRequestAsync(request, _context);

        JsonRpcErrorResponse errorResponse = AssertJsonRpcError(response, ErrorCodes.InternalError);
        // Covers the second error.data producer, JsonRpcService.ReturnErrorResponse, which the module-invocation
        // path in Error_data_does_not_leak_stack_trace_or_build_paths never reaches.
        AssertErrorDataWithoutStackTrace(errorResponse);
    }

    // error.data reaches unauthenticated callers, so it must not carry the stack trace: our release builds
    // render frames with the build machine's absolute source paths and expose the internal call graph.
    [TestCase(ErrorCodes.InternalError, TestName = "InternalErrorArm")]
    [TestCase(ErrorCodes.InvalidParams, TestName = "InvalidParamsArm")]
    public void Error_data_does_not_leak_stack_trace_or_build_paths(int expectedCode)
    {
        Exception thrown = expectedCode == ErrorCodes.InternalError
            ? new InvalidOperationException("Stack empty.")
            : new ArgumentException("bad argument");

        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_getLogs(Arg.Any<Filter>()).Throws(thrown);

        using JsonRpcErrorResponse response = AssertJsonRpcError(TestRequest(ethRpcModule, "eth_getLogs", "{}"), expectedCode);

        AssertErrorDataWithoutStackTrace(response, thrown.GetType(), thrown.Message);
    }

    private static void AssertErrorDataWithoutStackTrace(JsonRpcErrorResponse response, Type? expectedType = null, string? expectedMessage = null)
    {
        string data = response.Error!.Data?.ToString() ?? string.Empty;
        Assert.Multiple(() =>
        {
            // Still actionable: the caller learns what went wrong.
            if (expectedType is not null) Assert.That(data, Does.Contain(expectedType.FullName!), data);
            if (expectedMessage is not null) Assert.That(data, Does.Contain(expectedMessage), data);
            // But nothing about where our source lives or how the call got there.
            Assert.That(data, Does.Not.Contain("   at "), data);
            Assert.That(data, Does.Not.Contain(".cs:line"), data);
            Assert.That(data, Does.Not.Contain("Nethermind.JsonRpc.JsonRpcService"), data);
        });
    }

    private static IEnumerable<TestCaseData> OutOfMemoryPools()
    {
        static IRpcModulePool<IEthRpcModule> Throwing(Exception ex)
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter>()).Throws(ex);
            return new SingletonModulePool<IEthRpcModule>(new SingletonFactory<IEthRpcModule>(ethRpcModule), true);
        }

        static IRpcModulePool<IEthRpcModule> FaultedRental()
        {
            IRpcModulePool<IEthRpcModule> pool = Substitute.For<IRpcModulePool<IEthRpcModule>>();
            pool.GetModule(Arg.Any<bool>()).Returns(Task.FromException<IEthRpcModule>(new OutOfMemoryException()));
            return pool;
        }

        yield return new TestCaseData(Throwing(new OutOfMemoryException())).SetName("{m}(module throws)");
        yield return new TestCaseData(Throwing(new TargetInvocationException(new OutOfMemoryException()))).SetName("{m}(module throws wrapped)");
        yield return new TestCaseData(FaultedRental()).SetName("{m}(module rental faults)");
    }

    [TestCaseSource(nameof(OutOfMemoryPools))]
    public void OutOfMemory_logs_without_request_parameters(IRpcModulePool<IEthRpcModule> pool)
    {
        const string marker = "0x00000000000000000000000000000000deadbeef";
        TestErrorLogManager logManager = new();
        _logManager = logManager;

        AssertJsonRpcError(TestRequestWithPool(pool, "eth_getBalance", marker, "latest"), ErrorCodes.InternalError);

        TestErrorLogManager.Error logged = logManager.Errors.Single(e => e.Exception is OutOfMemoryException or { InnerException: OutOfMemoryException });
        Assert.That(logged.Text, Does.Contain("eth_getBalance").And.Not.Contain(marker));
    }

    // #13156 follow-up: -32600 is overloaded. It is returned both for a request the caller got wrong ("Method is
    // required") and for a namespace this node has disabled, whose message is a remediation instruction for the
    // operator. Only the first may be demoted out of WARN, so the disabled cases carry OperatorActionable.
    [TestCase(ModuleResolution.Disabled, true)]
    [TestCase(ModuleResolution.EndpointDisabled, true)]
    [TestCase(ModuleResolution.NotAuthenticated, false)]
    public async Task Disabled_namespace_stays_operator_actionable(ModuleResolution resolution, bool expectedOperatorActionable)
    {
        IRpcModuleProvider moduleProvider = Substitute.For<IRpcModuleProvider>();
        moduleProvider.Check(Arg.Any<string>(), Arg.Any<JsonRpcContext>(), out Arg.Any<string?>(), out Arg.Any<RpcModuleProvider.ResolvedMethodInfo?>())
            .Returns(callInfo =>
            {
                callInfo[2] = "Debug";
                callInfo[3] = null;
                return resolution;
            });

        JsonRpcService service = new(moduleProvider, _logManager, _configurationProvider.GetConfig<IJsonRpcConfig>(), _gcKeeper);
        JsonRpcRequest request = RpcTest.BuildJsonRequest("debug_traceCall");
        using JsonRpcErrorResponse response = (JsonRpcErrorResponse)await service.SendRequestAsync(request, _context);

        Assert.That(response.Error!.Code, Is.EqualTo(ErrorCodes.InvalidRequest));
        Assert.That(ErrorCodes.IsRequestError(response.Error.Code), Is.True, "guards the premise: the code alone would demote this");
        Assert.That(response.Error.OperatorActionable, Is.EqualTo(expectedOperatorActionable));
    }

    [Test]
    public void Invocation_limit_exceeded_suppresses_warning()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_getLogs(Arg.Any<Filter>()).Throws(new LimitExceededException("limit"));

        using JsonRpcErrorResponse response = AssertJsonRpcError(
            TestRequest(ethRpcModule, "eth_getLogs", "{}"),
            ErrorCodes.LimitExceeded,
            "Too many requests");

        Assert.That(response.Error!.SuppressWarning, Is.True);
    }

    [Test]
    public void Overload_rejections_are_counted_from_both_shedding_paths()
    {
        // Per-path deltas so a double-count on one path cannot masquerade as both paths counted.
        // >= rather than == on each: the counter is a global metric other parallel tests may bump.
        long beforeInvocation = Metrics.JsonRpcOverloadRejections;

        // During-invocation path: the override-environment cap throws from inside the handler.
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_getLogs(Arg.Any<Filter>()).Throws(new ConcurrencyLimitReachedException("cap"));
        using JsonRpcErrorResponse invocationRejection = AssertJsonRpcError(
            TestRequest(ethRpcModule, "eth_getLogs", "{}"),
            ErrorCodes.LimitExceeded,
            "Too many requests");
        Assert.That(Metrics.JsonRpcOverloadRejections, Is.GreaterThanOrEqualTo(beforeInvocation + 1),
            "invocation-path rejection was not counted");

        long beforeRental = Metrics.JsonRpcOverloadRejections;

        // Before-invocation path: module rental times out.
        IRpcModulePool<IEthRpcModule> pool = Substitute.For<IRpcModulePool<IEthRpcModule>>();
        pool.GetModule(Arg.Any<bool>()).Returns(Task.FromException<IEthRpcModule>(new ModuleRentalTimeoutException("timeout")));
        using JsonRpcErrorResponse rentalRejection = AssertJsonRpcError(
            TestRequestWithPool(pool, "eth_getLogs", "{}"),
            ErrorCodes.ModuleTimeout,
            "Timeout");
        Assert.That(Metrics.JsonRpcOverloadRejections, Is.GreaterThanOrEqualTo(beforeRental + 1),
            "rental-path rejection was not counted");
    }

    [Test]
    public void Eth_call_holds_an_evm_permit_for_the_duration_of_the_invocation()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        int inFlightDuringInvocation = 0;
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ =>
        {
            inFlightDuringInvocation = _gate.InFlight;
            return ResultWrapper<HexBytes>.Success(ToHexBytes("0x01"));
        });

        RpcTest.AssertSuccess<HexBytes>(TestRequest(ethRpcModule, "eth_call", new LegacyTransactionForRpc()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inFlightDuringInvocation, Is.EqualTo(1), "the permit must be held while the method runs");
            Assert.That(_gate.InFlight, Is.EqualTo(0), "permit was not released");
        }
    }

    [Test]
    public async Task Saturated_evm_gate_sheds_eth_call_but_not_cheap_reads()
    {
        UseGate(SinglePermitConfig(maxQueueWaitMs: 100));
        using ManualResetEventSlim release = new();
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ =>
        {
            release.Wait(TestTimeout);
            return ResultWrapper<HexBytes>.Success(ToHexBytes("0x01"));
        });
        ethRpcModule.eth_blockNumber().Returns(Task.FromResult(ResultWrapper<ulong?>.Success(7)));
        IJsonRpcService service = CreateService(ethRpcModule);

        // A free permit lets the invocation run synchronously on the caller, so the substitute's Wait() would block this test thread; issue it from the pool.
        Task<JsonRpcResponse> blocked = Task.Run(() => SendEthCallAsync(service));
        await WaitUntil(() => _gate.InFlight == 1);

        long rejectionsBefore = Metrics.JsonRpcOverloadRejections;
        Task<JsonRpcResponse> shedTask = SendEthCallAsync(service);
        await WaitUntil(() => _gate.Queued == 1);
        _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(100));
        using JsonRpcErrorResponse shed = AssertJsonRpcError(await shedTask, ErrorCodes.LimitExceeded, "Too many requests");
        ulong? blockNumber = RpcTest.AssertSuccess<ulong?>(await service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_blockNumber"), _context));

        release.Set();
        RpcTest.AssertSuccess<HexBytes>(await blocked);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(shed.Error!.SuppressWarning, Is.True);
            Assert.That(Metrics.JsonRpcOverloadRejections, Is.GreaterThanOrEqualTo(rejectionsBefore + 1), "shed request was not counted as an overload rejection");
            Assert.That(blockNumber, Is.EqualTo(7UL), "cheap reads must not be gated");
            Assert.That(_gate.InFlight, Is.EqualTo(0));
        }
    }

    [TestCase(RpcEndpoint.Http, 1, 1, false, false, true, TestName = "HTTP requests may queue")]
    [TestCase(RpcEndpoint.Ws, 1, 1, false, false, false, TestName = "Single-lane WebSocket requests fail fast")]
    [TestCase(RpcEndpoint.Ws, 2, 1, false, false, true, TestName = "Multi-lane WebSocket requests may queue")]
    [TestCase(RpcEndpoint.IPC, 2, 1, false, false, false, TestName = "IPC requests fail fast when WebSocket is multi-lane")]
    [TestCase(RpcEndpoint.IPC, 1, 2, false, false, false, TestName = "IPC requests fail fast when IPC is multi-lane")]
    [TestCase(RpcEndpoint.IPC, 1, 2, true, false, false, TestName = "Explicitly authenticated IPC requests fail fast")]
    [TestCase(RpcEndpoint.Http, 1, 1, true, false, false, TestName = "Authenticated HTTP requests fail fast")]
    [TestCase(RpcEndpoint.Http, 1, 1, false, true, false, TestName = "Batch items fail fast")]
    public async Task Evm_queueing_policy_depends_on_transport_authentication_and_batch_membership(
        RpcEndpoint endpoint,
        int webSocketsProcessingConcurrency,
        int ipcProcessingConcurrency,
        bool authenticated,
        bool batchItem,
        bool expectedToQueue)
    {
        JsonRpcConfig config = new()
        {
            EthModuleConcurrentInstances = 1,
            EvmExecutionMaxQueueWaitMs = 10_000,
            EvmExecutionQueueLimit = 1,
            WebSocketsProcessingConcurrency = webSocketsProcessingConcurrency,
            IpcProcessingConcurrency = ipcProcessingConcurrency,
        };
        UseGate(config);

        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(ToHexBytes("0x01")));
        IJsonRpcService service = CreateService(ethRpcModule, config);
        using JsonRpcContext context = authenticated
            ? new JsonRpcContext(endpoint, url: new JsonRpcUrl(string.Empty, string.Empty, 0, endpoint, true, [ModuleType.Eth]))
            : new JsonRpcContext(endpoint);
        if (endpoint == RpcEndpoint.IPC)
        {
            Assert.That(context.IsAuthenticated, Is.True, "IPC contexts are authenticated even without an explicit URL");
        }
        JsonRpcRequest request = RpcTest.BuildJsonRequest("eth_call", new LegacyTransactionForRpc());
        request.IsBatchItem = batchItem;
        Task<JsonRpcResponse> response;
        using (Lease held = await HoldPermitAsync())
        {
            response = service.SendRequestAsync(request, context).AsTask();
            if (expectedToQueue)
            {
                await WaitUntil(() => _gate.Queued == 1);
            }
            else
            {
                Assert.That(_gate.Queued, Is.EqualTo(0));
            }
        }

        if (expectedToQueue)
        {
            using JsonRpcResponse granted = await response.WaitAsync(TestTimeout);
            RpcTest.AssertSuccess<HexBytes>(granted);
        }
        else
        {
            using JsonRpcErrorResponse rejected = AssertJsonRpcError(await response.WaitAsync(TestTimeout), ErrorCodes.LimitExceeded, "Too many requests");
        }
    }

    [Test]
    public async Task Production_resolved_service_disposes_admission_gate_and_settles_pending_requests()
    {
        JsonRpcConfig config = new()
        {
            EnabledModules = [ModuleType.Eth],
            EthModuleConcurrentInstances = 1,
            EvmExecutionMaxQueueWaitMs = 10_000,
            EvmExecutionQueueLimit = 1,
        };
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim invocationStarted = new();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ =>
        {
            invocationStarted.Set();
            release.Wait(TestTimeout);
            return ResultWrapper<HexBytes>.Success(ToHexBytes("0x01"));
        });

        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddLast<RpcModuleInfo>(_ => new RpcModuleInfo(typeof(IEthRpcModule), new SingletonModulePool<IEthRpcModule>(ethRpcModule, true)))
            .Build();
        bool containerDisposed = false;
        Task<JsonRpcResponse>? first = null;
        try
        {
            IJsonRpcService service = container.Resolve<IJsonRpcService>();
            using JsonRpcContext context = new(RpcEndpoint.Http);
            first = Task.Run(async () => await service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_call", new LegacyTransactionForRpc()), context));
            await WaitUntil(() => invocationStarted.IsSet);

            Task<JsonRpcResponse> second = service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_call", new LegacyTransactionForRpc()), context).AsTask();
            Assert.That(second.IsCompleted, Is.False, "the second real eth_call must wait for the occupied permit");

            using JsonRpcErrorResponse third = AssertJsonRpcError(
                await service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_call", new LegacyTransactionForRpc()), context).AsTask().WaitAsync(TestTimeout),
                ErrorCodes.LimitExceeded,
                "Too many requests");

            container.Dispose();
            containerDisposed = true;

            // Disposal faults the queued admission before the holder releases, while still completing the queued task.
            using JsonRpcErrorResponse settled = AssertJsonRpcError(await second.WaitAsync(TestTimeout), ErrorCodes.InternalError);

            release.Set();
            using JsonRpcResponse completed = await first.WaitAsync(TestTimeout);
            RpcTest.AssertSuccess<HexBytes>(completed);
        }
        finally
        {
            release.Set();
            if (first is not null)
            {
                await first.WaitAsync(TestTimeout);
            }

            if (!containerDisposed)
            {
                container.Dispose();
            }
        }
    }

    [Test]
    public async Task Ungated_methods_never_touch_the_gate()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_blockNumber().Returns(Task.FromResult(ResultWrapper<ulong?>.Success(7)));
        IJsonRpcService service = CreateService(ethRpcModule);

        for (int i = 0; i < 1_000; i++)
        {
            RpcTest.AssertSuccess<ulong?>(await service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_blockNumber"), _context));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.InFlight, Is.EqualTo(0));
            Assert.That(_gate.Queued, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Cancelled_admission_wait_propagates_operation_canceled_to_the_caller()
    {
        UseGate(SinglePermitConfig(maxQueueWaitMs: 100));
        IJsonRpcService service = CreateService(Substitute.For<IEthRpcModule>());
        using EvmAdmissionGate.Lease held = await HoldPermitAsync();
        using CancellationTokenSource cancellation = new();

        Task<JsonRpcResponse> waiting = service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_call", new LegacyTransactionForRpc()), _context, cancellation.Token).AsTask();
        await WaitUntil(() => _gate.Queued == 1);

        cancellation.Cancel();
        // Cancellation is observed lazily: the next sweep drops the waiter whose caller has gone.
        _timeProvider.AdvanceAndFireTimer(TimeSpan.Zero);

        Assert.CatchAsync<OperationCanceledException>(() => waiting.WaitAsync(TestTimeout), "A cancelled admission must not produce a response.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.Queued, Is.EqualTo(0));
            Assert.That(_gate.InFlight, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Cancellation_after_admission_grant_is_observed_before_invocation()
    {
        JsonRpcConfig config = SinglePermitConfig(maxQueueWaitMs: 10_000);
        UseGate(config);
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(ToHexBytes("0x01")));
        IJsonRpcService service = CreateService(ethRpcModule, config);
        using CancellationTokenSource cancellation = new();
        CapturingSynchronizationContext synchronizationContext = new();
        Task<JsonRpcResponse> waiting;
        using (Lease held = await HoldPermitAsync())
        {
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(synchronizationContext);
                waiting = service.SendRequestAsync(
                    RpcTest.BuildJsonRequest("eth_call", new LegacyTransactionForRpc()),
                    _context,
                    cancellation.Token).AsTask();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            Assert.That(_gate.Queued, Is.EqualTo(1));
        }

        await WaitUntil(() => synchronizationContext.PendingCount > 0);

        cancellation.Cancel();
        synchronizationContext.Drain();

        Assert.CatchAsync<OperationCanceledException>(() => waiting.WaitAsync(TestTimeout));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.InFlight, Is.EqualTo(0), "the granted lease must be released when cancellation wins before invocation");
            Assert.That(_gate.Queued, Is.EqualTo(0));
            ethRpcModule.DidNotReceive().eth_call(Arg.Any<SignableTransactionForRpc>());
        }
    }

    [TestCaseSource(nameof(FailingEvmRequests))]
    public async Task Evm_permit_is_released_when_the_request_fails(Action<IEthRpcModule> configure, string method, object? parameter, int expectedCode)
    {
        UseGate(SinglePermitConfig(maxQueueWaitMs: 100));
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        configure(ethRpcModule);
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(ToHexBytes("0x01")));
        IJsonRpcService service = CreateService(ethRpcModule);

        using JsonRpcErrorResponse failure = AssertJsonRpcError(
            await service.SendRequestAsync(RpcTest.BuildJsonRequest(method, parameter), _context).AsTask().WaitAsync(TestTimeout),
            expectedCode);
        Assert.That(_gate.InFlight, Is.EqualTo(0));

        // The single permit must be free again, otherwise this waits for a sweep the manual clock never fires.
        RpcTest.AssertSuccess<HexBytes>(await SendEthCallAsync(service));
    }

    private static IEnumerable<TestCaseData> FailingEvmRequests()
    {
        yield return new TestCaseData(
            (Action<IEthRpcModule>)(static module => module.eth_estimateGas(Arg.Any<SignableTransactionForRpc>()).ThrowsForAnyArgs(new InvalidOperationException("boom"))),
            "eth_estimateGas",
            new LegacyTransactionForRpc(),
            ErrorCodes.InternalError).SetName("Synchronous exception");
        yield return new TestCaseData(
            (Action<IEthRpcModule>)(static module => module.eth_fillTransaction(Arg.Any<SignableTransactionForRpc>())
                .ReturnsForAnyArgs(Task.FromException<ResultWrapper<FillTransactionResult>>(new InvalidOperationException("boom")))),
            "eth_fillTransaction",
            new LegacyTransactionForRpc(),
            ErrorCodes.InternalError).SetName("Faulted task");
        yield return new TestCaseData(
            (Action<IEthRpcModule>)(static _ => { }),
            "eth_estimateGas",
            "not a transaction",
            ErrorCodes.InvalidParams).SetName("Invalid params");
    }

    [Test]
    public async Task Evm_permit_is_released_when_the_module_rental_fails()
    {
        UseGate(SinglePermitConfig(maxQueueWaitMs: 100));
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(ToHexBytes("0x01")));
        IRpcModulePool<IEthRpcModule> pool = Substitute.For<IRpcModulePool<IEthRpcModule>>();
        pool.GetModule(Arg.Any<bool>()).Returns(Task.FromException<IEthRpcModule>(new LimitExceededException("limit")), Task.FromResult(ethRpcModule));
        IJsonRpcService service = CreateService(pool);

        using JsonRpcErrorResponse rejected = AssertJsonRpcError(await SendEthCallAsync(service), ErrorCodes.LimitExceeded, "Too many requests");
        Assert.That(_gate.InFlight, Is.EqualTo(0));

        RpcTest.AssertSuccess<HexBytes>(await SendEthCallAsync(service));
    }

    [TestCase(true, 0, true, TestName = "Raw params below one unit overtake a heavier waiter")]
    [TestCase(true, 2, false, TestName = "Raw params of the same weight queue behind it")]
    [TestCase(false, 0, true, TestName = "Parsed params below one unit overtake a heavier waiter")]
    [TestCase(false, 2, false, TestName = "Parsed params of the same weight queue behind it")]
    public async Task Evm_request_weight_follows_its_params_size(bool rawParams, int paddingUnits, bool overtakes)
    {
        UseGate(SinglePermitConfig(maxQueueWaitMs: 10_000));
        List<int> servedInputLengths = [];
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(callInfo =>
        {
            lock (servedInputLengths)
            {
                servedInputLengths.Add(callInfo.Arg<SignableTransactionForRpc>() is LegacyTransactionForRpc { Input: { } input } ? input.Length : -1);
            }
            return ResultWrapper<HexBytes>.Success(ToHexBytes("0x01"));
        });
        IJsonRpcService service = CreateService(ethRpcModule);
        // Calldata is hex-encoded on the wire, so half a unit of bytes pads the params by one unit; the extra byte tells the two apart.
        LegacyTransactionForRpc transaction = new() { Input = new byte[paddingUnits * EvmAdmissionGate.BytesPerWeightUnit / 2 + 1] };
        LegacyTransactionForRpc threeUnitTransaction = new() { Input = new byte[2 * EvmAdmissionGate.BytesPerWeightUnit / 2] };
        JsonRpcRequest request = rawParams
            ? BuildRawRequest("eth_call", $"[{new EthereumJsonSerializer().Serialize(transaction)}]")
            : RpcTest.BuildJsonRequest("eth_call", transaction);

        Task<JsonRpcResponse> queued;
        Task<JsonRpcResponse> weighed;
        using (await HoldPermitAsync())
        {
            queued = service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_call", threeUnitTransaction), _context).AsTask();
            await WaitUntil(() => _gate.Queued == 1);
            weighed = service.SendRequestAsync(request, _context).AsTask();
            await WaitUntil(() => _gate.Queued == 2);
        }

        RpcTest.AssertSuccess<HexBytes>(await queued.WaitAsync(TestTimeout));
        RpcTest.AssertSuccess<HexBytes>(await weighed.WaitAsync(TestTimeout));
        int[] expectedOrder = overtakes
            ? [transaction.Input!.Length, threeUnitTransaction.Input!.Length]
            : [threeUnitTransaction.Input!.Length, transaction.Input!.Length];
        Assert.That(servedInputLengths, Is.EqualTo(expectedOrder), "lighter requests are served first, equal weights FIFO");
    }

    [TestCase(true, ErrorCodes.LimitExceeded, 0, TestName = "Saturated gate sheds before binding")]
    [TestCase(false, ErrorCodes.InvalidParams, 1, TestName = "Free gate binds, then rejects")]
    public async Task Gated_parameters_are_bound_only_after_admission(bool saturated, int expectedCode, int expectedBindings)
    {
        UseGate(SinglePermitConfig(maxQueueWaitMs: 100));
        IJsonRpcService service = CreateService(Substitute.For<IMetadataTestRpcModule>());
        using EvmAdmissionGate.Lease held = saturated ? await HoldPermitAsync() : default;
        int bindingsBefore = BindingProbeConverter.Bindings;

        Task<JsonRpcResponse> responseTask = service.SendRequestAsync(RpcTest.BuildJsonRequest("eth_call", new object()), _context).AsTask();
        if (saturated)
        {
            await WaitUntil(() => _gate.Queued == 1);
            _timeProvider.AdvanceAndFireTimer(TimeSpan.FromMilliseconds(100));
        }

        using JsonRpcErrorResponse response = AssertJsonRpcError(await responseTask.WaitAsync(TestTimeout), expectedCode);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BindingProbeConverter.Bindings - bindingsBefore, Is.EqualTo(expectedBindings));
            Assert.That(_gate.InFlight, Is.EqualTo(saturated ? 1 : 0), "only the externally held permit may remain in flight");
        }
    }

    // Every caller waits for another task to make progress, so this sleeps rather than yielding: on a saturated agent
    // a yield loop competes for the pool with the very task it is waiting for.
    private static async Task WaitUntil(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.That(Environment.TickCount64, Is.LessThan(deadline), "condition not reached in time");
            await Task.Delay(5);
        }
    }

    [TestCaseSource(nameof(ModuleRentalOverloadExceptions))]
    public void Module_rental_overload_does_not_log_or_return_exception_data(
        Exception exception,
        int expectedCode,
        string expectedMessage)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        _logManager = new OneLoggerLogManager(new ILogger(logger));

        IRpcModulePool<IEthRpcModule> pool = Substitute.For<IRpcModulePool<IEthRpcModule>>();
        pool.GetModule(Arg.Any<bool>()).Returns(Task.FromException<IEthRpcModule>(exception));

        using JsonRpcErrorResponse response = AssertJsonRpcError(
            TestRequestWithPool(pool, "eth_getLogs", "{}"),
            expectedCode,
            expectedMessage);

        Assert.That(response.Error!.SuppressWarning, Is.True);
        Assert.That(response.Error.Data, Is.Null);
        logger.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception?>());
    }

    private static IEnumerable<TestCaseData> ModuleRentalOverloadExceptions()
    {
        yield return new TestCaseData(
            new LimitExceededException("limit"),
            ErrorCodes.LimitExceeded,
            "Too many requests");
        yield return new TestCaseData(
            new ModuleRentalTimeoutException("timeout"),
            ErrorCodes.ModuleTimeout,
            "Timeout");
    }

    [Test]
    public void Missing_trie_node_exception_returns_resource_not_found()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_getLogs(Arg.Any<Filter>())
            .Throws(new MissingTrieNodeException("Node missing", null, TreePath.Empty, TestItem.KeccakA));

        using JsonRpcErrorResponse response = AssertJsonRpcError(TestRequest(ethRpcModule, "eth_getLogs", "{}"), ErrorCodes.ResourceNotFound, "Node missing");
    }

    [RpcModule(ModuleType.Eth)]
    public interface IMetadataTestRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Test method used to verify JSON-RPC parameter metadata handling.")]
        ResultWrapper<string> test_string(string value);

        [JsonRpcMethod(Description = "Test method used to verify JSON-RPC array parameter metadata handling.")]
        ResultWrapper<int> test_byte_arrays(byte[][] value);

        [JsonRpcMethod(Description = "Test method used to verify that gated requests are admitted before their parameters are bound.", IsEvmExecution = true)]
        ResultWrapper<string> eth_call(BindingProbe probe);
        [JsonRpcMethod(Description = "Test method used to verify JSON-RPC parameter binding faults.")]
        ResultWrapper<string> test_node_fault(NodeFaultPayload value);
    }

    [JsonConverter(typeof(BindingProbeConverter))]
    public sealed class BindingProbe;

    public sealed class BindingProbeConverter : JsonConverter<BindingProbe>
    {
        public static int Bindings;

        public override BindingProbe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Interlocked.Increment(ref Bindings);
            throw new JsonException("binding probe");
        }

        public override void Write(Utf8JsonWriter writer, BindingProbe value, JsonSerializerOptions options) => throw new NotSupportedException();
    }

    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = [];

        public int PendingCount
        {
            get
            {
                lock (_callbacks)
                {
                    return _callbacks.Count;
                }
            }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_callbacks)
            {
                _callbacks.Enqueue((callback, state));
            }
        }

        public void Drain()
        {
            SynchronizationContext? previousContext = Current;
            SynchronizationContext.SetSynchronizationContext(this);
            try
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) callback;
                    lock (_callbacks)
                    {
                        if (_callbacks.Count == 0)
                        {
                            return;
                        }

                        callback = _callbacks.Dequeue();
                    }

                    callback.Callback(callback.State);
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    [JsonConverter(typeof(NodeFaultPayloadConverter))]
    public sealed class NodeFaultPayload;

    /// <summary>Stands in for a fault the caller's params cannot cause, arriving from inside parameter binding.</summary>
    private sealed class NodeFaultPayloadConverter : JsonConverter<NodeFaultPayload>
    {
        public override NodeFaultPayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new ObjectDisposedException(nameof(NodeFaultPayloadConverter));

        public override void Write(Utf8JsonWriter writer, NodeFaultPayload value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    public class PolymorphicBasePayload
    {
        public string? BaseValue { get; init; }
    }

    public sealed class PolymorphicDerivedPayload : PolymorphicBasePayload
    {
        public string? DerivedValue { get; init; }
    }

    public sealed class SealedPayload
    {
        public string? Value { get; init; }
    }

    [Test]
    public async Task Legacy_two_argument_json_rpc_service_uses_default_three_argument_forwarder()
    {
        IJsonRpcService service = new LegacyJsonRpcService();
        using JsonRpcContext context = new(RpcEndpoint.Http);
        JsonRpcRequest request = RpcTest.BuildJsonRequest("eth_blockNumber");

        using JsonRpcResponse response = await service.SendRequestAsync(request, context, new CancellationToken(canceled: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Id, Is.EqualTo(request.Id));
            Assert.That(((LegacyJsonRpcService)service).Calls, Is.EqualTo(1));
        }
    }

    private sealed class LegacyJsonRpcService : IJsonRpcService
    {
        public int Calls { get; private set; }

        public ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context)
        {
            Calls++;
            return ValueTask.FromResult<JsonRpcResponse>(new JsonRpcSuccessResponse { Id = request.Id });
        }

        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null) =>
            throw new NotSupportedException();

        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null) =>
            throw new NotSupportedException();
    }
}
