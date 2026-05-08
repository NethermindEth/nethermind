// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.SszRest;

[TestFixture]
public class SszMiddlewareTests
{
    private IEngineRpcModule _engineModule = null!;

    private IJsonRpcUrlCollection _urlCollection = null!;
    private IRpcAuthentication _auth = null!;
    private IProcessExitSource _processExitSource = null!;
    private SszMiddleware _middleware = null!;

    private const int AuthenticatedPort = 8551;
    private const string OctetStream = "application/octet-stream";
    private const string BearerToken =
        "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9" +
        ".eyJpYXQiOjE2NDQ5OTQ5NzF9" +
        ".RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PME";

    [SetUp]
    public void SetUp()
    {
        _engineModule = Substitute.For<IEngineRpcModule>();

        _urlCollection = Substitute.For<IJsonRpcUrlCollection>();
        _auth = Substitute.For<IRpcAuthentication>();
        _processExitSource = Substitute.For<IProcessExitSource>();
        _processExitSource.Token.Returns(CancellationToken.None);

        JsonRpcUrl engineUrl = new("http", "localhost", AuthenticatedPort, RpcEndpoint.Http, true, ["engine"]);
        _urlCollection.TryGetValue(AuthenticatedPort, out Arg.Any<JsonRpcUrl?>())
            .Returns(x => { x[1] = engineUrl; return true; });
        _auth.Authenticate(Arg.Any<string>()).Returns(true);

        _middleware = BuildMiddleware();
    }

    private SszMiddleware BuildMiddleware(RequestDelegate? next = null)
    {
        RequestDelegate passthrough = next ?? (_ => Task.CompletedTask);

        ISszEndpointHandler[] handlers =
        [
            new NewPayloadSszHandler<NewPayloadDescriptorV1, NewPayloadV1RequestWire>(_engineModule),
            new NewPayloadSszHandler<NewPayloadDescriptorV2, NewPayloadV2RequestWire>(_engineModule),
            new NewPayloadSszHandler<NewPayloadDescriptorV3, NewPayloadV3RequestWire>(_engineModule),
            new NewPayloadSszHandler<NewPayloadDescriptorV4, NewPayloadV4RequestWire>(_engineModule),
            new NewPayloadSszHandler<NewPayloadDescriptorV5, NewPayloadV5RequestWire>(_engineModule),

            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV1, ForkchoiceUpdatedV1RequestWire>(_engineModule),
            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV2, ForkchoiceUpdatedV2RequestWire>(_engineModule),
            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV3, ForkchoiceUpdatedV3RequestWire>(_engineModule),
            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV4, ForkchoiceUpdatedRequestWire>(_engineModule),

            new GetPayloadSszHandler<GetPayloadDescriptorV1, ExecutionPayload>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV2, GetPayloadV2Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV3, GetPayloadV3Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV4, GetPayloadV4Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV5, GetPayloadV5Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV6, GetPayloadV6Result>(_engineModule),

            new GetBlobsV1SszHandler(_engineModule),
            new GetBlobsV2SszHandler<GetBlobsDescriptorV2>(_engineModule),
            new GetBlobsV2SszHandler<GetBlobsDescriptorV3>(_engineModule),

            new GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV1, ExecutionPayloadBodyV1Result>(_engineModule),
            new GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV2, ExecutionPayloadBodyV2Result>(_engineModule),

            new GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV1, ExecutionPayloadBodyV1Result>(_engineModule),
            new GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV2, ExecutionPayloadBodyV2Result>(_engineModule),

            new ClientVersionSszHandler(_engineModule),
            new CapabilitiesSszHandler(_engineModule),
            new TransitionConfigurationSszHandler(_engineModule),
        ];

        return new SszMiddleware(
            passthrough,
            _urlCollection,
            _auth,
            handlers,
            _processExitSource,
            LimboLogs.Instance);
    }

    private static DefaultHttpContext MakeBaseContext(string method, string path, int port)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Connection.LocalPort = port;
        ctx.Request.Headers.Authorization = BearerToken;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static DefaultHttpContext MakePostContext(string path, byte[] body, int port = AuthenticatedPort)
    {
        DefaultHttpContext ctx = MakeBaseContext("POST", path, port);
        ctx.Request.ContentType = OctetStream;
        ctx.Request.ContentLength = body.Length;
        ctx.Request.Body = new MemoryStream(body);
        return ctx;
    }

