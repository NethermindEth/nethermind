// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Authentication;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Merge.Plugin.SszRest.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using System.Linq;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.SszRest;

[TestFixture]
public class SszMiddlewareTests
{
    private IEngineRpcModule _engineModule = null!;
    private ISpecProvider _specProvider = null!;
    private Nethermind.Blockchain.Find.IBlockFinder _blockFinder = null!;

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
        _specProvider = Substitute.For<ISpecProvider>();
        _blockFinder = Substitute.For<Nethermind.Blockchain.Find.IBlockFinder>();

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

            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV1, ForkchoiceUpdatedV1RequestWire>(_engineModule, _specProvider),
            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV2, ForkchoiceUpdatedV2RequestWire>(_engineModule, _specProvider),
            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV3, ForkchoiceUpdatedV3RequestWire>(_engineModule, _specProvider),
            new ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV4, ForkchoiceUpdatedRequestWire>(_engineModule, _specProvider),

            new GetPayloadSszHandler<GetPayloadDescriptorV1, GetPayloadV2Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV2, GetPayloadV2Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV3, GetPayloadV3Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV4, GetPayloadV4Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV5, GetPayloadV5Result>(_engineModule),
            new GetPayloadSszHandler<GetPayloadDescriptorV6, GetPayloadV6Result>(_engineModule),

            new GetBlobsV1SszHandler(_engineModule),
            new GetBlobsV2SszHandler<GetBlobsDescriptorV2>(_engineModule),
            new GetBlobsV2SszHandler<GetBlobsDescriptorV3>(_engineModule),
            new GetBlobsV4SszHandler(_engineModule),

            new GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV1, ExecutionPayloadBodyV1Result>(_engineModule, _blockFinder, _specProvider),
            new GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV2, ExecutionPayloadBodyV2Result>(_engineModule, _blockFinder, _specProvider),

            new GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV1, ExecutionPayloadBodyV1Result>(_engineModule, _blockFinder, _specProvider),
            new GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV2, ExecutionPayloadBodyV2Result>(_engineModule, _blockFinder, _specProvider),

            new ClientVersionSszHandler(_engineModule, LimboLogs.Instance),
            new CapabilitiesSszHandler(_specProvider),

            new NewPayloadWithWitnessSszHandler<NewPayloadWithWitnessDescriptorV1, NewPayloadV5RequestWire>(_engineModule),
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

    private static DefaultHttpContext MakePostContext(string path, byte[] body, int port = AuthenticatedPort, string? fork = null)
    {
        DefaultHttpContext ctx = MakeBaseContext("POST", path, port);
        ctx.Request.ContentType = OctetStream;
        ctx.Request.ContentLength = body.Length;
        ctx.Request.Body = new MemoryStream(body);
        if (fork is not null) ctx.Request.Headers[SszMiddleware.ForkHeaderName] = fork;
        return ctx;
    }

    private static DefaultHttpContext MakeGetContext(string path, int port = AuthenticatedPort, string? fork = null)
    {
        DefaultHttpContext ctx = MakeBaseContext("GET", path, port);
        ctx.Request.Headers.Accept = OctetStream;
        ctx.Request.Body = Stream.Null;
        if (fork is not null) ctx.Request.Headers[SszMiddleware.ForkHeaderName] = fork;
        return ctx;
    }

    private static byte[] ResponseBytes(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using MemoryStream ms = new();
        ctx.Response.Body.CopyTo(ms);
        return ms.ToArray();
    }

    private static readonly TestCaseData[] NewPayloadRoutingCases =
    [
        new TestCaseData(EngineApiVersions.NewPayload.V1, "paris").SetName("NewPayload_paris_routes_to_V1"),
        new TestCaseData(EngineApiVersions.NewPayload.V2, "shanghai").SetName("NewPayload_shanghai_routes_to_V2"),
    ];

    [TestCaseSource(nameof(NewPayloadRoutingCases))]
    public async Task NewPayload_routes_to_correct_engine_module_version(int version, string fork)
    {
        PayloadStatusV1 status = new() { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA };
        _engineModule.engine_newPayloadV1(Arg.Any<ExecutionPayload>())
            .Returns(ResultWrapper<PayloadStatusV1>.Success(status));
        _engineModule.engine_newPayloadV2(Arg.Any<ExecutionPayload>())
            .Returns(ResultWrapper<PayloadStatusV1>.Success(status));

        byte[] body = version == 1 ? BuildMinimalV1NewPayloadRequest() : BuildMinimalV2NewPayloadRequest();
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", body, fork: fork);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.ContentType, Does.Contain(OctetStream));
        await _engineModule.Received(version == 1 ? 1 : 0).engine_newPayloadV1(Arg.Any<ExecutionPayload>());
        await _engineModule.Received(version == 2 ? 1 : 0).engine_newPayloadV2(Arg.Any<ExecutionPayload>());
    }

    private static readonly TestCaseData[] GetPayloadRoutingCases =
    [
        new TestCaseData(EngineApiVersions.GetPayload.V1, "paris").SetName("GetPayload_paris_routes_to_V1"),
        new TestCaseData(EngineApiVersions.GetPayload.V2, "shanghai").SetName("GetPayload_shanghai_routes_to_V2"),
    ];

