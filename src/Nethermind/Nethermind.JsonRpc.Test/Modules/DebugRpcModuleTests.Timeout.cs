// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public void Debug_traceCall_timeout_source_generated_metadata_defers_invalid_duration()
    {
        GethTraceOptions options = JsonSerializer.Deserialize("{\"timeout\":\"bad\",\"tracer\":\"callTracer\"}", EthRpcJsonContext.Default.GethTraceOptions)!;
        Assert.That(options.Tracer, Is.EqualTo("callTracer"));
        Assert.That(() => options.Timeout, Throws.TypeOf<FormatException>().With.Message.EqualTo("time: invalid duration \"bad\""));
    }

    private const string TimeoutJs = "{step:function(){},fault:function(){},result:function(){return {};}}";

    [Test]
    public async Task Debug_traceCall_timeout_streaming_and_buffered_routes(
        [Values] bool buffer, [Values(null, false, true)] bool? streamMode, [Values] bool streamDefault)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.BufferResponses = buffer;
        config.EnableTracingStreamMode = streamDefault;
        JToken response = await TraceTimeout(ctx, new { timeout = "0", streamMode, stateOverrides = FlatOverrides("5b600056") });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["error"]!["message"]!.Value<string>(), Is.EqualTo("execution timeout"));
            Assert.That(response["error"]!["code"]!.Value<int>(), Is.EqualTo(-32000));
            Assert.That(response["result"], Is.Null);
        }
    }

    [Test]
    public async Task Debug_traceCall_timeout_native_and_js_expire(
        [Values("callTracer", "prestateTracer", "4byteTracer", TimeoutJs)] string tracer,
        [Values("0", "-1s", "1ns")] string timeout, [Values(null, "0x0")] string? txIndex)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken response = await TraceTimeout(ctx, new { tracer, timeout, txIndex, stateOverrides = FlatOverrides("5b600056") });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["error"]!["code"]!.Value<int>(), Is.EqualTo(-32000), response.ToString());
            Assert.That(response["error"]!["message"]!.Value<string>(), Does.StartWith("execution timeout"));
        }
    }

    [Test]
    public async Task Debug_traceCall_timeout_long_go_duration_is_valid(
        [Values("", "callTracer", TimeoutJs)] string tracer,
        [Values("3m", "2562047h47m16.854775807s")] string timeout)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken response = await TraceTimeout(ctx, new { tracer, timeout, stateOverrides = FlatOverrides("00") });
        Assert.That(response["error"], Is.Null, response.ToString());
    }

    [Test]
    public async Task Debug_traceCall_timeout_noop_stop_is_ignored([Values] bool mux, [Values("0", "-1s", "1ns")] string timeout)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        JToken response = await TraceTimeout(ctx, new
        {
            tracer = mux ? "muxTracer" : "noopTracer",
            timeout,
            tracerConfig = mux ? new Dictionary<string, object> { ["noopTracer"] = new { } } : null,
            stateOverrides = FlatOverrides("5b600056")
        });
        Assert.That(response["error"], Is.Null, response.ToString());
        Assert.That(JToken.DeepEquals(response["result"], JObject.Parse(mux ? "{\"noopTracer\":{}}" : "{}")), Is.True);
    }

    [TestCase("bad", "time: invalid duration \"bad\"")]
    [TestCase("00:00:05", "time: unknown unit \":\" in duration \"00:00:05\"")]
    [TestCase("1", "time: missing unit in duration \"1\"")]
    public async Task Debug_traceCall_timeout_invalid_duration_is_top_level(string timeout, string message)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        foreach (bool buffer in new[] { false, true })
            foreach (bool streamMode in new[] { false, true })
            {
                ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().BufferResponses = buffer;
                JToken response = await TraceTimeout(ctx, new { timeout, streamMode, stateOverrides = FlatOverrides("00") });
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(response["error"]!["code"]!.Value<int>(), Is.EqualTo(-32000));
                    Assert.That(response["error"]!["message"]!.Value<string>(), Is.EqualTo(message));
                    Assert.That(response["result"], Is.Null);
                }
            }
    }

    [Test]
    public async Task Debug_traceCall_timeout_tracer_validation_precedes_duration(
        [Values] bool buffer, [Values(null, false, true)] bool? streamMode)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().BufferResponses = buffer;
        JToken response = await TraceTimeout(ctx, new { tracer = "unknownTracer", timeout = "bad", streamMode, stateOverrides = FlatOverrides("00") });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["error"]!["code"]!.Value<int>(), Is.EqualTo(-32000));
            Assert.That(response["error"]!["message"]!.Value<string>(), Does.Contain("unknownTracer"));
            Assert.That(response["error"]!["message"]!.Value<string>(), Does.Not.Contain("time: invalid duration"));
        }
    }

    [Test]
    public async Task Debug_traceCall_timeout_mux_validates_all_children_first(
        [Values] bool buffer, [Values(null, false, true)] bool? streamMode)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().BufferResponses = buffer;
        JToken response = await TraceTimeout(ctx, new
        {
            tracer = "muxTracer",
            timeout = "bad",
            streamMode,
            tracerConfig = new Dictionary<string, object> { [TimeoutJs] = new { }, ["prestateTracer"] = new { diffMode = true, includeEmpty = true } },
            stateOverrides = FlatOverrides("00")
        });
        Assert.That(response["error"]!["message"]!.Value<string>(), Is.EqualTo("cannot use diffMode with includeEmpty"));
    }

    [Test]
    public async Task Debug_traceCall_repeated_deadlines_do_not_poison_next_call(
        [Values(null, "callTracer", TimeoutJs)] string? tracer)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Cancun.Instance));
        for (int attempt = 0; attempt < 10; attempt++)
        {
            JToken expired = await TraceTimeout(ctx, new { tracer, timeout = "0", stateOverrides = FlatOverrides("5b600056") });
            Assert.That(expired["error"]?["code"]?.Value<int>(), Is.EqualTo(-32000), expired.ToString());
            JToken recovered = await TraceTimeout(ctx, new { tracer, timeout = "1s", stateOverrides = FlatOverrides("00") });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recovered["error"], Is.Null, recovered.ToString());
                Assert.That(recovered["result"], Is.Not.Null);
            }
        }
    }

    [Test]
    public async Task Debug_traceCall_timeout_without_commitment_context_propagates()
    {
        CancellationTokenSource server = new();
        using GethLikeTxTraceStreamingSingleResult result = new((writer, _, token) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("pc", 0);
            writer.WriteEndObject();
            Assert.That(token.IsCancellationRequested, Is.False);
            throw new TimeoutException("execution timeout");
        }, server, LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());
        Pipe pipe = new();
        Assert.ThrowsAsync<TimeoutException>(async () => await result.WriteToAsync(pipe.Writer, CancellationToken.None));
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
        Assert.That(server.IsCancellationRequested, Is.False);
    }

    [Test]
    public void Debug_traceCall_timeout_unexecuted_stream_disposal_releases_owned_cts()
    {
        CancellationTokenSource server = new();
        bool executed = false;
        GethLikeTxTraceStreamingSingleResult result = new((_, _, _) => { executed = true; return null; },
            server, LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>());
        result.Dispose();
        Assert.That(executed, Is.False);
        Assert.That(() => server.Token, Throws.TypeOf<ObjectDisposedException>());
    }

    private static async Task<JToken> TraceTimeout(Context ctx, object options) => JToken.Parse(
        await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", FlatTransaction(), "latest", options));
}
