// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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
using NUnit.Framework.Constraints;
using Testably.Abstractions;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class JsonRpcServiceTests
{
    [SetUp]
    public void Initialize()
    {
        _configurationProvider = new ConfigProvider();
        _logManager = LimboLogs.Instance;
        _context = new JsonRpcContext(RpcEndpoint.Http);
        _previousStrictHexFormat = EthereumJsonSerializer.StrictHexFormat;
        EthereumJsonSerializer.StrictHexFormat = _configurationProvider.GetConfig<IJsonRpcConfig>().StrictHexFormat;
        _timeProvider = new ManualTimeProvider();
        UseGate(_configurationProvider.GetConfig<IJsonRpcConfig>());
    }

    [TearDown]
    public void TearDown()
    {
        EthereumJsonSerializer.StrictHexFormat = _previousStrictHexFormat;
        _context?.Dispose();
    }

    private bool _previousStrictHexFormat;

    private IJsonRpcService _jsonRpcService = null!;
    private IConfigProvider _configurationProvider = null!;
    private ILogManager _logManager = null!;
    private JsonRpcContext _context = null!;
    private EvmAdmissionGate _gate = null!;
    private ManualTimeProvider _timeProvider = null!;

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

    private IJsonRpcService CreateService<T>(IRpcModulePool<T> pool) where T : IRpcModule
    {
        RpcModuleProvider moduleProvider = new(new RealFileSystem(), _configurationProvider.GetConfig<IJsonRpcConfig>(), new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(pool);
        return new JsonRpcService(moduleProvider, _logManager, _configurationProvider.GetConfig<IJsonRpcConfig>(), _gate);
    }

    private IJsonRpcService CreateService<T>(T module) where T : IRpcModule =>
        CreateService(new SingletonModulePool<T>(new SingletonFactory<T>(module), true));

    private void UseGate(IJsonRpcConfig config) => _gate = new EvmAdmissionGate(config, _logManager, _timeProvider);

    private static JsonRpcConfig SinglePermitConfig(int maxQueueWaitMs) =>
        new() { EvmExecutionConcurrency = 1, EthModuleConcurrentInstances = 1, MaxQueueWaitMs = maxQueueWaitMs };

    private ValueTask<EvmAdmissionGate.Lease> HoldPermitAsync() => _gate.AdmitAsync(EvmAdmissionGate.MinWeight, CancellationToken.None);

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

    [TestCase(false)]
    [TestCase(true)]
    public async Task Admin_peers_is_working_with_empty_or_null_params(bool useNullParams)
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
    [TestCase(true)]
    [TestCase(false)]
    public void Returns_module_to_pool_only_after_a_streamed_result_is_disposed(bool streamed)
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
    public void Eth_getTransactionReceipt_properly_fails_given_wrong_parameters()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();

        AssertJsonRpcError(TestRequest(ethRpcModule, "eth_getTransactionReceipt", """["0x80757153e93d1b475e203406727b62a501187f63e23b8fa999279e219ee3be71"]"""), ErrorCodes.InvalidParams);
    }

    [TestCase("eth_getBlockByNumber", new object?[] { }, "missing value for required argument 0", TestName = "FirstArgOmitted")]
    [TestCase("eth_feeHistory", new object?[] { "0x1", "latest" }, "missing value for required argument 2", TestName = "LaterArgOmitted")]
    public void MissingRequiredArgument_ReturnsGethStyleError(string method, object?[] parameters, string expectedMessage)
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        AssertInvalidParamsWithoutData(TestRequest(ethRpcModule, method, parameters), expectedMessage);
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
            _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            return ResultWrapper<HexBytes>.Success(ToHexBytes("0x01"));
        });

        RpcTest.AssertSuccess<HexBytes>(TestRequest(ethRpcModule, "eth_call", new LegacyTransactionForRpc()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inFlightDuringInvocation, Is.EqualTo(1), "the permit must be held while the method runs");
            Assert.That(_gate.ServiceTimeMs, Is.EqualTo(1), "admission did not observe the call");
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
            Assert.That(_gate.ServiceTimeMs, Is.EqualTo(0));
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

    [TestCaseSource(nameof(FailingEvmRequests))]
    public async Task Evm_permit_is_released_when_the_request_fails(Action<IEthRpcModule> configure, string method, object? parameter, int expectedCode, bool invoked)
    {
        const double presetServiceTimeMs = 1_000;
        UseGate(SinglePermitConfig(maxQueueWaitMs: 100));
        _gate.SetServiceTimeMs(presetServiceTimeMs);
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        configure(ethRpcModule);
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(ToHexBytes("0x01")));
        IJsonRpcService service = CreateService(ethRpcModule);

        using JsonRpcErrorResponse failure = AssertJsonRpcError(
            await service.SendRequestAsync(RpcTest.BuildJsonRequest(method, parameter), _context).AsTask().WaitAsync(TestTimeout),
            expectedCode);
        Constraint serviceTime = invoked ? Is.LessThan(presetServiceTimeMs) : Is.EqualTo(presetServiceTimeMs);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.InFlight, Is.EqualTo(0));
            Assert.That(_gate.ServiceTimeMs, serviceTime, "only an invocation that ran is a service-time observation");
        }

        // The single permit must be free again, otherwise this waits for a sweep the manual clock never fires.
        RpcTest.AssertSuccess<HexBytes>(await SendEthCallAsync(service));
    }

    private static IEnumerable<TestCaseData> FailingEvmRequests()
    {
        yield return new TestCaseData(
            (Action<IEthRpcModule>)(static module => module.eth_estimateGas(Arg.Any<SignableTransactionForRpc>()).ThrowsForAnyArgs(new InvalidOperationException("boom"))),
            "eth_estimateGas",
            new LegacyTransactionForRpc(),
            ErrorCodes.InternalError,
            true).SetName("Synchronous exception");
        yield return new TestCaseData(
            (Action<IEthRpcModule>)(static module => module.eth_fillTransaction(Arg.Any<SignableTransactionForRpc>())
                .ReturnsForAnyArgs(Task.FromException<ResultWrapper<FillTransactionResult>>(new InvalidOperationException("boom")))),
            "eth_fillTransaction",
            new LegacyTransactionForRpc(),
            ErrorCodes.InternalError,
            true).SetName("Faulted task");
        yield return new TestCaseData(
            (Action<IEthRpcModule>)(static _ => { }),
            "eth_estimateGas",
            "not a transaction",
            ErrorCodes.InvalidParams,
            false).SetName("Invalid params");
    }

    [Test]
    public async Task Evm_permit_is_released_without_sampling_when_the_module_rental_fails()
    {
        const double presetServiceTimeMs = 1_000;
        UseGate(SinglePermitConfig(maxQueueWaitMs: 100));
        _gate.SetServiceTimeMs(presetServiceTimeMs);
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(ToHexBytes("0x01")));
        IRpcModulePool<IEthRpcModule> pool = Substitute.For<IRpcModulePool<IEthRpcModule>>();
        pool.GetModule(Arg.Any<bool>()).Returns(Task.FromException<IEthRpcModule>(new LimitExceededException("limit")), Task.FromResult(ethRpcModule));
        IJsonRpcService service = CreateService(pool);

        using JsonRpcErrorResponse rejected = AssertJsonRpcError(await SendEthCallAsync(service), ErrorCodes.LimitExceeded, "Too many requests");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_gate.InFlight, Is.EqualTo(0));
            Assert.That(_gate.ServiceTimeMs, Is.EqualTo(presetServiceTimeMs), "a rental failure never occupied the permit doing work");
        }

        RpcTest.AssertSuccess<HexBytes>(await SendEthCallAsync(service));
    }

    [TestCase(true, 0, false, TestName = "Raw params below one unit weigh one")]
    [TestCase(true, 2, true, TestName = "Raw params are weighed by size")]
    [TestCase(false, 0, false, TestName = "Parsed params below one unit weigh one")]
    [TestCase(false, 2, true, TestName = "Parsed params are weighed by size")]
    public async Task Evm_request_weight_follows_its_params_size(bool rawParams, int paddingUnits, bool shed)
    {
        // Predicted wait = queued work no heavier than the request x service time / permits: behind one queued
        // three-unit request, at a 5 s service time and a 10 s budget, a request weighing up to two units overtakes
        // it and queues while three or more units are shed up front.
        UseGate(SinglePermitConfig(maxQueueWaitMs: 10_000));
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<SignableTransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(ToHexBytes("0x01")));
        IJsonRpcService service = CreateService(ethRpcModule);
        // Calldata is hex-encoded on the wire, so half a unit of bytes pads the params by one unit.
        LegacyTransactionForRpc transaction = new() { Input = new byte[paddingUnits * EvmAdmissionGate.BytesPerWeightUnit / 2] };
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
            _gate.SetServiceTimeMs(5_000);

            weighed = service.SendRequestAsync(request, _context).AsTask();
            Assert.That(weighed.IsCompleted, Is.EqualTo(shed), "a shed request is answered up front, an admitted one waits for the permit");
        }

        RpcTest.AssertSuccess<HexBytes>(await queued.WaitAsync(TestTimeout));
        JsonRpcResponse response = await weighed.WaitAsync(TestTimeout);
        if (shed)
        {
            using JsonRpcErrorResponse error = AssertJsonRpcError(response, ErrorCodes.LimitExceeded, "Too many requests");
        }
        else
        {
            RpcTest.AssertSuccess<HexBytes>(response);
        }
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

        [JsonRpcMethod(Description = "Test method used to verify that gated requests are admitted before their parameters are bound.")]
        ResultWrapper<string> eth_call(BindingProbe probe);
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
}