    [TestCaseSource(nameof(GetPayloadRoutingCases))]
    public async Task GetPayload_routes_to_correct_handler_with_no_store_header(int version, string fork)
    {
        // BuiltPayloadParis needs block_value, sourced from engine_getPayloadV2.
        _engineModule.engine_getPayloadV2(Arg.Any<byte[]>())
            .Returns(ResultWrapper<GetPayloadV2Result?>.Success(new GetPayloadV2Result(MakeMinimalBlock(), UInt256.One)));

        DefaultHttpContext ctx = MakeGetContext("/engine/v2/payloads/0x0102030405060708", fork: fork);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.Headers["Cache-Control"].ToString(), Does.Contain("no-store"));
        await _engineModule.DidNotReceive().engine_getPayloadV1(Arg.Any<byte[]>());
        await _engineModule.Received(1).engine_getPayloadV2(Arg.Any<byte[]>());
    }

    private static readonly TestCaseData[] ForkchoiceRoutingCases =
    [
        new TestCaseData("paris", EngineApiVersions.Fcu.V1).SetName("Forkchoice_paris_routes_to_V1"),
        new TestCaseData("shanghai", EngineApiVersions.Fcu.V2).SetName("Forkchoice_shanghai_routes_to_V2"),
        new TestCaseData("cancun", EngineApiVersions.Fcu.V3).SetName("Forkchoice_cancun_routes_to_V3"),
        new TestCaseData("amsterdam", EngineApiVersions.Fcu.V4).SetName("Forkchoice_amsterdam_routes_to_V4"),
    ];

    [TestCaseSource(nameof(ForkchoiceRoutingCases))]
    public async Task Forkchoice_calls_correct_engine_module_version(string fork, int version)
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

        byte[] body = version == 4 ? BuildForkchoiceV4Request() : BuildForkchoiceRequest();
        DefaultHttpContext ctx = MakePostContext("/engine/v2/forkchoice", body, fork: fork);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));

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
        DefaultHttpContext ctx = MakePostContext("/engine/v2/blobs/v1", body);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        await _engineModule.Received(1).engine_getBlobsV1(Arg.Any<byte[][]>());
        await _engineModule.DidNotReceive().engine_getBlobsV2(Arg.Any<byte[][]>());
        await _engineModule.DidNotReceive().engine_getBlobsV3(Arg.Any<byte[][]>());
    }

    [TestCase("/engine/v2/blobs/v2", false)]
    [TestCase("/engine/v2/blobs/v3", true)]
    public async Task GetBlobsV2V3_routes_to_correct_engine_method(string path, bool isV3)
    {
        _engineModule.engine_getBlobsV2(Arg.Any<byte[][]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(null));
        _engineModule.engine_getBlobsV3(Arg.Any<byte[][]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(null));

        DefaultHttpContext ctx = MakePostContext(path, BuildHashListRequest([]));

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
        await _engineModule.Received(isV3 ? 0 : 1).engine_getBlobsV2(Arg.Any<byte[][]>());
        await _engineModule.Received(isV3 ? 1 : 0).engine_getBlobsV3(Arg.Any<byte[][]>());
    }

    [Test]
    public async Task GetBlobsV4_routes_to_engine_getBlobsV4()
    {
        _engineModule.engine_getBlobsV4(Arg.Any<byte[][]>(), Arg.Any<byte[]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(null));

        System.Collections.BitArray indicesBitarray = new(128);
        indicesBitarray.Set(0, true);
        GetBlobsV4RequestWire request = new()
        {
            BlobVersionedHashes = [],
            IndicesBitarray = indicesBitarray
        };
        byte[] body = GetBlobsV4RequestWire.Encode(request);
        DefaultHttpContext ctx = MakePostContext("/engine/v2/blobs/v4", body);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
        await _engineModule.Received(1).engine_getBlobsV4(Arg.Any<byte[][]>(), Arg.Is<byte[]>(static b => b.Length == 16 && b[0] == 1));
    }

    private static readonly TestCaseData[] BodiesByHashRoutingCases =
    [
        new TestCaseData(EngineApiVersions.PayloadBodiesByHash.V1, "shanghai").SetName("BodiesByHash_shanghai_routes_to_V1"),
        new TestCaseData(EngineApiVersions.PayloadBodiesByHash.V2, "amsterdam").SetName("BodiesByHash_amsterdam_routes_to_V2"),
    ];

    [TestCaseSource(nameof(BodiesByHashRoutingCases))]
    public async Task GetPayloadBodiesByHash_routes_to_correct_engine_method(int version, string fork)
    {
        _engineModule.engine_getPayloadBodiesByHashV1(Arg.Any<IReadOnlyList<Hash256>>())
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success(
                [new ExecutionPayloadBodyV1Result([], null)]));
        _engineModule.engine_getPayloadBodiesByHashV2(Arg.Any<IReadOnlyList<Hash256>>())
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Success(
                [new ExecutionPayloadBodyV2Result([], null, null)]));

        byte[] body = BuildPayloadBodiesByHashRequest([TestItem.KeccakA]);
        DefaultHttpContext ctx = MakePostContext("/engine/v2/bodies/hash", body, fork: fork);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        _engineModule.Received(version == 1 ? 1 : 0).engine_getPayloadBodiesByHashV1(Arg.Any<IReadOnlyList<Hash256>>());
        await _engineModule.Received(version == 2 ? 1 : 0).engine_getPayloadBodiesByHashV2(Arg.Any<IReadOnlyList<Hash256>>());
    }

    [Test]
    public async Task GetPayloadBodiesByHash_marks_out_of_fork_blocks_unavailable()
    {
        Hash256 inFork = TestItem.KeccakA;
        Hash256 outOfFork = TestItem.KeccakB;
        _engineModule.engine_getPayloadBodiesByHashV1(Arg.Any<IReadOnlyList<Hash256>>())
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success(
                [new ExecutionPayloadBodyV1Result([], null), new ExecutionPayloadBodyV1Result([], null)]));

        BlockHeader shanghaiHeader = Build.A.BlockHeader.WithNumber(10).WithTimestamp(1_000UL).TestObject;
        BlockHeader cancunHeader = Build.A.BlockHeader.WithNumber(20).WithTimestamp(2_000UL).TestObject;
        _blockFinder.FindHeader(inFork).Returns(shanghaiHeader);
        _blockFinder.FindHeader(outOfFork).Returns(cancunHeader);
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == 1_000UL)).Returns(Shanghai.Instance);
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == 2_000UL)).Returns(Cancun.Instance);

        byte[] body = BuildPayloadBodiesByHashRequest([inFork, outOfFork]);
        DefaultHttpContext ctx = MakePostContext("/engine/v2/bodies/hash", body, fork: "shanghai");

        await _middleware.InvokeAsync(ctx);

        byte[] resp = ResponseBytes(ctx);
        PayloadBodiesV1ResponseWire.Decode(new ReadOnlySequence<byte>(resp), out PayloadBodiesV1ResponseWire decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(decoded.Entries, Has.Length.EqualTo(2));
            Assert.That(decoded.Entries![0].Available, Is.True, "Shanghai block at /shanghai/bodies must stay available");
            Assert.That(decoded.Entries[1].Available, Is.False, "Cancun block at /shanghai/bodies must surface as unavailable");
        }
    }

    private static readonly TestCaseData[] BodiesByRangeRoutingCases =
    [
        new TestCaseData(EngineApiVersions.PayloadBodiesByRange.V1, "shanghai").SetName("BodiesByRange_shanghai_routes_to_V1"),
        new TestCaseData(EngineApiVersions.PayloadBodiesByRange.V2, "amsterdam").SetName("BodiesByRange_amsterdam_routes_to_V2"),
    ];

    [TestCaseSource(nameof(BodiesByRangeRoutingCases))]
    public async Task GetPayloadBodiesByRange_routes_to_correct_engine_method_with_correct_args(int version, string fork)
    {
        const ulong expectedStart = 7;
        const ulong expectedCount = 3;

        ulong v1Start = ulong.MaxValue, v1Count = ulong.MaxValue;
        ulong v2Start = ulong.MaxValue, v2Count = ulong.MaxValue;

        _engineModule
            .engine_getPayloadBodiesByRangeV1(Arg.Do<ulong>(s => v1Start = s), Arg.Do<ulong>(c => v1Count = c))
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success([]));
        _engineModule
            .engine_getPayloadBodiesByRangeV2(Arg.Do<ulong>(s => v2Start = s), Arg.Do<ulong>(c => v2Count = c))
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Success([]));

        // The range endpoint is now GET with from/count as query parameters.
        DefaultHttpContext ctx = MakeGetContext("/engine/v2/bodies", fork: fork);
        ctx.Request.QueryString = new QueryString($"?from={expectedStart}&count={expectedCount}");

        await _middleware.InvokeAsync(ctx);

        await _engineModule.Received(version == 1 ? 1 : 0).engine_getPayloadBodiesByRangeV1(Arg.Any<ulong>(), Arg.Any<ulong>());
        await _engineModule.Received(version == 2 ? 1 : 0).engine_getPayloadBodiesByRangeV2(Arg.Any<ulong>(), Arg.Any<ulong>());

        ulong capturedStart = version == 1 ? v1Start : v2Start;
        ulong capturedCount = version == 1 ? v1Count : v2Count;
        Assert.That(capturedStart, Is.EqualTo(expectedStart));
        Assert.That(capturedCount, Is.EqualTo(expectedCount));
    }

    [Test]
    public async Task Capabilities_returns_intersection_of_supported_methods()
    {
        _specProvider.TransitionActivations.Returns([]);

        DefaultHttpContext ctx = MakeGetContext("/engine/v2/capabilities");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.ContentType, Does.Contain("application/json"));
    }

    [Test]
    public async Task Capabilities_supported_forks_are_gated_by_spec_provider()
    {
        // Two distinct spec objects for Shanghai and Cancun, identified purely by reference
        // equality — no Name property is involved.
        IReleaseSpec shanghaiSpec = Substitute.For<IReleaseSpec>();
        IReleaseSpec cancunSpec = Substitute.For<IReleaseSpec>();

        ForkActivation[] transitions =
        [
            ForkActivation.TimestampOnly(1_000UL),
            ForkActivation.TimestampOnly(2_000UL),
        ];
        _specProvider.TransitionActivations.Returns(transitions);
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == 1_000UL)).Returns(shanghaiSpec);
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == 2_000UL)).Returns(cancunSpec);

        // Rebuild middleware so it picks up the now-configured spec provider.
        SszMiddleware mw = BuildMiddleware();
        DefaultHttpContext ctx = MakeGetContext("/engine/v2/capabilities");
        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));

        Assert.That(body, Does.Contain("\"paris\""));
        Assert.That(body, Does.Contain("\"shanghai\""));
        Assert.That(body, Does.Contain("\"cancun\""));

        Assert.That(body, Does.Not.Contain("\"prague\""));
        Assert.That(body, Does.Not.Contain("\"osaka\""));
        Assert.That(body, Does.Not.Contain("\"amsterdam\""));
    }

    [Test]
    public async Task ClientVersion_returns_non_empty_response()
    {
        ClientVersionV1[] response = [new ClientVersionV1()];
        _engineModule.engine_getClientVersionV1(default)
            .ReturnsForAnyArgs(ResultWrapper<ClientVersionV1[]>.Success(response));

        DefaultHttpContext ctx = MakeGetContext("/engine/v2/identity");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.ContentType, Does.Contain("application/json"));
        Assert.That(ResponseBytes(ctx).Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task Authentication_failure_returns_401_and_does_not_call_engine_module()
    {
        _auth.Authenticate(Arg.Any<string>()).Returns(false);

        byte[] body = BuildMinimalV1NewPayloadRequest();
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", body, fork: "paris");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    [Test]
    public async Task Oversized_body_returns_413_without_calling_engine_module()
    {
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", [], fork: "paris");
        ctx.Request.ContentLength = SszMiddleware.MaxBodySize + 1;
        ctx.Request.Body = new MemoryStream(new byte[1]);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    [Test]
    public async Task Unknown_engine_path_returns_404_without_delegating_to_next()
    {
        bool nextInvoked = false;
        SszMiddleware mw = BuildMiddleware(_ => { nextInvoked = true; return Task.CompletedTask; });
        DefaultHttpContext ctx = MakePostContext("/engine/v2/unknown-resource", [], fork: "paris");

        await mw.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(nextInvoked, Is.False, "SSZ middleware should reply 404 itself, not delegate to JSON-RPC");
    }

    // Each case is a different routing rejection that must NOT reach the engine module:
    // extra segments on a non-AcceptsPathExtra handler, runs of '/' inside the path.
    [TestCase("/engine/v2/payloads/foo/bar", TestName = "Extra_segments_on_non_path_handler_404")]
    [TestCase("/engine/v2/payloads//abc", TestName = "Consecutive_slashes_404")]
    public async Task POST_with_malformed_path_returns_404(string path)
    {
        DefaultHttpContext ctx = MakePostContext(path, [], fork: "paris");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    private static readonly TestCaseData[] MalformedSszBodyCases =
    [
        new TestCaseData("/engine/v2/payloads", "paris").SetName("Malformed_ssz_payloads_returns_400"),
        new TestCaseData(WitnessPath, WitnessFork).SetName("Malformed_ssz_payloads_witness_returns_400"),
    ];

    [TestCaseSource(nameof(MalformedSszBodyCases))]
    public async Task Malformed_ssz_body_returns_400_without_propagating_exception(string path, string fork)
    {
        byte[] garbage = new byte[64];
        new Random(42).NextBytes(garbage);

        DefaultHttpContext ctx = MakePostContext(path, garbage, fork: fork);

        Func<Task> act = () => _middleware.InvokeAsync(ctx);

        Assert.That(async () => await act(), Throws.Nothing);
        // Per execution-apis #793: malformed SSZ encoding maps to 400 Bad Request.
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task Truncated_body_with_overstated_content_length_returns_400()
    {
        byte[] body = new byte[16];
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", body, fork: "paris");
        // Declare more bytes than the stream will deliver — ReadAtLeastAsync returns short.
        ctx.Request.ContentLength = body.Length + 64;

        Func<Task> act = () => _middleware.InvokeAsync(ctx);

        Assert.That(async () => await act(), Throws.Nothing);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task GetBlobsV1_null_result_data_returns_204_no_content()
    {
        _engineModule.engine_getBlobsV1(Arg.Any<byte[][]>())
            .Returns(ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success(null!));

        byte[] body = BuildHashListRequest([TestItem.KeccakA.Bytes.ToArray()]);
        DefaultHttpContext ctx = MakePostContext("/engine/v2/blobs/v1", body);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
        Assert.That(ResponseBytes(ctx), Is.Empty, "204 responses must have no body");
    }

    [Test]
    public async Task Unknown_version_for_versioned_endpoint_returns_404()
    {
        DefaultHttpContext ctx = MakeGetContext("/engine/v99/payloads/0x0102030405060708");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [Test]
    public async Task Server_error_skips_WriteError_when_request_already_aborted()
    {
        // Engine module throws — the middleware's outer catch normally writes a 500 error
        // and calls ctx.Response.WriteAsync. If the inner code already aborted the request
        // (encode failure path: WriteSszAsync calls ctx.Abort), the WriteAsync would throw
        // a secondary OperationCanceledException, producing a duplicate exception log.
        // The fix: the outer catch checks RequestAborted and skips the error write.
        _engineModule.engine_newPayloadV1(Arg.Any<ExecutionPayload>())
            .Returns<Task<ResultWrapper<PayloadStatusV1>>>(_ => throw new InvalidOperationException("simulated server error"));

        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", BuildMinimalV1NewPayloadRequest(), fork: "paris");

        // Simulate the encode-failure → ctx.Abort() effect by pre-cancelling RequestAborted.
        // DefaultHttpContext's Abort() is a no-op without a real lifetime feature, so we
        // signal the cancellation directly to drive the catch's IsCancellationRequested branch.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        ctx.RequestAborted = cts.Token;

        await _middleware.InvokeAsync(ctx);

        // With the fix: the outer catch sees RequestAborted is cancelled and does NOT
        // call WriteErrorAsync. StatusCode remains the DefaultHttpContext default (200);
        // crucially it must NOT be 500.
        Assert.That(ctx.Response.StatusCode, Is.Not.EqualTo(StatusCodes.Status500InternalServerError));
        Assert.That(ResponseBytes(ctx), Is.Empty, "aborted request must not have an error body written");
    }

    [Test]
    public async Task Encoder_returning_zero_length_for_non_null_data_yields_204()
    {
        // Build a middleware whose only handler succeeds with a non-null result through
        // an encoder that produces no bytes. WriteSszAsync should treat empty-success as
        // 204 No Content rather than 200 OK with Content-Length: 0.
        ZeroLengthEncodeHandler handler = new();
        SszMiddleware middleware = new(
            _ => Task.CompletedTask, _urlCollection, _auth, [handler], _processExitSource, LimboLogs.Instance);

        DefaultHttpContext ctx = MakePostContext($"/engine/v2/{ZeroLengthEncodeHandler.ResourceName}", [], fork: "paris");

        await middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
        Assert.That(ResponseBytes(ctx), Is.Empty);
    }

    private sealed class ZeroLengthEncodeHandler : SszEndpointHandlerBase
    {
        // Route under a known fork-scoped resource so TryRoute can map it to a version.
        public const string ResourceName = "payloads";
        public override string HttpMethod => "POST";
        public override string Resource => ResourceName;
        public override int? Version => 1;

        public override Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body) =>
            WriteSszResultAsync(ctx, ResultWrapper<int>.Success(42), static (_, _) => 0);
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
            ExecutionPayload = new SszExecutionPayloadV2(SszTestData.MakeMinimalPayload())
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

    // V4 wire adds CustodyColumns (a second variable list), so the fixed section is
    // 96 (ForkchoiceState) + 4 (PayloadAttributes offset) + 4 (CustodyColumns offset) = 104 bytes.
    private static byte[] BuildForkchoiceV4Request()
    {
        byte[] body = new byte[104];
        Buffer.BlockCopy(TestItem.KeccakA.Bytes.ToArray(), 0, body, 0, 32);
        Buffer.BlockCopy(TestItem.KeccakB.Bytes.ToArray(), 0, body, 32, 32);
        Buffer.BlockCopy(Keccak.Zero.Bytes.ToArray(), 0, body, 64, 32);
        // Both lists are empty; both offsets point just past the fixed section.
        BitConverter.TryWriteBytes(body.AsSpan(96, 4), (uint)104);
        BitConverter.TryWriteBytes(body.AsSpan(100, 4), (uint)104);
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

    [Test]
    public async Task ClientVersion_reads_X_Engine_Client_Version_header()
    {
        ClientVersionV1 clVersion = new()
        {
            Code = "NB",
            Name = "Nimbus",
            Version = "v26.5.0",
            Commit = "0df2a74"
        };

        string jsonHeader = System.Text.Json.JsonSerializer.Serialize(clVersion);

        ClientVersionV1[] response = [new(), clVersion];
        _engineModule.engine_getClientVersionV1(default)
            .ReturnsForAnyArgs(ResultWrapper<ClientVersionV1[]>.Success(response));

        DefaultHttpContext ctx = MakeGetContext("/engine/v2/identity");
        ctx.Request.Headers["X-Engine-Client-Version"] = jsonHeader;

        await _middleware.InvokeAsync(ctx);

        ICall[] calls = _engineModule.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IEngineRpcModule.engine_getClientVersionV1))
            .ToArray();
        Assert.That(calls.Length, Is.EqualTo(1));
        ClientVersionV1 arg = (ClientVersionV1)calls[0].GetArguments()[0]!;
        Assert.That(arg.Code, Is.EqualTo("NB"));

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.ContentType, Does.Contain("application/json"));

        byte[] bytes = ResponseBytes(ctx);
        string responseStr = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(bytes).ToString();
        Assert.That(responseStr, Does.Contain("Nimbus"));
    }

    [Test]
    public async Task Forkchoice_unsupported_fork_returns_400()
    {
        IReleaseSpec shanghaiSpec = Substitute.For<IReleaseSpec>();
        IReleaseSpec cancunSpec = Substitute.For<IReleaseSpec>();

        const ulong shanghaiTs = 1_000UL;
        const ulong cancunTs = 2_000UL;
        const ulong payloadTs = 1_500UL;

        ForkActivation[] transitions =
        [
            ForkActivation.TimestampOnly(shanghaiTs),
            ForkActivation.TimestampOnly(cancunTs),
        ];
        _specProvider.TransitionActivations.Returns(transitions);
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == shanghaiTs)).Returns(shanghaiSpec);
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == cancunTs)).Returns(cancunSpec);
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == payloadTs)).Returns(shanghaiSpec);

        ForkchoiceUpdatedV3RequestWire request = new()
        {
            ForkchoiceState = new ForkchoiceStateWire
            {
                HeadBlockHash = TestItem.KeccakA,
                SafeBlockHash = TestItem.KeccakB,
                FinalizedBlockHash = Keccak.Zero
            },
            PayloadAttributes = [new PayloadAttributesV3Wire
            {
                Timestamp = payloadTs,
                SuggestedFeeRecipient = TestItem.AddressA,
                PrevRandao = Keccak.Zero,
                Withdrawals = [],
                ParentBeaconBlockRoot = Keccak.Zero
            }]
        };
        byte[] body = ForkchoiceUpdatedV3RequestWire.Encode(request);

        DefaultHttpContext ctx = MakePostContext("/engine/v2/forkchoice", body, fork: "cancun");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        string respBody = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        Assert.That(respBody, Does.Contain("unsupported-fork"));
    }

    [Test]
    public async Task Forkchoice_stale_fork_url_without_attributes_is_allowed()
    {
        ForkchoiceUpdatedV1Result fcuResult = new()
        {
            PayloadStatus = new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA }
        };
        _engineModule.engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>())
            .Returns(ResultWrapper<ForkchoiceUpdatedV1Result>.Success(fcuResult));

        ForkchoiceUpdatedV3RequestWire request = new()
        {
            ForkchoiceState = new ForkchoiceStateWire
            {
                HeadBlockHash = TestItem.KeccakA,
                SafeBlockHash = TestItem.KeccakB,
                FinalizedBlockHash = Keccak.Zero
            },
            PayloadAttributes = []
        };
        byte[] body = ForkchoiceUpdatedV3RequestWire.Encode(request);

        DefaultHttpContext ctx = MakePostContext("/engine/v2/forkchoice", body, fork: "cancun");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        await _engineModule.Received(1).engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>());
        // ISpecProvider must NOT have been consulted — no timestamp to validate.
        _specProvider.DidNotReceive().GetSpec(Arg.Any<ForkActivation>());
    }

    [Test]
    public async Task Forkchoice_payload_in_BPO_fork_routes_to_parent_url()
    {
        ForkchoiceUpdatedV1Result fcuResult = new()
        {
            PayloadStatus = new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA }
        };
        _engineModule.engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>())
            .Returns(ResultWrapper<ForkchoiceUpdatedV1Result>.Success(fcuResult));

        const ulong payloadTs = 1_000UL;
        _specProvider.GetSpec(Arg.Is<ForkActivation>(fa => fa.Timestamp == payloadTs))
            .Returns(BPO1.Instance);

        ForkchoiceUpdatedV3RequestWire request = new()
        {
            ForkchoiceState = new ForkchoiceStateWire
            {
                HeadBlockHash = TestItem.KeccakA,
                SafeBlockHash = TestItem.KeccakB,
                FinalizedBlockHash = Keccak.Zero
            },
            PayloadAttributes = [new PayloadAttributesV3Wire
            {
                Timestamp = payloadTs,
                SuggestedFeeRecipient = TestItem.AddressA,
                PrevRandao = Keccak.Zero,
                Withdrawals = [],
                ParentBeaconBlockRoot = Keccak.Zero
            }]
        };
        byte[] body = ForkchoiceUpdatedV3RequestWire.Encode(request);

        DefaultHttpContext ctx = MakePostContext("/engine/v2/forkchoice", body, fork: "osaka");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        await _engineModule.Received(1).engine_forkchoiceUpdatedV3(Arg.Any<ForkchoiceStateV1>(), Arg.Any<PayloadAttributes?>());
    }

    [TestCase("application/json")]
    [TestCase("*/*")]
    [TestCase("text/html, application/json;q=0.9, */*;q=0.8")]
    public async Task Capabilities_returns_200_json_regardless_of_Accept_header(string accept)
    {
        DefaultHttpContext ctx = MakeBaseContext("GET", "/engine/v2/capabilities", AuthenticatedPort);
        ctx.Request.Headers.Accept = accept;
        ctx.Request.Body = Stream.Null;

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.ContentType, Does.Contain("application/json"));
        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        Assert.That(body, Does.Contain("supported_forks"));
    }

    [TestCase("application/json")]
    [TestCase("*/*")]
    public async Task Identity_returns_200_json_regardless_of_Accept_header(string accept)
    {
        ClientVersionV1[] response = [new ClientVersionV1()];
        _engineModule.engine_getClientVersionV1(default)
            .ReturnsForAnyArgs(ResultWrapper<ClientVersionV1[]>.Success(response));

        DefaultHttpContext ctx = MakeBaseContext("GET", "/engine/v2/identity", AuthenticatedPort);
        ctx.Request.Headers.Accept = accept;
        ctx.Request.Body = Stream.Null;

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.ContentType, Does.Contain("application/json"));
    }

    // Unknown extra path segments still 404; trailing slashes are accepted (covered below).
    private static readonly TestCaseData[] MalformedPathCases =
    [
        new TestCaseData("GET", "/engine/v2/capabilities/foo", true, null).SetName("Malformed_capabilities_extra_segment_404"),
        new TestCaseData("GET", "/engine/v2/identity/foo", true, null).SetName("Malformed_identity_extra_segment_404"),
        new TestCaseData("POST", "/engine/v2/forkchoice/whatever", false, "cancun").SetName("Malformed_forkchoice_extra_segment_404"),
    ];

    [TestCaseSource(nameof(MalformedPathCases))]
    public async Task Malformed_or_trailing_path_returns_404(string method, string path, bool assertMethodNotFoundBody, string? fork)
    {
        DefaultHttpContext ctx = method == "POST"
            ? MakePostContext(path, [], fork: fork)
            : MakeBaseContext("GET", path, AuthenticatedPort);
        if (method == "GET")
        {
            ctx.Request.Headers.Accept = "application/json";
            ctx.Request.Body = Stream.Null;
        }

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        if (assertMethodNotFoundBody)
        {
            string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
            Assert.That(body, Does.Contain("method-not-found"));
        }
    }

    [TestCase("/engine/v2/capabilities/")]
    [TestCase("/engine/v2/identity/")]
    public async Task Trailing_slash_on_unscoped_endpoint_returns_404(string path)
    {
        DefaultHttpContext ctx = MakeBaseContext("GET", path, AuthenticatedPort);
        ctx.Request.Headers.Accept = "application/json";
        ctx.Request.Body = Stream.Null;

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [Test]
    public async Task Trailing_slash_on_fork_scoped_path_with_id_does_not_leak_into_extra()
    {
        _engineModule.engine_getPayloadV2(Arg.Any<byte[]>())
            .Returns(ResultWrapper<GetPayloadV2Result?>.Success(new GetPayloadV2Result(MakeMinimalBlock(), UInt256.One)));

        DefaultHttpContext ctx = MakeGetContext("/engine/v2/payloads/0x0102030405060708/", fork: "paris");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task Unknown_fork_in_header_returns_400_unsupported_fork()
    {
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", [], fork: "atlantis");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        Assert.That(body, Does.Contain("unsupported-fork"));
    }

    [Test]
    public async Task Missing_fork_header_on_fork_scoped_endpoint_returns_400()
    {
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", BuildMinimalV1NewPayloadRequest());

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        Assert.That(body, Does.Contain("invalid-request"));
        await _engineModule.DidNotReceive().engine_newPayloadV1(Arg.Any<ExecutionPayload>());
    }

    [Test]
    public async Task Fork_that_predates_endpoint_returns_400_unsupported_fork()
    {
        // Paris is a supported fork but predates getPayloadBodies (introduced in Shanghai), so the
        // endpoint is recognised yet unavailable for this fork — 400 unsupported-fork, not 404.
        DefaultHttpContext ctx = MakePostContext("/engine/v2/bodies/hash",
            BuildPayloadBodiesByHashRequest([TestItem.KeccakA]), fork: "paris");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        Assert.That(body, Does.Contain("unsupported-fork"));
        _engineModule.DidNotReceive().engine_getPayloadBodiesByHashV1(Arg.Any<IReadOnlyList<Hash256>>());
    }

    [Test]
    public async Task Unknown_blob_version_returns_404()
    {
        DefaultHttpContext ctx = MakePostContext("/engine/v2/blobs/v99", []);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [Test]
    public async Task Invalid_payload_id_in_path_returns_400()
    {
        DefaultHttpContext ctx = MakeGetContext("/engine/v2/payloads/0xZZZZZZZZZZZZZZZZ", fork: "paris");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task GetPayloadBodiesByRange_over_limit_returns_413_request_too_large()
    {
        DefaultHttpContext ctx = MakeGetContext("/engine/v2/bodies", fork: "shanghai");
        ctx.Request.QueryString = new QueryString("?from=1&count=1000");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        Assert.That(body, Does.Contain("request-too-large"));
    }

    [Test]
    public async Task GetPayloadBodiesByRange_from_zero_is_valid()
    {
        _engineModule.engine_getPayloadBodiesByRangeV1(Arg.Any<ulong>(), Arg.Any<ulong>())
            .Returns(ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success([]));

        DefaultHttpContext ctx = MakeGetContext("/engine/v2/bodies", fork: "shanghai");
        ctx.Request.QueryString = new QueryString("?from=0&count=1");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.Not.EqualTo(StatusCodes.Status400BadRequest),
            "from=0 (genesis block) must be accepted");
    }

    [Test]
    public async Task Error_response_has_correct_RFC7807_shape_type_only_for_canned_errors()
    {
        byte[] garbage = new byte[64];
        new Random(42).NextBytes(garbage);
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", garbage, fork: "paris");

        await _middleware.InvokeAsync(ctx);

        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
        System.Text.Json.JsonElement root = doc.RootElement;
        Assert.That(root.TryGetProperty("type", out _), Is.True, "RFC 7807 body must contain 'type'");
        Assert.That(root.TryGetProperty("detail", out _), Is.False, "ssz-decode-error must NOT include 'detail'");
        Assert.That(root.EnumerateObject().Count(), Is.EqualTo(1), "ssz-decode-error body must have exactly one key");
    }

    [Test]
    public async Task Error_response_has_correct_RFC7807_shape_with_detail_for_non_canned_errors()
    {
        DefaultHttpContext ctx = MakePostContext("/engine/v2/payloads", [], fork: "atlantis");

        await _middleware.InvokeAsync(ctx);

        string body = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
        System.Text.Json.JsonElement root = doc.RootElement;
        Assert.That(root.TryGetProperty("type", out _), Is.True);
        Assert.That(root.TryGetProperty("detail", out _), Is.True, "unsupported-fork must include 'detail'");
        Assert.That(root.EnumerateObject().Count(), Is.EqualTo(2), "error body must have exactly two keys: type + detail");
    }

    private const string WitnessPath = "/engine/v2/payloads/witness";
    private const string WitnessFork = "amsterdam";

    [TestCase(true, TestName = "NewPayloadWithWitness_valid_with_generated_witness_encodes_witness_present")]
    [TestCase(false, TestName = "NewPayloadWithWitness_valid_without_witness_encodes_witness_absent")]
    public async Task NewPayloadWithWitness_valid_status_encodes_witness_presence(bool withWitness)
    {
        Witness? stubWitness = withWitness
            ? new Witness
            {
                State = new ArrayPoolList<byte[]>(1) { new byte[] { 0xDE, 0xAD, 0xBE, 0xEF } },
                Codes = new ArrayPoolList<byte[]>(0),
                Keys = new ArrayPoolList<byte[]>(0),
                Headers = new ArrayPoolList<byte[]>(0),
            }
            : null;

        NewPayloadWithWitnessV1Result witnessResult = NewPayloadWithWitnessV1Result.FromPayloadStatus(
            new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA },
            stubWitness);

        _engineModule.engine_newPayloadWithWitness(
                Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
            .Returns(ResultWrapper<NewPayloadWithWitnessV1Result>.Success(witnessResult));

        byte[] body = BuildMinimalWitnessRequestBody();
        DefaultHttpContext ctx = MakePostContext(WitnessPath, body, fork: WitnessFork);

        await _middleware.InvokeAsync(ctx);

        await _engineModule.Received(1).engine_newPayloadWithWitness(
            Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>());
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK),
            "a VALID status must return 200 OK whether or not a witness was produced");
        Assert.That(ctx.Response.ContentType, Does.Contain(OctetStream),
            "successful SSZ responses must use application/octet-stream");

        byte[] responseBody = ResponseBytes(ctx);
        Assert.That(responseBody, Is.Not.Empty, "the SSZ body must contain the encoded response");

        (byte decodedStatus, Hash256? decodedLvh, bool witnessPresent) = SszCodec.DecodeNewPayloadWithWitnessResponse(responseBody);
        Assert.That(decodedStatus, Is.EqualTo(0), "decoded status byte must match VALID");
        Assert.That(decodedLvh, Is.EqualTo(TestItem.KeccakA),
            "latest_valid_hash Union Some variant must round-trip the hash correctly");
        Assert.That(witnessPresent, Is.EqualTo(withWitness),
            "the witness Union must be Some iff a witness was generated");
    }

    [Test]
    public async Task NewPayloadWithWitness_wrong_content_type_post_returns_415()
    {
        DefaultHttpContext ctx = MakeBaseContext("POST", WitnessPath, AuthenticatedPort);
        ctx.Request.ContentType = "text/plain";
        ctx.Request.Body = Stream.Null;

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status415UnsupportedMediaType),
            "a POST with wrong Content-Type must receive 415, like every other SSZ POST endpoint");
        Assert.That(ctx.Response.ContentType, Does.Contain("application/problem+json"));
    }

    [Test]
    public async Task NewPayloadWithWitness_non_valid_status_returns_200_with_ssz_body()
    {
        NewPayloadWithWitnessV1Result witnessResult = NewPayloadWithWitnessV1Result.FromPayloadStatus(
            new PayloadStatusV1 { Status = PayloadStatus.Syncing });

        _engineModule.engine_newPayloadWithWitness(
                Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
            .Returns(ResultWrapper<NewPayloadWithWitnessV1Result>.Success(witnessResult));

        byte[] body = BuildMinimalWitnessRequestBody();
        DefaultHttpContext ctx = MakePostContext(WitnessPath, body, fork: WitnessFork);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK),
            "SYNCING is a normal processing outcome and must return 200, not an HTTP error");
        Assert.That(ctx.Response.ContentType, Does.Contain(OctetStream));
        Assert.That(ResponseBytes(ctx), Is.Not.Empty, "the SSZ body must contain the status fields");
    }

    [Test]
    public async Task NewPayloadWithWitness_non_post_method_returns_404()
    {
        DefaultHttpContext ctx = MakeGetContext(WitnessPath, fork: WitnessFork);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound),
            "the witness resource is POST-only; a GET resolves no handler, like every other POST endpoint");
        Assert.That(ctx.Response.ContentType, Does.Contain("application/problem+json"));
    }

    [Test]
    public async Task NewPayloadWithWitness_on_pre_Amsterdam_fork_returns_400()
    {
        byte[] body = BuildMinimalWitnessRequestBody();
        DefaultHttpContext ctx = MakePostContext(WitnessPath, body, fork: "cancun");

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest),
            "the witness endpoint is gated to Amsterdam+; a recognized pre-EIP-7928 fork is 400 unsupported-fork");
        await _engineModule.DidNotReceive().engine_newPayloadWithWitness(
            Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>());
    }

    private static readonly TestCaseData[] WitnessEngineErrorCases =
    [
        new TestCaseData(MergeErrorCodes.UnsupportedFork, "Unsupported fork", StatusCodes.Status400BadRequest, "/engine-api/errors/unsupported-fork")
            .SetName("NewPayloadWithWitness_unsupported_fork_maps_to_400"),
        new TestCaseData(ErrorCodes.InternalError, "Something exploded", StatusCodes.Status500InternalServerError, "/engine-api/errors/internal")
            .SetName("NewPayloadWithWitness_internal_error_maps_to_500"),
    ];

    [TestCaseSource(nameof(WitnessEngineErrorCases))]
    public async Task NewPayloadWithWitness_engine_error_maps_to_http_status(
        int errorCode, string error, int expectedStatus, string expectedTypeUri)
    {
        _engineModule.engine_newPayloadWithWitness(
                Arg.Any<ExecutionPayloadV4>(), Arg.Any<Hash256?[]>(), Arg.Any<Hash256?>(), Arg.Any<byte[][]?>())
            .Returns(ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(error, errorCode));

        byte[] body = BuildMinimalWitnessRequestBody();
        DefaultHttpContext ctx = MakePostContext(WitnessPath, body, fork: WitnessFork);

        await _middleware.InvokeAsync(ctx);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(expectedStatus));
        Assert.That(ctx.Response.ContentType, Does.Contain("application/problem+json"));
        string responseBody = System.Text.Encoding.UTF8.GetString(ResponseBytes(ctx));
        Assert.That(responseBody, Does.Contain(expectedTypeUri));
    }

    private static byte[] BuildMinimalWitnessRequestBody() =>
        NewPayloadV5RequestWire.Encode(new NewPayloadV5RequestWire
        {
            ExecutionPayload = new SszExecutionPayloadV4(SszTestData.MakeV4Payload(blockAccessList: [0xc0], slotNumber: 0)),
            ParentBeaconBlockRoot = TestItem.KeccakA,
        });
}