    private static DefaultHttpContext MakeGetContext(string path, int port = AuthenticatedPort)
    {
        DefaultHttpContext ctx = MakeBaseContext("GET", path, port);
        ctx.Request.Headers.Accept = OctetStream;
        ctx.Request.Body = Stream.Null;
        return ctx;
    }

    private static byte[] ResponseBytes(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using MemoryStream ms = new();
        ctx.Response.Body.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] ToBytes(ArrayPoolSpan<byte> span)
    {
        try { return ((ReadOnlySpan<byte>)span).ToArray(); }
        finally { span.Dispose(); }
    }

    private static (byte[] buffer, int length) ToPooledTuple(ArrayPoolSpan<byte> span)
    {
        int len = span.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            ((ReadOnlySpan<byte>)span).CopyTo(rented);
        }
        finally
        {
            span.Dispose();
        }
        return (rented, len);
    }

    [TestCase(1, "/engine/v1/payloads")]
    [TestCase(2, "/engine/v2/payloads")]
    public async Task NewPayload_routes_to_correct_engine_module_version(int version, string path)
    {
        PayloadStatusV1 status = new() { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA };
        _engineModule.engine_newPayloadV1(Arg.Any<ExecutionPayload>())
            .Returns(ResultWrapper<PayloadStatusV1>.Success(status));
        _engineModule.engine_newPayloadV2(Arg.Any<ExecutionPayload>())
            .Returns(ResultWrapper<PayloadStatusV1>.Success(status));

        byte[] body = version == 1 ? BuildMinimalV1NewPayloadRequest() : BuildMinimalV2NewPayloadRequest();
        DefaultHttpContext ctx = MakePostContext(path, body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.Response.ContentType.Should().Contain(OctetStream);
        await _engineModule.Received(version == 1 ? 1 : 0).engine_newPayloadV1(Arg.Any<ExecutionPayload>());
        await _engineModule.Received(version == 2 ? 1 : 0).engine_newPayloadV2(Arg.Any<ExecutionPayload>());
    }

    [TestCase(1, "/engine/v1/payloads/0x0102030405060708")]
    [TestCase(2, "/engine/v2/payloads/0x0102030405060708")]
    public async Task GetPayload_routes_to_correct_handler_with_no_store_header(int version, string path)
    {
        _engineModule.engine_getPayloadV1(Arg.Any<byte[]>())
            .Returns(ResultWrapper<ExecutionPayload?>.Success(SszTestData.MakeMinimalPayload()));
        _engineModule.engine_getPayloadV2(Arg.Any<byte[]>())
            .Returns(ResultWrapper<GetPayloadV2Result?>.Success(new GetPayloadV2Result(MakeMinimalBlock(), UInt256.One)));

        DefaultHttpContext ctx = MakeGetContext(path);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.Response.Headers["Cache-Control"].ToString().Should().Contain("no-store");
        await _engineModule.Received(version == 1 ? 1 : 0).engine_getPayloadV1(Arg.Any<byte[]>());
        await _engineModule.Received(version == 2 ? 1 : 0).engine_getPayloadV2(Arg.Any<byte[]>());
    }

    [TestCase("/engine/v1/forkchoice", 1)]
    [TestCase("/engine/v2/forkchoice", 2)]
    [TestCase("/engine/v3/forkchoice", 3)]
    [TestCase("/engine/v4/forkchoice", 4)]
    public async Task Forkchoice_calls_correct_engine_module_version(string path, int version)
    {
        ForkchoiceUpdatedV1Result fcuResult = new()
        {
            PayloadStatus = new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA }
        };
        _engineModule.engine_forkchoiceUpdatedV1(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>())
            .Returns(ResultWrapper<ForkchoiceUpdatedV1Result>.Success(fcuResult));
        _engineModule.engine_forkchoiceUpdatedV2(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>())
            .Returns(ResultWrapper<ForkchoiceUpdatedV1Result>.Success(fcuResult));
        _engineModule.engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>())
            .Returns(ResultWrapper<ForkchoiceUpdatedV1Result>.Success(fcuResult));
        _engineModule.engine_forkchoiceUpdatedV4(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>())
            .Returns(ResultWrapper<ForkchoiceUpdatedV1Result>.Success(fcuResult));

        byte[] body = BuildForkchoiceRequest();
        DefaultHttpContext ctx = MakePostContext(path, body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        int v1Calls = version == 1 ? 1 : 0;
        int v2Calls = version == 2 ? 1 : 0;
        int v3Calls = version == 3 ? 1 : 0;
        int v4Calls = version == 4 ? 1 : 0;
        await _engineModule.Received(v1Calls).engine_forkchoiceUpdatedV1(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>());
        await _engineModule.Received(v2Calls).engine_forkchoiceUpdatedV2(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>());
        await _engineModule.Received(v3Calls).engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>());
        await _engineModule.Received(v4Calls).engine_forkchoiceUpdatedV4(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>());
    }

    [Test]
    public async Task GetBlobsV1_returns_200_when_all_blobs_present()
    {
        byte[] blob = new byte[131072];
        byte[] proof = new byte[48];
        BlobAndProofV1 bap = new(blob, proof);
        _engineModule.engine_getBlobsV1(Arg.Any<byte[][]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success([bap]));

        byte[] body = BuildHashListRequest([TestItem.KeccakA.Bytes.ToArray()]);
        DefaultHttpContext ctx = MakePostContext("/engine/v1/blobs", body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await _engineModule.Received(1).engine_getBlobsV1(Arg.Any<byte[][]>());
        await _engineModule.DidNotReceive().engine_getBlobsV2(Arg.Any<byte[][]>());
        await _engineModule.DidNotReceive().engine_getBlobsV3(Arg.Any<byte[][]>());
    }

    [TestCase("/engine/v2/blobs", false)]
    [TestCase("/engine/v3/blobs", true)]
    public async Task GetBlobsV2V3_routes_to_correct_engine_method(string path, bool isV3)
    {
        _engineModule.engine_getBlobsV2(Arg.Any<byte[][]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(null));
        _engineModule.engine_getBlobsV3(Arg.Any<byte[][]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(null));

        DefaultHttpContext ctx = MakePostContext(path, BuildHashListRequest([]));

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        await _engineModule.Received(isV3 ? 0 : 1).engine_getBlobsV2(Arg.Any<byte[][]>());
        await _engineModule.Received(isV3 ? 1 : 0).engine_getBlobsV3(Arg.Any<byte[][]>());
    }

    [TestCase(1, "/engine/v1/payloads/bodies/by-hash")]
    [TestCase(2, "/engine/v2/payloads/bodies/by-hash")]
    public async Task GetPayloadBodiesByHash_routes_to_correct_engine_method(int version, string path)
    {
        _engineModule.engine_getPayloadBodiesByHashV1(Arg.Any<IReadOnlyList<Hash256>>())
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success(
                [new ExecutionPayloadBodyV1Result([], null)]));
        _engineModule.engine_getPayloadBodiesByHashV2(Arg.Any<IReadOnlyList<Hash256>>())
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Success(
                [new ExecutionPayloadBodyV2Result([], null, null)]));

        byte[] body = BuildPayloadBodiesByHashRequest([TestItem.KeccakA]);
        DefaultHttpContext ctx = MakePostContext(path, body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        _engineModule.Received(version == 1 ? 1 : 0).engine_getPayloadBodiesByHashV1(Arg.Any<IReadOnlyList<Hash256>>());
        await _engineModule.Received(version == 2 ? 1 : 0).engine_getPayloadBodiesByHashV2(Arg.Any<IReadOnlyList<Hash256>>());
    }

    [TestCase(1, "/engine/v1/payloads/bodies/by-range")]
    [TestCase(2, "/engine/v2/payloads/bodies/by-range")]
    public async Task GetPayloadBodiesByRange_routes_to_correct_engine_method_with_correct_args(int version, string path)
    {
        const long expectedStart = 7;
        const long expectedCount = 3;

        long v1Start = -1, v1Count = -1;
        long v2Start = -1, v2Count = -1;
        _engineModule
            .engine_getPayloadBodiesByRangeV1(Arg.Do<long>(s => v1Start = s), Arg.Do<long>(c => v1Count = c))
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success([]));
        _engineModule
            .engine_getPayloadBodiesByRangeV2(Arg.Do<long>(s => v2Start = s), Arg.Do<long>(c => v2Count = c))
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Success([]));

        byte[] body = BuildPayloadBodiesByRangeRequest((ulong)expectedStart, (ulong)expectedCount);
        DefaultHttpContext ctx = MakePostContext(path, body);

        await _middleware.InvokeAsync(ctx);

        await _engineModule.Received(version == 1 ? 1 : 0).engine_getPayloadBodiesByRangeV1(Arg.Any<long>(), Arg.Any<long>());
        await _engineModule.Received(version == 2 ? 1 : 0).engine_getPayloadBodiesByRangeV2(Arg.Any<long>(), Arg.Any<long>());

        long capturedStart = version == 1 ? v1Start : v2Start;
        long capturedCount = version == 1 ? v1Count : v2Count;
        capturedStart.Should().Be(expectedStart);
        capturedCount.Should().Be(expectedCount);
    }

    [Test]
    public async Task Capabilities_returns_intersection_of_supported_methods()
    {
        string[] returned = ["POST /engine/v5/payloads"];
        _engineModule.engine_exchangeCapabilities(Arg.Any<IEnumerable<string>>())
            .Returns(ResultWrapper<IReadOnlyList<string>>.Success(returned));

        byte[] body = BuildCapabilitiesRequest(["POST /engine/v5/payloads", "POST /engine/v4/forkchoice"]);
        DefaultHttpContext ctx = MakePostContext("/engine/v1/capabilities", body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.Response.ContentType.Should().Contain(OctetStream);
    }

    [Test]
    public async Task ClientVersion_returns_non_empty_response()
    {
        ClientVersionV1[] response = [new ClientVersionV1()];
        _engineModule.engine_getClientVersionV1(default)
            .ReturnsForAnyArgs(ResultWrapper<ClientVersionV1[]>.Success(response));

        byte[] body = BuildClientVersionRequest();
        DefaultHttpContext ctx = MakePostContext("/engine/v1/client/version", body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ctx.Response.ContentType.Should().Contain(OctetStream);
        ResponseBytes(ctx).Length.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task TransitionConfiguration_calls_engine_module_and_returns_200()
    {
        TransitionConfigurationV1 tc = new()
        {
            TerminalTotalDifficulty = UInt256.One,
            TerminalBlockHash = TestItem.KeccakA,
            TerminalBlockNumber = 1
        };
        _engineModule.engine_exchangeTransitionConfigurationV1(Arg.Any<TransitionConfigurationV1>())
            .Returns(ResultWrapper<TransitionConfigurationV1>.Success(tc));

        byte[] body = BuildTransitionConfigRequest(tc);
        DefaultHttpContext ctx = MakePostContext("/engine/v1/transition-configuration", body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        _engineModule.Received(1).engine_exchangeTransitionConfigurationV1(Arg.Any<TransitionConfigurationV1>());
    }

    [Test]
    public async Task Authentication_failure_returns_401_and_does_not_call_engine_module()
    {
        _auth.Authenticate(Arg.Any<string>()).Returns(false);

        byte[] body = BuildMinimalV1NewPayloadRequest();
        DefaultHttpContext ctx = MakePostContext("/engine/v1/payloads", body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    [Test]
    public async Task Oversized_body_returns_413_without_calling_engine_module()
    {
        DefaultHttpContext ctx = MakePostContext("/engine/v1/payloads", []);
        ctx.Request.ContentLength = SszMiddleware.MaxBodySize + 1;
        ctx.Request.Body = new MemoryStream(new byte[1]);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    [Test]
    public async Task Unknown_engine_path_returns_404()
    {
        bool nextInvoked = false;
        SszMiddleware mw = BuildMiddleware(_ => { nextInvoked = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = MakePostContext("/engine/v1/unknown-resource", []);

        await mw.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        nextInvoked.Should().BeFalse("SSZ middleware should reply 404 itself, not delegate to JSON-RPC");
    }

    [Test]
    public async Task Post_payloads_with_unknown_extra_returns_404_not_500()
    {
        DefaultHttpContext ctx = MakePostContext("/engine/v1/payloads/foo/bar", []);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    [Test]
    public async Task Path_with_consecutive_slashes_returns_404()
    {
        // TryRoute must reject runs of '/' so that //abc does not reach
        // the payload-id parser and produce a confusing parse error.
        DefaultHttpContext ctx = MakePostContext("/engine/v1/payloads//abc", []);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    [Test]
    public async Task Malformed_ssz_body_returns_400_without_propagating_exception()
    {
        byte[] garbage = new byte[64];
        new Random(42).NextBytes(garbage);

        DefaultHttpContext ctx = MakePostContext("/engine/v1/payloads", garbage);

        Func<Task> act = () => _middleware.InvokeAsync(ctx);

        await act.Should().NotThrowAsync();
        // Per execution-apis #764: malformed SSZ encoding maps to 400 Bad Request.
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task GetBlobsV1_null_result_data_returns_204_no_content()
    {
        _engineModule.engine_getBlobsV1(Arg.Any<byte[][]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success(null!));

        byte[] body = BuildHashListRequest([TestItem.KeccakA.Bytes.ToArray()]);
        DefaultHttpContext ctx = MakePostContext("/engine/v1/blobs", body);

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        ResponseBytes(ctx).Should().BeEmpty("204 responses must have no body");
    }

    [Test]
    public async Task Unknown_version_for_versioned_endpoint_returns_404()
    {
        DefaultHttpContext ctx = MakeGetContext("/engine/v99/payloads/0x0102030405060708");

        await _middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    private static Block MakeMinimalBlock()
    {
        BlockHeader header = new(
            parentHash: TestItem.KeccakA,
            unclesHash: Keccak.OfAnEmptySequenceRlp,
            beneficiary: TestItem.AddressA,
            difficulty: 0,
            number: 1,
            gasLimit: 1_000_000,
            timestamp: 1_700_000_000,
            extraData: [])
        {
            StateRoot = TestItem.KeccakB,
            ReceiptsRoot = TestItem.KeccakC,
            Bloom = Bloom.Empty,
            MixHash = TestItem.KeccakD,
            GasUsed = 0,
            BaseFeePerGas = 1,
            Hash = TestItem.KeccakE
        };
        return new Block(header);
    }

    private static byte[] BuildMinimalV1NewPayloadRequest() =>
        NewPayloadV1RequestWire.Encode(new NewPayloadV1RequestWire
        {
            ExecutionPayload = new SszExecutionPayloadV1(SszTestData.MakeMinimalPayload())
        });

    private static byte[] BuildMinimalV2NewPayloadRequest() =>
        NewPayloadV2RequestWire.Encode(new NewPayloadV2RequestWire
        {
            ExecutionPayload = new SszExecutionPayload(SszTestData.MakeMinimalPayload())
        });

    private static byte[] BuildForkchoiceRequest()
    {
        byte[] body = new byte[100];
        Buffer.BlockCopy(TestItem.KeccakA.Bytes.ToArray(), 0, body, 0, 32);
        Buffer.BlockCopy(TestItem.KeccakB.Bytes.ToArray(), 0, body, 32, 32);
        Buffer.BlockCopy(Keccak.Zero.Bytes.ToArray(), 0, body, 64, 32);
        BitConverter.TryWriteBytes(body.AsSpan(96, 4), (uint)100);
        return body;
    }

    private static byte[] BuildHashListRequest(byte[][] hashes)
    {
        byte[] result = new byte[4 + hashes.Length * 32];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), (uint)4);
        for (int i = 0; i < hashes.Length; i++)
            Buffer.BlockCopy(hashes[i], 0, result, 4 + i * 32, 32);
        return result;
    }

    private static byte[] BuildPayloadBodiesByHashRequest(Hash256[] hashes) =>
        BuildHashListRequest(Array.ConvertAll(hashes, h => h.Bytes.ToArray()));

    private static byte[] BuildPayloadBodiesByRangeRequest(ulong start, ulong count)
    {
        byte[] result = new byte[16];
        BitConverter.TryWriteBytes(result.AsSpan(0, 8), start);
        BitConverter.TryWriteBytes(result.AsSpan(8, 8), count);
        return result;
    }

    private static byte[] BuildCapabilitiesRequest(string[] capabilities) =>
        ToBytes(SszCodec.EncodeCapabilitiesResponse(capabilities));

    private static byte[] BuildClientVersionRequest()
    {
        byte[] clientVersion = new byte[16];
        uint offset = 16;
        BitConverter.TryWriteBytes(clientVersion.AsSpan(0, 4), offset);
        BitConverter.TryWriteBytes(clientVersion.AsSpan(4, 4), offset);
        BitConverter.TryWriteBytes(clientVersion.AsSpan(8, 4), offset);

        byte[] request = new byte[4 + clientVersion.Length];
        BitConverter.TryWriteBytes(request.AsSpan(0, 4), (uint)4);
        Buffer.BlockCopy(clientVersion, 0, request, 4, clientVersion.Length);
        return request;
    }

    private static byte[] BuildTransitionConfigRequest(TransitionConfigurationV1 tc) =>
        ToBytes(SszCodec.EncodeTransitionConfigurationResponse(tc));
}
