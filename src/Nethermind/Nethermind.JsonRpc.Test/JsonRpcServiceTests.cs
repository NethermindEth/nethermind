// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Trie;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
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
        SendRequestWithPool(
            new SingletonModulePool<T>(new SingletonFactory<T>(module), true),
            new JsonRpcRequest
            {
                JsonRpc = "2.0",
                Method = method,
                ParamsUtf8 = Encoding.UTF8.GetBytes(rawParameters),
                ParamsKind = JsonValueKind.Array,
                Id = 67
            });

    private JsonRpcResponse SendRequestWithPool<T>(IRpcModulePool<T> pool, JsonRpcRequest request) where T : IRpcModule
    {
        RpcModuleProvider moduleProvider = new(new RealFileSystem(), _configurationProvider.GetConfig<IJsonRpcConfig>(), new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(pool);
        _jsonRpcService = new JsonRpcService(moduleProvider, _logManager, _configurationProvider.GetConfig<IJsonRpcConfig>());
        JsonRpcResponse response = _jsonRpcService.SendRequestAsync(request, _context).Result;
        Assert.That(response.Id, Is.EqualTo(request.Id));
        return response;
    }

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
        ethRpcModule.eth_call(Arg.Any<TransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(expected));
        HexBytes result = RpcTest.AssertSuccess<HexBytes>(TestRequest(ethRpcModule, "eth_call", new LegacyTransactionForRpc()));
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Value_type_result_failure_without_error_data_does_not_emit_default_data()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule.eth_call(Arg.Any<TransactionForRpc>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Fail("out of gas", ErrorCodes.ExecutionError));

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
        ethRpcModule.eth_call(Arg.Any<TransactionForRpc>(), Arg.Any<BlockParameter?>()).ReturnsForAnyArgs(_ => ResultWrapper<HexBytes>.Success(expected));

        HexBytes result = RpcTest.AssertSuccess<HexBytes>(TestRequest(ethRpcModule, "eth_call", parameters));
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Raw_utf8_params_keep_explicit_nullable_trailing_defaults()
    {
        IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
        ethRpcModule
            .eth_call(
                Arg.Any<TransactionForRpc>(),
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
        AssertJsonRpcError(TestRequest(Substitute.For<IEthRpcModule>(), "incorrect_method"), ErrorCodes.MethodNotFound, "the method incorrect_method does not exist/is not available");

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

    [Test]
    public async Task Unhandled_exception_returns_InternalError()
    {
        IRpcModuleProvider moduleProvider = Substitute.For<IRpcModuleProvider>();
        moduleProvider.Resolve(Arg.Any<string>()).Throws(new Exception("test"));

        JsonRpcService service = new(moduleProvider, _logManager, _configurationProvider.GetConfig<IJsonRpcConfig>());
        JsonRpcRequest request = RpcTest.BuildJsonRequest("eth_test");
        JsonRpcResponse response = await service.SendRequestAsync(request, _context);

        AssertJsonRpcError(response, ErrorCodes.InternalError);
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
