// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Newtonsoft.Json.Linq;
using Microsoft.ClearScript;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using NSubstitute;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Preimage;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

using System.Reflection;
using JavaScriptContext = Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript.Context;
using JavaScriptDb = Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript.Db;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_traceBlockByNumber_buffered_execution_deadline(
        [Values("callTracer", "prestateTracer", "4byteTracer", "noopTracer", "")] string tracer,
        [Values("0s", "-1s", "1s")] string timeout, [Values] bool filtered)
    {
        DeadlineExecution clock = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddDecorator<ITransactionProcessorAdapter>((_, inner) => new DeadlineAdapter(inner, clock)));
        Block block = await chain.AddBlock(CreateTraceBlockTransactions(chain));
        clock.Enabled = true;
        IDebugRpcModule module = WithDeadlineClock(chain.DebugRpcModule, clock);
        Dictionary<string, object> options = new() { ["timeout"] = timeout, ["streamMode"] = false };
        if (tracer.Length != 0) options["tracer"] = tracer;
        if (filtered) options["txHash"] = block.Transactions[^1].Hash!;

        foreach (string method in new[] { "debug_traceBlockByNumber", "debug_traceBlockByHash" })
        {
            object selector = method == "debug_traceBlockByNumber" ? block.Number : block.Hash!;
            string response = await RpcTest.TestSerializedRequest(module, method, selector, options);
            JToken json = JToken.Parse(response);
            if (timeout == "1s")
            {
                Assert.That(json["error"], Is.Null, response);
                Assert.That((JArray)json["result"]!, Has.Count.EqualTo(filtered ? 1 : block.Transactions.Length));
            }
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
                    Assert.That((string?)json["error"]?["message"], Is.EqualTo("execution timeout"), response);
                    Assert.That(json["result"], Is.Null, response);
                }
            }
        }
        Assert.That(clock.Timers, Is.Not.Empty);
        Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
    }

    [Test]
    [NonParallelizable]
    public async Task Specified_block_registered_native_tracers_enforce_reached_timeout(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values(NativeNoopTracer.NoopTracer, NativeKeccakPreimageTracer.KeccakPreimageTracer, "registered", Native4ByteTracer.FourByteTracer)] string tracer,
        [Values("before", "after")] string expiry)
    {
        if (tracer == "registered")
        {
            tracer = $"native-deadline-{Guid.NewGuid():N}";
            GethLikeNativeTracerFactory.RegisterTracer(tracer, static (options, _, transaction, _) => new NativeNoopTracer(transaction, options));
        }
        DeadlineExecution clock = new() { Expiry = expiry };
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddDecorator<ITransactionProcessorAdapter>((_, inner) => new DeadlineAdapter(inner, clock)));
        Block block = await chain.AddBlock(CreateTraceBlockTransactions(chain));
        clock.Enabled = true;
        clock.Target = block.Transactions[expiry == "before" ? 1 : 0].Nonce;
        object selector = method == "debug_traceBlockByNumber" ? block.Number : block.Hash!;
        string response = await RpcTest.TestSerializedRequest(WithDeadlineClock(chain.DebugRpcModule, clock), method, selector,
            new { tracer, timeout = "1s", streamMode = false });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
            Assert.That((string?)json["error"]?["message"], Is.EqualTo("execution timeout"), response);
            Assert.That(json["result"], Is.Null, response);
            Assert.That(clock.Timers, Has.Count.EqualTo(expiry == "before" ? 2 : 1));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    [Test]
    public async Task Specified_block_empty_tracer_uses_default_opcode_logger(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method, [Values] bool streamMode)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object selector = method == "debug_traceBlockByNumber" ? block.Number : block.Hash!;
        JToken baseline = JToken.Parse(await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector, new { streamMode }));
        Assert.That(baseline["error"], Is.Null, baseline.ToString());
        Assert.That((JArray)baseline["result"]!, Has.Count.EqualTo(block.Transactions.Length));
        foreach (JToken entry in baseline["result"]!) Assert.That((JArray)entry["result"]!["structLogs"]!, Is.Not.Empty);
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector, new { tracer = "", streamMode });
        Assert.That(JToken.DeepEquals(JToken.Parse(response), baseline), Is.True, response);
    }

    [Test]
    public async Task Specified_block_javascript_accepts_go_timeout_range(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values] bool streamMode,
        [Values("0s", "-1s", "1ns", "121s", "4294968s", "1s")] string timeout)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        DeadlineExecution clock = new();
        object selector = method == "debug_traceBlockByNumber" ? block.Number : block.Hash!;
        string response = await RpcTest.TestSerializedRequest(WithDeadlineClock(context.DebugRpcModule, clock), method, selector,
            new { tracer = "{fault:function(){},result:function(){return {ok:true};}}", timeout, streamMode });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        JArray entries = (JArray)json["result"]!;
        Assert.That(entries, Has.Count.EqualTo(block.Transactions.Length));
        foreach (JToken entry in entries) Assert.That((bool?)entry["result"]?["ok"], Is.True, response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.Timers, Has.Count.EqualTo(block.Transactions.Length));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    [Test]
    public async Task Specified_block_javascript_owned_expiry_discards_partial_results(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values] bool streamMode, [Values("before", "after")] string expiry,
        [Values] bool ordinaryFailure)
    {
        DeadlineExecution clock = new() { Expiry = expiry };
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddDecorator<ITransactionProcessorAdapter>((_, inner) => new DeadlineAdapter(inner, clock)));
        Block block = await chain.AddBlock(CreateTraceBlockTransactions(chain));
        clock.Enabled = true;
        clock.Target = block.Transactions[expiry == "before" ? 1 : 0].Nonce;
        object selector = method == "debug_traceBlockByNumber" ? block.Number : block.Hash!;
        string tracer = "{fault:function(){},step:function(){" + (ordinaryFailure ? "throw Error('execution timeout');" : "")
            + "},result:function(){return {};}}";
        string response = await RpcTest.TestSerializedRequest(WithDeadlineClock(chain.DebugRpcModule, clock), method, selector,
            new { tracer, timeout = "1s", streamMode });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
            Assert.That((string?)json["error"]?["message"], Is.EqualTo("execution timeout"), response);
            Assert.That(json["result"], Is.Null, response);
            Assert.That(clock.Timers, Has.Count.EqualTo(expiry == "before" ? 2 : 1));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    [Test]
    public async Task Specified_javascript_owned_interrupt_is_typed([Values("result", "step")] string callback)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        DeadlineExecution clock = new() { Expiry = "before" };
        Action expire = () => clock.Advance(0);
        using (ScriptObject installer = (ScriptObject)engine.CreateTracer("{install:function(action){globalThis.expireTrace=action;}}"))
            installer.InvokeMethod("install", expire);
        string body = "expireTrace();for(let i=0;i<10000;i++){};";
        using GethLikeJavaScriptTxTracer tracer = CreateDeadlineJavaScriptTracer(engine, chain, clock,
            "{fault:function(){},step:function(){" + (callback == "step" ? body : "")
                + "},result:function(){" + (callback == "result" ? body : "") + "return {};}}");
        Assert.Throws<GethTraceDeadlineException>(() =>
        {
            if (callback == "step") tracer.SetOperationStack(default);
            using GethLikeTxTrace result = tracer.BuildResult();
        });
        tracer.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.Timers, Has.Count.EqualTo(1));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    [Test]
    public async Task Specified_javascript_constructor_failure_releases_timer()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        DeadlineExecution clock = new() { FailTimerChange = true };
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
        {
            using GethLikeJavaScriptTxTracer tracer = CreateDeadlineJavaScriptTracer(engine, chain, clock,
                "{fault:function(){},result:function(){return {};}}");
        })!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Message, Is.EqualTo("timer change failure"));
            Assert.That(clock.Timers, Has.Count.EqualTo(1));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    [Test]
    public async Task Specified_javascript_unexpired_deadline_preserves_failure_provenance(
        [Values("input", "host", "fatal", "cancellation", "interrupt", "input-interrupt", "user-timeout")] string kind)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        DeadlineExecution clock = new();
        Action fail = kind switch
        {
            "input" => () => engine.AbortInput("deadline input marker"),
            "host" => () => throw new InvalidOperationException("deadline host marker"),
            "fatal" => () => throw CreateException(true, true, null),
            "cancellation" => () => throw new OperationCanceledException(),
            "interrupt" => engine.Interrupt,
            "input-interrupt" => () => { engine.AbortInput("deadline input marker"); engine.Interrupt(); },
            _ => () => { }
        };
        using (ScriptObject installer = (ScriptObject)engine.CreateTracer("{install:function(action){globalThis.failDeadlineTrace=action;}}"))
            installer.InvokeMethod("install", fail);
        using GethLikeJavaScriptTxTracer tracer = CreateDeadlineJavaScriptTracer(engine, chain, clock,
            "{fault:function(){},result:function(){failDeadlineTrace();throw "
                + (kind == "user-timeout" ? "Error('execution timeout')" : "null") + ";}}");
        if (kind is "input" or "user-timeout")
        {
            using GethLikeTxTrace result = tracer.BuildResult();
            Assert.That(result.TraceError, Does.Contain(kind == "input" ? "deadline input marker" : "execution timeout"));
        }
        else
        {
            Exception failure = Assert.Catch(() => { using GethLikeTxTrace result = tracer.BuildResult(); })!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(failure, Is.Not.TypeOf<GethTraceDeadlineException>());
                Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.False, failure.ToString());
            }
        }
        tracer.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.Timers, Has.Count.EqualTo(1));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    private static GethLikeJavaScriptTxTracer CreateDeadlineJavaScriptTracer(
        Engine engine, TestRpcBlockchain chain, DeadlineExecution clock, string script) =>
        new(engine, new JavaScriptDb(chain.MainWorldState), new JavaScriptContext(), new GethTraceOptions
        {
            EnforceExecutionDeadline = true, CaptureJavaScriptErrors = true,
            DeadlineClock = clock, Timeout = TimeSpan.FromSeconds(1), Tracer = script
        });

    [TestCase("callTracer", "onlyTopCall")]
    [TestCase("prestateTracer", "diffMode")]
    public async Task Debug_traceBlockByNumber_native_config_error_precedes_expired_deadline(string tracer, string field)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { tracer, timeout = "0s", tracerConfig = JsonSerializer.Deserialize<JsonElement>($$"""{"{{field}}":[]}""") });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Not.Null, response);
        Assert.That(json["error"]!.ToString(), Does.Contain("Cannot convert").And.Not.Contain("execution timeout"), response);
    }

    [Test]
    public async Task Debug_traceBlockByNumber_does_not_start_deadlines_without_a_selected_transaction(
        [Values("callTracer", "prestateTracer", "4byteTracer", "")] string tracer, [Values] bool empty)
    {
        using Context context = await Context.Create();
        Block block = empty ? await context.Blockchain.AddBlock()
            : await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        Dictionary<string, object> options = new() { ["timeout"] = "0s", ["streamMode"] = false };
        if (tracer.Length != 0) options["tracer"] = tracer;
        if (!empty) options["txHash"] = TestItem.KeccakD;

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number, options);
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        Assert.That((JArray)json["result"]!, Is.Empty);
    }

    [Test]
    public async Task NativeConfigPreflight_matches_constructor_null_semantics(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash", "debug_traceBlock")] string method,
        [Values("callTracer", "prestateTracer")] string tracer,
        [Values("null", "{\"withLog\":null,\"diffMode\":null}",
            "{\"onlyTopCall\":true,\"onlyTopCall\":null,\"diffMode\":true,\"diffMode\":null}")] string configuration)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object selector = method switch
        {
            "debug_traceBlockByNumber" => block.Number,
            "debug_traceBlockByHash" => block.Hash!,
            _ => Rlp.Encode(block).ToString()
        };
        JsonElement tracerConfig = JsonSerializer.Deserialize<JsonElement>(configuration);
        string buffered = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer, tracerConfig, streamMode = false });
        string streamed = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer, tracerConfig, streamMode = true });

        Assert.That(JToken.Parse(buffered)["error"], Is.Null, buffered);
        Assert.That(streamed, Is.EqualTo(buffered));
        Assert.That((JArray)JToken.Parse(streamed)["result"]!, Has.Count.EqualTo(block.Transactions.Length));
    }

    [Test]
    public async Task NativeConfigPreflight_malformed_config_streams_like_buffered(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash", "debug_traceBlock")] string method,
        [Values("callTracer", "prestateTracer")] string tracer,
        [Values] bool empty)
    {
        using Context context = await Context.Create();
        Block block = empty
            ? await context.Blockchain.AddBlock()
            : await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object selector = method switch
        {
            "debug_traceBlockByNumber" => block.Number,
            "debug_traceBlockByHash" => block.Hash!,
            _ => Rlp.Encode(block).ToString()
        };
        JsonElement tracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"onlyTopCall":17,"diffMode":17}""");
        string buffered = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer, tracerConfig, streamMode = false });
        string streamed = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer, tracerConfig, streamMode = true });

        Assert.That(streamed, Is.EqualTo(buffered));
        Assert.That(JToken.Parse(streamed)["error"], empty ? Is.Null : Is.Not.Null, streamed);
    }

    [Test]
    public async Task NativeStreamTimeout_Valid_duration_keeps_indexed_streaming(
        [Values("5s", "0s", "-1s", "4294968s")] string timeout)
    {
        IParallelBlockTracer parallel = Substitute.For<IParallelBlockTracer>();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton(parallel));
        Block block = await chain.AddBlock(CreateTraceBlockTransactions(chain));
        var options = new { tracer = "callTracer", timeout, streamMode = true };
        DeadlineExecution clock = new();
        IDebugRpcModule module = WithDeadlineClock(chain.DebugRpcModule, clock);

        using JsonRpcResponse response = await RpcTest.TestRequest(module, "debug_traceBlockByNumber", block.Number, options);
        Assert.That(response.TryGetStreamableResult(out IStreamableResult? result) ? result : null,
            Is.InstanceOf<GethLikeTxTraceStreamingBlockResult>());
        string streamed = await RpcTest.TestSerializedRequest(module, "debug_traceBlockByNumber", block.Number, options);
        Assert.That(parallel.ReceivedCalls(), Is.Not.Empty, "the consumed native stream must use the indexed path");
        string buffered = await RpcTest.TestSerializedRequest(module, "debug_traceBlockByNumber", block.Number,
            new { tracer = "callTracer", timeout, streamMode = false });
        Assert.That(streamed, Is.EqualTo(buffered));
        Assert.That((JArray)JToken.Parse(streamed)["result"]!, Has.Count.EqualTo(block.Transactions.Length));
        Assert.That(clock.Timers, Has.Count.EqualTo(block.Transactions.Length));
        Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
    }

    private static IDebugRpcModule WithDeadlineClock(IDebugRpcModule inner, TimeProvider clock)
    {
        IDebugRpcModule module = Substitute.For<IDebugRpcModule>();
        module.debug_traceBlockByNumber(Arg.Any<BlockParameter>(), Arg.Any<GethTraceOptions>()).Returns(call =>
            inner.debug_traceBlockByNumber(call.Arg<BlockParameter>(), call.Arg<GethTraceOptions>() with { DeadlineClock = clock }));
        module.debug_traceBlockByHash(Arg.Any<Hash256>(), Arg.Any<GethTraceOptions>()).Returns(call =>
            inner.debug_traceBlockByHash(call.Arg<Hash256>(), call.Arg<GethTraceOptions>() with { DeadlineClock = clock }));
        return module;
    }

    [Test]
    public async Task Debug_traceBlockByNumber_native_config_errors_are_execution_input_errors(
        [Values("callTracer", "prestateTracer", "noopTracer")] string tracer,
        [Values("17", "[]", "\"bad\"", "{\"onlyTopCall\":17,\"diffMode\":17}")] string configuration,
        [Values] bool empty) =>
        await AssertNativeConfigError("debug_traceBlockByNumber", tracer, configuration, empty);

    [TestCase("debug_traceBlock", "{\"diffMode\":[]}", false, false)]
    [TestCase("debug_traceBlock", "{\"diffMode\":[]}", true, false)]
    [TestCase("debug_traceBlock", "{}", false, false)]
    [TestCase("debug_traceBlock", "{\"diffMode\":[]}", false, true)]
    public async Task Debug_traceBlock_native_config_errors_are_execution_input_errors(
        string method, string configuration, bool empty, bool invalidRlp) =>
        await AssertNativeConfigError(method, "prestateTracer", configuration, empty, invalidRlp: invalidRlp);

    [Test]
    public async Task Debug_traceTransactionInBlockByHash_native_config_errors_remain_invalid_params() =>
        await AssertNativeConfigError("debug_traceTransactionInBlockByHash", "prestateTracer", "{\"diffMode\":[]}", empty: false, expectedError: ErrorCodes.InvalidParams);

    [TestCase("callTracer", "{\"onlyTopCall\":17}", false, false)]
    [TestCase("prestateTracer", "{\"diffMode\":[]}", false, false)]
    [TestCase("prestateTracer", "17", false, false)]
    [TestCase("prestateTracer", "{\"diffMode\":[]}", true, false)]
    [TestCase("prestateTracer", "{\"diffMode\":[]}", false, true)]
    public async Task Debug_traceBlockByHash_native_config_errors_are_execution_input_errors(
        string tracer, string configuration, bool empty, bool unknown) =>
        await AssertNativeConfigError("debug_traceBlockByHash", tracer, configuration, empty, unknown: unknown);

    [Test]
    public async Task Debug_traceTransaction_native_config_errors_are_execution_input_errors() =>
        await AssertNativeConfigError("debug_traceTransaction", "prestateTracer", "{\"diffMode\":[]}", empty: false, expectedError: ErrorCodes.InvalidInput);

    private static async Task AssertNativeConfigError(string method, string tracer, string configuration, bool empty, bool invalidRlp = false, bool unknown = false, int? expectedError = null, bool? streamMode = null)
    {
        using Context context = await Context.Create();
        Block block = empty
            ? await context.Blockchain.AddBlock()
            : await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        using JsonDocument config = JsonDocument.Parse(configuration);

        object options = new { tracer, tracerConfig = config.RootElement, timeout = "5s", streamMode };
        object target = method switch
        {
            "debug_traceBlockByNumber" => block.Number,
            "debug_traceBlockByHash" => unknown ? TestItem.KeccakD : block.Hash!,
            "debug_traceTransaction" => block.Transactions[0].Hash!,
            _ => invalidRlp ? "0xc0" : Rlp.Encode(block).ToString()
        };
        object[] parameters = method == "debug_traceTransactionInBlockByHash"
            ? [target, block.Transactions[0].Hash!, options]
            : [target, options];
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, parameters);
        JToken json = JToken.Parse(response);

        if (unknown)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.ResourceNotFound), response);
                Assert.That((string?)json["error"]?["message"], Does.Contain(TestItem.KeccakD.ToString()), response);
                Assert.That(json["result"], Is.Null, response);
            }
        }
        else if (expectedError is not null || invalidRlp || !empty && tracer != "noopTracer" && configuration != "{}")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(expectedError ?? ErrorCodes.InvalidInput), response);
                Assert.That(json["result"], Is.Null, response);
                if (invalidRlp)
                    Assert.That((string?)json["error"]?["message"], Does.Contain("Error decoding block RLP"), response);
            }
        }
        else
        {
            Assert.That(json["error"], Is.Null, response);
            JArray traces = (JArray)json["result"]!;
            Assert.That(traces, Has.Count.EqualTo(block.Transactions.Length), response);
            if (method == "debug_traceBlock")
                for (int i = 0; i < traces.Count; i++)
                    Assert.That((string?)traces[i]["txHash"], Is.EqualTo(block.Transactions[i].Hash!.ToString()), response);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Debug_native_tracer_host_errors_keep_existing_classification_when_buffered(
        [Values("debug_traceBlock", "debug_traceBlockByNumber")] string method, [Values] bool jsonError)
    {
        string tracer = nameof(Debug_native_tracer_host_errors_keep_existing_classification_when_buffered) + method + jsonError;
        Exception failure = jsonError ? new JsonException("host construction failure") : new FormatException("host construction failure");
        GethLikeNativeTracerFactory.RegisterTracer(tracer, (_, _, _, _) => throw failure);
        await AssertNativeConfigError(method, tracer, "{}", empty: false,
            expectedError: jsonError ? ErrorCodes.InvalidParams : ErrorCodes.InternalError, streamMode: false);
    }

    [Test]
    [NonParallelizable]
    [Repeat(2)]
    public async Task Debug_native_tracer_host_json_errors_remain_invalid_params_when_buffered(
        [Values("debug_traceBlockByHash", "debug_traceBlockByNumber")] string method)
    {
        string tracer = $"native-host-buffered-{method}-{Guid.NewGuid():N}";
        GethLikeNativeTracerFactory.RegisterTracer(tracer, static (_, _, _, _) => throw new JsonException("host construction failure"));
        await AssertNativeConfigError(method, tracer, "{}", empty: false, expectedError: ErrorCodes.InvalidParams, streamMode: false);
    }

    [Test]
    public async Task Debug_native_config_errors_preserve_route_classification(
        [Values("debug_traceBlockByHash", "debug_traceBlock", "debug_traceTransaction", "debug_traceCall")] string method,
        [Values("callTracer", "prestateTracer")] string tracer,
        [Values(null, "bad", "5s")] string? timeout,
        [Values("17", "[]")] string invalidValue) =>
        await AssertNativeConfigRouteClassification(method, tracer, timeout, invalidValue,
            method == "debug_traceCall" ? ErrorCodes.InvalidParams : ErrorCodes.InvalidInput);

    [TestCase("debug_traceBlockByHash", "callTracer", ErrorCodes.InvalidInput)]
    [TestCase("debug_traceBlockByHash", "prestateTracer", ErrorCodes.InvalidInput)]
    [TestCase("debug_traceBlock", "callTracer", ErrorCodes.InvalidInput)]
    [TestCase("debug_traceBlock", "prestateTracer", ErrorCodes.InvalidInput)]
    [TestCase("debug_traceTransaction", "callTracer", ErrorCodes.InvalidInput)]
    [TestCase("debug_traceTransaction", "prestateTracer", ErrorCodes.InvalidInput)]
    [TestCase("debug_traceCall", "callTracer", ErrorCodes.InvalidParams)]
    [TestCase("debug_traceCall", "prestateTracer", ErrorCodes.InvalidParams)]
    public async Task Debug_native_config_errors_preserve_route_classification(
        string method, string tracer, int expectedCode) =>
        await AssertNativeConfigRouteClassification(method, tracer, null, "17", expectedCode);

    private static async Task AssertNativeConfigRouteClassification(string method, string tracer, string? timeout, string invalidValue, int expectedCode)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object selector = method switch
        {
            "debug_traceBlockByHash" => block.Hash!,
            "debug_traceBlock" => new BlockDecoder().Encode(block).Bytes,
            "debug_traceTransaction" => block.Transactions[0].Hash!,
            _ => new { to = TestItem.AddressC.ToString(), gas = "0x186a0" }
        };
        JsonElement tracerConfig = JsonSerializer.Deserialize<JsonElement>($$"""{"onlyTopCall":{{invalidValue}},"diffMode":{{invalidValue}}}""");
        object options = timeout is null ? new { tracer, tracerConfig } : (object)new { tracer, timeout, tracerConfig };
        object[] parameters = method == "debug_traceCall" ? [selector, "latest", options] : [selector, options];
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, parameters);
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(expectedCode), response);
            Assert.That(json["result"], Is.Null, response);
            if (method == "debug_traceBlockByHash")
                Assert.That(json["error"]!.ToString(), Does.Contain("Cannot convert"), response);
        }
    }

    [Test]
    public async Task NativeStreamTimeout_Config_failure_precedes_bad_duration(
        [Values("callTracer", "prestateTracer")] string tracer)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        var tracerConfig = new { withLog = "yes", diffMode = "yes" };
        JToken buffered = JToken.Parse(await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { tracer, tracerConfig, timeout = "bad", streamMode = false }));
        JToken streamed = JToken.Parse(await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { tracer, tracerConfig, timeout = "bad", streamMode = true }));

        Assert.That((int?)buffered["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), buffered.ToString());
        Assert.That((int?)streamed["error"]?["code"], Is.EqualTo((int?)buffered["error"]?["code"]), streamed.ToString());
        Assert.That((string?)streamed["error"]?["message"], Does.Contain("Cannot convert").And.Not.Contain("Cannot parse 'bad'"));
    }

    [Test]
    public async Task NativeStreamTimeout_Registered_constructor_failure_preserves_error_boundary(
        [Values("json", "format", "cancel")] string kind,
        [Values("bad", "5s")] string timeout)
    {
        string tracer = $"native-host-{kind}-{Guid.NewGuid():N}";
        Exception failure = kind switch
        {
            "json" => new JsonException("native host failure"),
            "format" => new FormatException("native host failure"),
            _ => new OperationCanceledException("native host failure")
        };
        int constructorCalls = 0;
        GethLikeNativeTracerFactory.RegisterTracer(tracer, (_, _, _, _) =>
        {
            constructorCalls++;
            throw failure;
        });
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        var options = new { tracer, timeout, streamMode = true };

        if (timeout == "5s")
        {
            using JsonRpcResponse response = await RpcTest.TestRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number, options);
            Assert.That(response.TryGetStreamableResult(out IStreamableResult? result) ? result : null,
                Is.InstanceOf<GethLikeTxTraceStreamingBlockResult>());
            Assert.That(constructorCalls, Is.Zero, "the native constructor must remain deferred until the writer is consumed");

            JArray envelopes = JArray.Parse(await StreamToStringAsync(result!));
            Assert.That(constructorCalls, Is.EqualTo(1));
            Assert.That(envelopes, Has.Count.EqualTo(1));
            Assert.That((int?)envelopes[0]["errorCode"], Is.EqualTo(ErrorCodes.InternalError));
            Assert.That((string?)envelopes[0]["error"], Is.EqualTo("tracing failed: native host failure"));
            Assert.That(envelopes[0]["result"], Is.Null);
        }
        else
        {
            int expectedCode = kind switch
            {
                "json" => ErrorCodes.InvalidParams,
                "format" => ErrorCodes.InternalError,
                _ => ErrorCodes.Timeout
            };
            JToken response = JToken.Parse(await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number, options));

            Assert.That((int?)response["error"]?["code"], Is.EqualTo(expectedCode), response.ToString());
            Assert.That(response["result"], Is.Null);
            Assert.That(response.ToString(), Does.Not.Contain("Cannot parse 'bad'"));
        }
    }

    [Test]
    public async Task NativeStreamTimeout_Unselected_transactions_do_not_validate_bad_duration(
        [Values("empty", "unmatched")] string selection)
    {
        using Context context = await Context.Create();
        Block block = selection == "empty"
            ? await context.Blockchain.AddBlock()
            : await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        JToken response = JToken.Parse(await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { tracer = "noopTracer", timeout = "bad", streamMode = true, txHash = selection == "unmatched" ? TestItem.KeccakD : null }));

        Assert.That(response["error"], Is.Null, response.ToString());
        Assert.That((JArray)response["result"]!, Is.Empty);
    }

    private static IEnumerable<TestCaseData> NativeBooleanConfigCases()
    {
        foreach (string field in new[] { "onlyTopCall", "withLog", "diffMode", "includeEmpty" })
        {
            foreach ((string values, bool expected) in new (string, bool)[]
            {
                ("null", false), ("true,null", true), ("false,null", false),
                ("null,true", true), ("null,false", false), ("true,null,false", false),
                ("false,null,true", true), ("\"true\",null", true), ("\"false\",null", false)
            })
            {
                string[] members = values.Split(',');
                for (int i = 0; i < members.Length; i++) members[i] = $"\"{field}\":{members[i]}";
                string config = "{" + string.Join(',', members) + "}";
                yield return new TestCaseData(field, config, expected).SetName($"Native_boolean_config_{field}_{values}");
            }
            yield return new TestCaseData(field, $$$"""{"{{{field.ToUpperInvariant()}}}":true,"{{{field}}}":null,"unknown":{"nested":null}}""", true)
                .SetName($"Native_boolean_config_{field}_case_insensitive");
        }
    }

    [TestCaseSource(nameof(NativeBooleanConfigCases))]
    public async Task Debug_traceBlockByNumber_native_boolean_config_ignores_null(string field, string config, bool expected)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false }).Build();
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        byte[] code = Prepare.EvmCode.Log(0, 0).Call(TestItem.AddressC, 30_000).STOP().Done;
        Block block = await chain.AddBlock(Build.A.Transaction.WithNonce(nonce).WithCode(code)
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        string tracer = field is "diffMode" or "includeEmpty" ? "prestateTracer" : "callTracer";
        async Task<JToken> Trace(string json)
        {
            string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
                new { tracer, tracerConfig = JsonSerializer.Deserialize<JsonElement>(json) });
            JToken result = JToken.Parse(response);
            Assert.That(result["error"], Is.Null, response);
            return result["result"]![0]!["result"]!;
        }

        JToken baseline = await Trace(expected ? $$"""{"{{field}}":true}""" : "{}");
        JToken actual = await Trace(config);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.DeepEquals(actual, baseline), Is.True, actual.ToString());
            Assert.That(actual["calls"] is not null, Is.EqualTo(field is not ("diffMode" or "includeEmpty") && !(field == "onlyTopCall" && expected)));
            Assert.That(actual["logs"] is not null, Is.EqualTo(field == "withLog" && expected));
            Assert.That(actual["pre"] is not null && actual["post"] is not null, Is.EqualTo(field == "diffMode" && expected));
        }
    }

    [Test]
    public async Task Debug_traceBlockByNumber_native_boolean_config_rejects_invalid_types(
        [Values("onlyTopCall", "withLog", "diffMode", "includeEmpty", "disableCode", "disableStorage")] string field,
        [Values("1", "[]", "{}", "\"invalid\"")] string value)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false }).Build();
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Block block = await chain.AddBlock(Build.A.Transaction.WithNonce(nonce).WithCode([0])
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new
            {
                tracer = field is "onlyTopCall" or "withLog" ? "callTracer" : "prestateTracer",
                tracerConfig = JsonSerializer.Deserialize<JsonElement>($$"""{"{{field}}":{{value}}}"""),
                timeout = "0s"
            });
        JToken error = JToken.Parse(response)["error"]!;
        Assert.That(error, Is.Not.Null, response);
        Assert.That((string?)error["data"] ?? (string?)error["message"], Does.Contain("Cannot convert"), "native config errors remain top-level even with an expired timeout");
    }

    [Test]
    public async Task Debug_traceBlock_setup_failure_precedes_timeout_policy(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash", "debug_traceBlock")] string method,
        [Values("0s", "-1s", "121s", "5s")] string timeout,
        [Values("error", "null", "primitive", "timeout-message")] string failure)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object selector = method switch
        {
            "debug_traceBlockByNumber" => block.Number,
            "debug_traceBlockByHash" => block.Hash!,
            _ => Rlp.Encode(block).ToString()
        };
        string thrown = failure switch { "null" => "null", "primitive" => "42", "timeout-message" => "Error('execution timeout')", _ => "Error('setup-before-timeout')" };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer = "{fault:function(){},setup:function(){throw " + thrown + ";},result:function(){return {};}}", timeout });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        JArray traces = (JArray)json["result"]!;
        Assert.That(traces, Has.Count.EqualTo(block.Transactions.Length), response);
        for (int i = 0; i < traces.Count; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((string?)traces[i]["txHash"], Is.EqualTo(block.Transactions[i].Hash!.ToString()), response);
                Assert.That((string?)traces[i]["error"], Is.Not.Null.And.Not.Empty, response);
                if (failure != "null")
                    Assert.That((string?)traces[i]["error"], Does.Contain(failure switch { "primitive" => "42", "timeout-message" => "execution timeout", _ => "setup-before-timeout" }), response);
                if (method == "debug_traceBlock") Assert.That(traces[i]["result"], Is.Null, response);
                else Assert.That(traces[i]["result"]?.Type, Is.EqualTo(JTokenType.Null), response);
            }
        }
    }

    private static IEnumerable<TestCaseData> BlockTimeoutPrecedenceCases()
    {
        foreach (string method in new[] { "debug_traceBlockByNumber", "debug_traceBlockByHash" })
        {
            yield return new TestCaseData(false, "\"bad\"", "lookup", method);
            yield return new TestCaseData(false, "\"5s\"", "lookup", method);
            yield return new TestCaseData(true, "\"bad\"", "constructor", method);
            yield return new TestCaseData(true, "\"5s\"", "constructor", method);
            yield return new TestCaseData(false, "7", "type", method);
        }
        yield return new TestCaseData(false, "\"bad\"", "lookup-tx", "debug_traceTransaction");
        yield return new TestCaseData(false, "\"bad\"", "eager", "debug_intermediateRoots");
    }

    private static IEnumerable<TestCaseData> RawBlockTimeoutCases()
    {
        const string raw = "debug_traceBlock";
        const string noop = NativeNoopTracer.NoopTracer;
        const string invalidConfig = """{"diffMode":[]}""";
        const string nullConfig = """{"onlyTopCall":true,"onlyTopCall":null,"withLog":null}""";
        (string Selection, object Timeout, bool? Streaming, string? Tracer, string? Config, string Method, int? Code, string? Diagnostic)[] cases =
        [
            ("genesis", "5s", false, null, null, raw, ErrorCodes.InvalidInput, "genesis is not traceable"),
            ("genesis", "5s", true, null, null, raw, ErrorCodes.InvalidInput, "genesis is not traceable"),
            ("genesis", "5s", false, noop, null, raw, ErrorCodes.InvalidInput, "genesis is not traceable"),
            ("decode", "5s", false, null, null, raw, ErrorCodes.InvalidInput, "decoding block RLP"),
            ("decode", "5s", true, null, null, raw, ErrorCodes.InvalidInput, "decoding block RLP"),
            ("empty", "5s", false, null, null, raw, null, null),
            ("empty", "5s", true, null, null, raw, null, null),
            ("decode", "bad", null, noop, null, raw, ErrorCodes.InvalidInput, "decoding block RLP"),
            ("genesis", "bad", null, noop, null, raw, ErrorCodes.InvalidInput, "genesis is not traceable"),
            ("empty", "bad", null, noop, null, raw, null, null),
            ("nonempty", "bad", null, noop, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("nonempty", "bad", null, "prestateTracer", invalidConfig, raw, ErrorCodes.InvalidInput, "Cannot convert"),
            ("nonempty", "5s", null, "prestateTracer", invalidConfig, raw, ErrorCodes.InvalidInput, "Cannot convert"),
            ("nonempty", "5s", null, noop, null, raw, null, null),
            ("nonempty", "bad", null, "callTracer", nullConfig, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("nonempty", "5s", null, "callTracer", nullConfig, raw, null, null),
            ("nonempty", 7, null, noop, null, raw, ErrorCodes.InvalidParams, null),
            ("nonempty", new string('x', 65), null, noop, null, raw, ErrorCodes.InvalidParams, null),
            ("nonempty", "bad", null, noop, null, "debug_traceTransactionInBlockByHash", ErrorCodes.InvalidParams, null),
            ("nonempty", "bad", false, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("nonempty", "bad", true, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("nonempty", "5s", false, null, null, raw, null, null),
            ("nonempty", "5s", true, null, null, raw, null, null),
            ("empty", "bad", false, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("empty", "bad", true, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("unmatched", "bad", false, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("unmatched", "bad", true, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("decode", "bad", false, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1"),
            ("decode", "bad", true, null, null, raw, ErrorCodes.InvalidParams, "invalid argument 1")
        ];
        int index = 0;
        foreach ((string selection, object timeout, bool? streaming, string? tracer, string? config, string method, int? code, string? diagnostic) in cases)
        {
            Dictionary<string, object?> options = new() { ["timeout"] = timeout, ["tracer"] = tracer };
            if (streaming is { } streamMode) options["streamMode"] = streamMode;
            if (config is not null) options["tracerConfig"] = JsonSerializer.Deserialize<JsonElement>(config);
            if (selection == "unmatched") options["txHash"] = TestItem.KeccakD;
            yield return new TestCaseData(selection, method, options, code, diagnostic)
                .SetName($"RawBlockTimeout_{index++}_{selection}_{method}");
        }
    }

    [TestCaseSource(nameof(RawBlockTimeoutCases))]
    public async Task Debug_traceBlock_timeout_validation_preserves_fail_fast_errors_and_valid_traces(
        string selection, string method, Dictionary<string, object?> options, int? expectedCode, string? diagnostic)
    {
        using Context context = await Context.Create();
        Block block = selection switch
        {
            "genesis" => context.Blockchain.BlockTree.FindBlock(context.Blockchain.BlockTree.Genesis!.Hash!)!,
            "empty" => await context.Blockchain.AddBlock(),
            _ => await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain))
        };
        string input = selection == "decode" ? "0xc0" : Rlp.Encode(block).ToString();
        object[] parameters = method == "debug_traceTransactionInBlockByHash"
            ? [input, block.Transactions[0].Hash!, options] : [input, options];
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, parameters);
        JToken json = JToken.Parse(response);
        if (expectedCode is not null)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(expectedCode), response);
                Assert.That(json["result"], Is.Null, response);
                if (diagnostic is not null) Assert.That(json["error"]?.ToString(), Does.Contain(diagnostic), response);
            }
        }
        else
        {
            Assert.That(json["error"], Is.Null, response);
            JArray traces = (JArray)json["result"]!;
            Assert.That(traces, Has.Count.EqualTo(selection == "unmatched" ? 0 : block.Transactions.Length), response);
            for (int i = 0; i < traces.Count; i++)
                Assert.That((string?)traces[i]["txHash"], Is.EqualTo(block.Transactions[i].Hash!.ToString()), response);
        }

    }


    [Test]
    public async Task Debug_traceBlockByNumber_rejects_non_object_options([Values("17", "[]", "\"bad\"", "true")] string optionsJson)
    {
        using Context context = await Context.Create();
        using JsonDocument options = JsonDocument.Parse(optionsJson);

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber",
            context.Blockchain.BlockTree.Head!.Number, options.RootElement);

        Assert.That((int?)JToken.Parse(response)["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidParams), response);
    }

    [Test]
    public async Task Debug_traceBlockByNumber_null_boolean_preserves_trace_output(
        [Values("disableStorage", "disableStack", "enableMemory", "enableReturnData")] string property,
        [Values] bool enabled, [Values] bool streamMode, [Values] bool nullFirst)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        string members = $"\"limit\":1000,\"streamMode\":{streamMode.ToString().ToLowerInvariant()},\"{property}\":{enabled.ToString().ToLowerInvariant()}";
        using JsonDocument baselineOptions = JsonDocument.Parse("{" + members + "}");
        using JsonDocument nullOptions = JsonDocument.Parse(nullFirst
            ? $"{{\"{property}\":null," + members + "}"
            : "{" + members + $",\"{property}\":null" + "}");

        string expected = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number, baselineOptions.RootElement);
        string actual = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number, nullOptions.RootElement);

        Assert.That(JToken.Parse(expected)["error"], Is.Null, expected);
        Assert.That(JToken.DeepEquals(JToken.Parse(actual), JToken.Parse(expected)), Is.True, actual);
    }

    [Test]
    public async Task Debug_traceBlockByNumber_streaming_matches_buffered_output([Values] bool enableMemory)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        string buffered = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { enableMemory, enableReturnData = true, streamMode = false });
        string streamed = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { enableMemory, enableReturnData = true, streamMode = true });

        Assert.That(JToken.Parse(buffered)["error"], Is.Null, buffered);
        Assert.That(JToken.DeepEquals(JToken.Parse(streamed), JToken.Parse(buffered)), Is.True, streamed);
    }

    [TestCaseSource(nameof(BlockTimeoutPrecedenceCases))]
    public async Task Debug_traceBlock_orders_lookup_and_constructor_before_semantic_timeout(
        bool retainedBlock, string timeoutJson, string expected, string method)
    {
        using Context context = await Context.Create();
        if (retainedBlock)
            await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        ulong blockNumber = context.Blockchain.BlockTree.Head!.Number + (retainedBlock ? 0UL : 1UL);
        JsonElement timeout = JsonSerializer.Deserialize<JsonElement>(timeoutJson);
        string tracer = retainedBlock ? "unknownTracer" : NativeNoopTracer.NoopTracer;
        object selector = method == "debug_traceBlockByNumber" ? blockNumber
            : retainedBlock ? context.Blockchain.BlockTree.Head!.Hash! : TestItem.KeccakD;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new Dictionary<string, object> { ["tracer"] = tracer, ["timeout"] = timeout });
        JToken json = JToken.Parse(response);

        if (expected is "type" or "eager")
        {
            Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidParams), response);
            return;
        }

        if (expected == "lookup-tx")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.ResourceNotFound), response);
                Assert.That((string?)json["error"]?["message"], Is.EqualTo("transaction not found"), response);
                Assert.That(json["result"], Is.Null, response);
            }
            return;
        }

        if (expected == "lookup")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.ResourceNotFound), response);
                Assert.That((string?)json["error"]?["message"], Is.EqualTo(method == "debug_traceBlockByNumber"
                    ? $"block #{blockNumber} not found" : $"block {selector} not found"), response);
            }
            return;
        }

        Assert.That(json["error"], Is.Null, response);
        JArray traces = (JArray)json["result"]!;
        Assert.That(traces, Has.Count.EqualTo(context.Blockchain.BlockTree.Head!.Transactions.Length));
        for (int i = 0; i < traces.Count; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((string?)traces[i]["txHash"], Is.EqualTo(context.Blockchain.BlockTree.Head.Transactions[i].Hash!.ToString()));
                Assert.That((string?)traces[i]["error"], Does.Contain("unknownTracer"));
                Assert.That(traces[i]["result"]?.Type, Is.EqualTo(JTokenType.Null));
            }
        }
    }

    [Test]
    public async Task Debug_traceBlock_rejects_oversized_timeout_tokens_before_lookup(
        [Values("valid", "malformed")] string kind,
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method)
    {
        using Context context = await Context.Create();
        string timeout = kind == "valid" ? new string('0', 64) + "1s" : new string('x', 65);
        object selector = method == "debug_traceBlockByNumber" ? context.Blockchain.BlockTree.Head!.Number + 1 : TestItem.KeccakD;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method,
            selector, new { tracer = NativeNoopTracer.NoopTracer, timeout });

        Assert.That((int?)JToken.Parse(response)["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidParams), response);
    }

    private static IEnumerable<TestCaseData> BlockTimeoutRouteCases()
    {
        foreach (string method in new[] { "debug_traceBlockByNumber", "debug_traceBlockByHash" })
        {
            foreach (string scenario in new[] { "native", "javascript", "empty-native", "empty-javascript", "javascript-valid", "native-null-config", "native-null-config-valid" })
                foreach (bool streamMode in new[] { false, true })
                    yield return new TestCaseData(scenario, streamMode, scenario.EndsWith("-valid", StringComparison.Ordinal) ? "valid" : "bad", method);
            foreach (string scenario in new[] { "opcode", "empty-opcode", "unmatched-opcode" })
                foreach (string duration in new[] { "bad", "valid", "type", "oversized" })
                    foreach (bool streamMode in new[] { false, true })
                        yield return new TestCaseData(scenario, streamMode, duration, method);
        }
        foreach (string scenario in new[] { "native", "javascript", "empty-native", "empty-javascript" })
            foreach (bool streamMode in new[] { false, true })
                yield return new TestCaseData(scenario, streamMode, "bad", "debug_traceBlock");
    }

    [TestCaseSource(nameof(BlockTimeoutRouteCases))]
    public async Task Debug_traceBlock_defers_timeout_per_transaction_without_changing_routes(string scenario, bool streamMode, string duration, string method) =>
        await AssertBlockTimeoutRoute(scenario, streamMode, duration, method, "bad");

    [TestCase("native", false)]
    [TestCase("opcode", false)]
    [TestCase("opcode", true)]
    [TestCase("javascript", false)]
    [TestCase("empty-native", false)]
    [TestCase("empty-opcode", true)]
    [TestCase("empty-javascript", false)]
    [TestCase("unmatched-opcode", false)]
    [TestCase("unmatched-opcode", true)]
    [TestCase("javascript-valid", false)]
    [TestCase("native-null-config", false)]
    [TestCase("native-null-config-valid", false)]
    [TestCase("native", false, "2562047.788015215502222222h")]
    [TestCase("opcode", false, "2562047.788015215502222222h")]
    [TestCase("opcode", true, "2562047.788015215502222222h")]
    [TestCase("empty-native", false, "2562047.788015215502222222h")]
    [TestCase("empty-opcode", true, "2562047.788015215502222222h")]
    public async Task Debug_traceBlockByNumber_defers_timeout_per_transaction_without_changing_routes(string scenario, bool streamMode, string timeout = "bad") =>
        await AssertBlockTimeoutRoute(scenario, streamMode, scenario.EndsWith("-valid", StringComparison.Ordinal) ? "valid" : "bad",
            "debug_traceBlockByNumber", timeout);

    private static async Task AssertBlockTimeoutRoute(string scenario, bool streamMode, string duration, string method, string timeout)
    {
        using Context context = await Context.Create();
        bool empty = scenario.StartsWith("empty", StringComparison.Ordinal);
        bool unmatched = scenario.StartsWith("unmatched", StringComparison.Ordinal);
        Block block = empty
            ? await context.Blockchain.AddBlock()
            : await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        bool valid = duration == "valid";
        Dictionary<string, object> options = new()
        {
            ["timeout"] = duration switch { "valid" => "5s", "type" => 7, "oversized" => new string('x', 65), _ => timeout },
            ["streamMode"] = streamMode
        };
        if (unmatched) options["txHash"] = TestItem.KeccakD;
        if (scenario.Contains("native", StringComparison.Ordinal)) options["tracer"] = NativeNoopTracer.NoopTracer;
        if (scenario.Contains("javascript", StringComparison.Ordinal))
            options["tracer"] = "{fault:function(){},result:function(){return {};}}";
        if (scenario.StartsWith("native-null-config", StringComparison.Ordinal))
        {
            options["tracer"] = "callTracer";
            options["tracerConfig"] = JsonSerializer.Deserialize<JsonElement>("""{"onlyTopCall":true,"onlyTopCall":null,"withLog":null}""");
        }

        object selector = method switch
        {
            "debug_traceBlockByNumber" => block.Number,
            "debug_traceBlockByHash" => block.Hash!,
            _ => Rlp.Encode(block).ToString()
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector, options);
        JToken json = JToken.Parse(response);
        if (duration is "type" or "oversized")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidParams), response);
                Assert.That(json["result"], Is.Null, response);
            }
        }
        else if (empty || unmatched)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"], Is.Null, response);
                Assert.That((json["result"] as JArray)?.Count, Is.Zero, response);
            }
        }
        else if (scenario == "javascript")
        {
            Assert.That(json["error"], Is.Null, response);
            JArray traces = (JArray)json["result"]!;
            Assert.That(traces, Has.Count.EqualTo(block.Transactions.Length));
            foreach (JToken trace in traces)
                Assert.That((string?)trace["error"], Does.Contain($"Cannot parse '{timeout}' as a duration"), response);
        }
        else if (valid)
        {
            Assert.That(json["error"], Is.Null, response);
            JArray traces = (JArray)json["result"]!;
            Assert.That(traces, Has.Count.EqualTo(block.Transactions.Length), response);
            for (int i = 0; i < traces.Count; i++)
                Assert.That((string?)traces[i]["txHash"], Is.EqualTo(block.Transactions[i].Hash!.ToString()), response);
        }
        else if (method == "debug_traceBlock")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidParams), response);
                Assert.That(json["result"], Is.Null, response);
                Assert.That((string?)json["error"]?["message"], Is.EqualTo("invalid argument 1"), response);
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
                Assert.That(json["result"], Is.Null, response);
                Assert.That((string?)json["error"]?["message"], Does.Contain($"Cannot parse '{timeout}' as a duration"), response);
            }
        }
    }

    private static IEnumerable<TestCaseData> BlockJavaScriptRecoveryCases()
    {
        (string Failure, int FailAt, int? SelectedAt)[] cases =
        [
            ("result", 0, null),
            ("result", 1, null),
            ("result", 2, null),
            ("null", 0, null),
            ("undefined", 0, null),
            ("42", 0, null),
            ("step", 0, null),
            ("enter", 0, null),
            ("fault", 0, null),
            ("fault", 1, null),
            ("exit", 0, null),
            ("exit", 1, null),
            ("postStep", 0, null),
            ("postStep", 1, null),
            ("precedence", 0, null),
            ("precedence", 1, null),
            ("stack-input", 0, null),
            ("stack-input", 1, null),
            ("slice-input", 0, null),
            ("slice-input", 1, null),
            ("uint-input", 0, null),
            ("uint-input", 1, null),
            ("slice-padding", 0, null),
            ("slice-padding", 1, null),
            ("setup", -1, null),
            ("missing", -1, null),
            ("noncallable", -1, null),
            ("unknown", -1, null),
            ("syntax", -1, null),
            ("result", 1, 1),
            ("step", 2, 2),
            ("result", 0, 1),
            ("unknown", -1, 2),
            ("result", 0, -1)
        ];

        foreach (string method in new[] { "debug_traceBlockByNumber", "debug_traceBlockByHash", "debug_traceBlock" })
        {
            foreach ((string failure, int failAt, int? selectedAt) in cases)
            {
                string? scenario = (failure, failAt, selectedAt) switch
                {
                    ("result", 1, 1) => "txHash_recovers_middle_result_failure",
                    ("step", 2, 2) => "txHash_recovers_last_step_failure",
                    ("result", 0, 1) => "txHash_selected_success_keeps_canonical_index_and_state",
                    ("unknown", -1, 2) => "txHash_reports_constructor_failure_for_selected_transaction",
                    ("result", 0, -1) => "unknown_txHash_returns_no_trace",
                    _ => null
                };
                TestCaseData testCase = new(method, failure, failAt, selectedAt);
                yield return scenario is null ? testCase : testCase.SetName($"{nameof(Debug_traceBlock_recovers_js_errors_without_replaying_prefixes)}_{method}_{scenario}");
            }
        }
    }

    [TestCaseSource(nameof(BlockJavaScriptRecoveryCases))]
    public async Task Debug_traceBlock_recovers_js_errors_without_replaying_prefixes(
        string method, string failure, int failAt, int? selectedAt)
    {
        JsRecoveryObservations observations = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder.AddSingleton(observations).AddDecorator<ITransactionProcessor, JsRecoveryProcessor>());
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        byte[] runtime = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).PushData(1).Op(Instruction.ADD)
            .PushData(0).Op(Instruction.SSTORE).STOP().Done;
        await chain.AddBlock(Build.A.Transaction.WithNonce(nonce).WithCode(Prepare.EvmCode.ForInitOf(runtime).Done)
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        Address contract = observations.Contract = ContractAddress.From(TestItem.AddressB, nonce);
        ulong firstNonce = nonce + 1;
        Address failingWrapper = ContractAddress.From(TestItem.AddressB, firstNonce + (ulong)Math.Max(failAt, 0));
        ulong failingExecutionNonce = firstNonce + (ulong)Math.Max(failAt, 0) + 1;
        byte[] call = Prepare.EvmCode.Call(contract, 100_000).STOP().Done;
        byte[] faultingCall = Prepare.EvmCode.Call(contract, 100_000).Op(Instruction.INVALID).Done;
        Transaction[] transactions = Enumerable.Range(0, 3).Select(index => Build.A.Transaction.WithNonce(firstNonce + (ulong)index)
            .WithCode(failure == "fault" && index == failAt ? faultingCall : call).WithValue((UInt256)(index + 1)).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject).ToArray();
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(3));
        string resultBody = $$"""
            return {index:ctx.txIndex,storage:toHex(db.getState(toAddress('{{contract}}'),toWord('0x0'))),
                    nonce:db.getNonce(ctx.from),balance:db.getBalance(ctx.from).toString()};
            """;
        string baselineTracer = "{fault:function(){},result:function(ctx,db){" + resultBody + "}}";
        observations.Enabled = true;
        string baselineJson = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash!, new { tracer = baselineTracer });
        JToken baseline = JToken.Parse(baselineJson);
        Assert.That(baseline["error"], Is.Null, baselineJson);
        JsRecoveryState[] expectedStates = observations.States.ToArray();
        Assert.That(expectedStates, Has.Length.EqualTo(3));
        UInt256[] expectedStorage = Enumerable.Range(0, 3)
            .Select(index => (UInt256)(index + 1 - (failure == "fault" && index >= failAt ? 1 : 0))).ToArray();
        Assert.That(expectedStates.Select(state => state.Storage), Is.EqualTo(expectedStorage));
        observations.States.Clear();
        string? invalidRead = failure switch
        {
            "stack-input" => "log.stack.peek(-1)",
            "slice-input" => "log.memory.slice(-1,1)",
            "uint-input" => "log.memory.getUint(-1)",
            "slice-padding" => "log.memory.slice(0,1048577)",
            _ => null
        };
        string tracer = failure switch
        {
            "result" => "{fault:function(){},result:function(ctx,db){if(ctx.txIndex===" + failAt + ")throw Error('result failure');" + resultBody + "}}",
            "null" or "undefined" or "42" => "{fault:function(){},result:function(ctx,db){if(ctx.txIndex===" + failAt + ")throw " + failure + ";" + resultBody + "}}",
            "step" or "postStep" => "{fault:function(){}," + failure + ":function(log,db){if(db.getNonce(toAddress('" + TestItem.AddressB + "'))===" + failingExecutionNonce + ")throw Error('" + failure + " failure');},result:function(ctx,db){" + resultBody + "}}",
            "enter" => "{fault:function(){},enter:function(frame){if(toHex(frame.getFrom())===toHex(toAddress('" + failingWrapper + "')))throw Error('enter failure');},exit:function(){},result:function(ctx,db){" + resultBody + "}}",
            "exit" => "{fault:function(){},enter:function(frame){this.fail=toHex(frame.getFrom())===toHex(toAddress('" + failingWrapper + "'));},exit:function(){if(this.fail)throw Error('exit failure');},result:function(ctx,db){" + resultBody + "}}",
            "fault" => "{fault:function(){throw Error('fault failure');},result:function(ctx,db){" + resultBody + "}}",
            "precedence" => "{fault:function(){throw Error('later fault failure');},enter:function(){},exit:function(){if(this.fail)throw Error('later exit failure');},step:function(log,db){if(this.fail)throw Error('later step failure');if(db.getNonce(toAddress('" + TestItem.AddressB + "'))===" + failingExecutionNonce + "){this.fail=true;throw Error('first failure');}},result:function(ctx,db){if(this.fail)throw Error('later result failure');" + resultBody + "}}",
            _ when invalidRead is not null => "{fault:function(){},step:function(log,db){if(db.getNonce(toAddress('" + TestItem.AddressB + "'))===" + failingExecutionNonce + "){try{" + invalidRead + ";}catch(e){}throw Error('input abort was swallowed');}},result:function(ctx,db){" + resultBody + "}}",
            "setup" => "{fault:function(){},setup:function(){throw Error('setup failure');},result:function(){return {};}}",
            "missing" => "{fault:function(){}}",
            "noncallable" => "{fault:function(){},result:42}",
            "unknown" => "unknown-js-tracer",
            _ => "{result:}"
        };
        Hash256 selectedHash = selectedAt >= 0 ? block.Transactions[selectedAt.Value].Hash! : TestItem.KeccakA;
        object traceOptions = selectedAt is null ? new { tracer } : new { tracer, txHash = selectedHash };
        object blockSelector = method switch
        {
            "debug_traceBlockByHash" => block.Hash!,
            "debug_traceBlock" => Rlp.Encode(block).Bytes,
            _ => block.Number
        };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, blockSelector, traceOptions);
        JToken actual = JToken.Parse(response);
        Assert.That(actual["error"], Is.Null, response);
        JArray entries = (JArray)actual["result"]!;
        JsRecoveryState[] observedStates = observations.States.ToArray();
        JsRecoveryState[] replayedStates = selectedAt >= 0 ? expectedStates[..(selectedAt.Value + 1)] : expectedStates;
        int expectedCount = selectedAt is null ? 3 : selectedAt >= 0 ? 1 : 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Has.Count.EqualTo(expectedCount));
            Assert.That(observedStates, Is.EqualTo(replayedStates), selectedAt is null
                ? "each transaction executes once with canonical storage, nonce and balance"
                : "each executed transaction uses canonical storage, nonce and balance");
        }
        for (int index = 0; index < entries.Count; index++)
        {
            int transactionIndex = selectedAt ?? index;
            using (Assert.EnterMultipleScope())
            {
                Assert.That((string?)entries[index]["txHash"], Is.EqualTo(block.Transactions[transactionIndex].Hash!.ToString()));
                if (failAt < 0 || transactionIndex == failAt)
                {
                    Assert.That((string?)entries[index]["error"], Is.Not.Null.And.Not.Empty, response);
                    if (method == "debug_traceBlock") Assert.That(entries[index]["result"], Is.Null);
                    else Assert.That(entries[index]["result"]?.Type, Is.EqualTo(JTokenType.Null));
                    if (invalidRead is not null)
                    {
                        string expectedError = failure switch
                        {
                            "stack-input" => "tracer accessed out of bound stack",
                            "slice-padding" => "reached limit for padding memory slice",
                            _ => "tracer accessed out of bound memory"
                        };
                        Assert.That((string?)entries[index]["error"], Does.Contain(expectedError).And.Not.Contain("input abort was swallowed"));
                    }
                    if (failure is "fault" or "exit" or "postStep" or "precedence")
                        Assert.That((string?)entries[index]["error"], Does.Contain(failure == "precedence" ? "first failure" : failure + " failure"));
                }
                else Assert.That(JToken.DeepEquals(entries[index], baseline["result"]![transactionIndex]), Is.True, response);
            }
        }
        if (selectedAt is not null) return;

        observations.Enabled = false;
        string legacy = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceTransaction",
            block.Transactions[Math.Max(failAt, 0)].Hash!, new { tracer });
        Assert.That(JToken.Parse(legacy)["error"], Is.Not.Null, "single-transaction tracing retains its existing error behavior");
        string native = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, blockSelector,
            new { tracer = "callTracer", tracerConfig = new { onlyTopCall = "not-a-boolean" } });
        Assert.That(JToken.Parse(native)["error"], Is.Not.Null, "native tracer failures remain top-level");
    }

    [Test]
    public async Task JavaScript_log_input_errors_are_uncatchable_only_in_capture_mode(
        [Values("stack.peek(-1)", "stack.peek(2147483647)", "memory.slice(-1,1)", "memory.slice(1,0)",
            "memory.getUint(-1)", "memory.getUint(0)", "memory.getUint(2147483647)")] string read, [Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{fault:function(){},step:function(log){try{log." + read + ";}catch(e){this.caught=true;}},result:function(){return this.caught===true;}}", capture);
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        if (capture) Assert.That(result.TraceError, Does.Contain("tracer accessed out of bound"));
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TraceError, Is.Null);
                Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("true"));
            }
        }
    }

    [Test]
    public async Task JavaScript_log_valid_bounds_preserve_values([Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        string equalBounds = capture
            ? "if(log.memory.slice(-1,-1).length!==0 || log.memory.slice(2147483648,2147483648).length!==0)throw Error('equal bounds');"
            : "";
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain, """
            {fault:function(){},step:function(log){
                if(log.stack.peek(0).toString()!=='3' || log.memory.getUint(0).toString()!=='7')throw Error('word');
                if(log.memory.slice(0,32).length!==32 || log.memory.slice(32,33)[0]!==0 || log.memory.slice(0,0).length!==0)throw Error('slice');
            """ + equalBounds + "},result:function(){return true;}}", capture);
        byte[] memory = new byte[32];
        memory[^1] = 7;
        byte[] stack = new byte[32];
        stack[^1] = 3;
        tracer.SetOperationMemory(new TraceMemory(32, memory));
        tracer.SetOperationStack(new TraceStack(stack));
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TraceError, Is.Null);
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("true"));
        }
    }

    [TestCase(0, 0, 0)]
    [TestCase(0, 0, 1)]
    [TestCase(32, 32, 0)]
    [TestCase(32, 32, 1)]
    [TestCase(32, 0, 0)]
    [TestCase(32, 0, 1)]
    [TestCase(32, 16, 0)]
    [TestCase(32, 16, 1)]
    public async Task JavaScript_log_memory_padding_limit(int memorySize, int backingSize, int excess)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        int end = memorySize + MemorySizes.MiB + excess;
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{fault:function(){},step:function(log){this.first=log.memory.slice(0,1)[0];this.bytes=log.memory.slice(" + (end - 1) + "," + end + ");},result:function(){return {length:this.bytes.length,first:this.first,last:this.bytes[0]};}}");
        byte[] memory = new byte[backingSize];
        if (backingSize != 0) memory[0] = 7;
        tracer.SetOperationMemory(new TraceMemory((ulong)memorySize, memory));
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            if (excess != 0)
            {
                Assert.That(result.TraceError, Is.EqualTo("reached limit for padding memory slice: 1048577"));
                Assert.That(result.CustomTracerResult, Is.Null);
            }
            else
            {
                Assert.That(result.TraceError, Is.Null);
                JToken payload = JToken.Parse(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions));
                Assert.That((int?)payload["length"], Is.EqualTo(1));
                Assert.That((int?)payload["first"], Is.EqualTo(backingSize == 0 ? 0 : 7));
                Assert.That((int?)payload["last"], Is.EqualTo(0));
            }
        }
    }

    [Test]
    public async Task JavaScript_log_word_reads_use_logical_memory_size(
        [Values(MemorySizes.MiB - 32, MemorySizes.MiB)] int offset, [Values(0, 32)] int backingSize)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{fault:function(){},step:function(log){this.word=log.memory.getUint(" + offset + ").toString();},result:function(){return this.word;}}");
        tracer.SetOperationMemory(new TraceMemory((ulong)offset + EvmPooledMemory.WordSize, new byte[backingSize]));
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TraceError, Is.Null);
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("\"0\""));
        }
    }

    [Test]
    public async Task JavaScript_public_interrupt_escapes_full_tracer_recovery(
        [Values] bool inputError, [Values("throw Error('later JS failure');", "throw null;", "return 1;")] string completion)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        bool actionCompleted = false;
        Action interrupt = () =>
        {
            if (inputError) engine.AbortInput("invalid API input");
            engine.Interrupt();
            actionCompleted = true;
        };
        using (ScriptObject installer = (ScriptObject)engine.CreateTracer("{install:function(action){globalThis.interruptForTest=action;}}"))
            installer.InvokeMethod("install", interrupt);
        using GethLikeJavaScriptTxTracer txTracer = CreateTracer(engine, chain,
            "{fault:function(){},result:function(){interruptForTest();" + completion + "}}");
        Transaction transaction = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        List<GethLikeTxTrace> completed = [];
        inner.StartNewTxTrace(transaction).Returns(txTracer);
        inner.BuildResult().Returns(completed);
        inner.When(tracer => tracer.EndTxTrace()).Do(_ => completed.Add(txTracer.BuildResult()));
        using RecoveringJavaScriptBlockTracer tracer = new(() => inner, null);
        tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(transaction).TestObject);
        using ITxTracer active = tracer.StartNewTxTrace(transaction);
        ScriptInterruptedException failure = Assert.Throws<ScriptInterruptedException>(() => tracer.EndTxTrace())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actionCompleted, Is.True, "both requests must be recorded before returning to JS");
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.False);
            Assert.That(completed, Is.Empty, "neither tracer layer may turn cancellation into an error entry");
        }
    }

    [Test]
    public void JavaScript_input_abort_never_overrides_public_interrupt([Range(0, 2)] int externalOrder)
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{result:function(action){try{action();}catch(e){}return 'swallowed';}}");
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        bool reached = false;
        bool completed = false;
        JavaScriptInputException? ownedInput = null;
        Action abort = () =>
        {
            reached = true;
            if (externalOrder == 1) engine.Interrupt();
            ownedInput = engine.AbortInput("invalid API input");
            if (externalOrder == 2) engine.Interrupt();
        };
        Exception failure = Assert.Catch(() =>
        {
            engine.InvokeCapturingNull(receiver, callback, abort, null, false, out _);
            engine.ThrowIfInputFailed();
            completed = true;
        })!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reached, Is.True);
            Assert.That(completed, Is.False, "the input failure must survive JS try/catch");
            Assert.That(engine.TryGetInputError(failure, out _), Is.EqualTo(externalOrder == 0));
            Assert.That(engine.TryGetInputError(new JavaScriptInputException("foreign input marker"), out _), Is.False);
            Assert.That(engine.TryGetInputError(new ArgumentOutOfRangeException("host state"), out _), Is.False);
            Assert.That(engine.TryGetInputError(new ScriptEngineException("host", new ArgumentOutOfRangeException("state")), out _), Is.False);
            Assert.That(ownedInput, Is.Not.Null);
            Assert.That(engine.TryGetInputError(CreateException(true, true, null, ownedInput), out _), Is.False);
            Assert.That(engine.TryGetInputError(new OperationCanceledException(), out _), Is.False);
        }
        engine.Interrupt();
        Assert.That(engine.TryGetInputError(failure, out _), Is.False);
    }

    [Test]
    public void JavaScript_input_abort_stops_a_loop_after_js_catch()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{result:function(action){try{action();}catch(e){}while(true){}}}");
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(5));
        using CancellationTokenRegistration registration = watchdog.Token.Register(engine.Interrupt);
        Action abort = () => engine.AbortInput("invalid API input");
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, callback, abort, null, false, out _))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.TryGetInputError(failure, out string? message), Is.True);
            Assert.That(message, Is.EqualTo("invalid API input"));
        }
    }

    [Test]
    public void JavaScript_host_argument_errors_remain_unrecoverable()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{result:function(action){action();}}");
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        Action host = () => throw new ArgumentOutOfRangeException("state");
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, callback, host, null, false, out _))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.TryGetInputError(failure, out _), Is.False);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.False);
        }
    }

    [Test]
    public async Task Debug_traceBlockByNumber_mux_preserves_javascript_result_errors([Values] bool nested)
    {
        using Context ctx = await Context.Create();
        const string script = "{fault:function(){},result:function(){throw Error('mux result failure');}}";
        Dictionary<string, object> children = new() { [script] = new { }, ["noopTracer"] = new { } };
        object config = nested ? new Dictionary<string, object> { ["muxTracer"] = children } : children;

        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceBlockByNumber", "latest",
            new { tracer = "muxTracer", tracerConfig = config });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        JArray entries = (JArray)json["result"]!;
        Assert.That(entries, Is.Not.Empty);
        foreach (JToken entry in entries)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((string?)entry["error"] ?? "", Does.Contain("mux result failure"), response);
                Assert.That(entry["result"]?.Type, Is.EqualTo(JTokenType.Null), response);
            }
        }
    }

    [Test]
    public void Debug_traceBlockByNumber_recovery_result_ownership([Values] bool transfer)
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject,
            Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject
        ];
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        ITxTracer first = Substitute.For<ITxTracer>();
        ITxTracer second = Substitute.For<ITxTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(first, second);
        List<GethLikeTxTrace> completed = [];
        IDisposable resource = Substitute.For<IDisposable>();
        inner.BuildResult().Returns(completed);
        inner.When(tracer => tracer.EndTxTrace()).Do(_ =>
        {
            if (completed.Count != 0) throw new JavaScriptTraceFailure(new ArgumentException("result failed"));
            completed.Add(new GethLikeTxTrace(resource) { TxHash = transactions[0].Hash });
        });
        IReadOnlyCollection<GethLikeTxTrace>? results = null;
        using (RecoveringJavaScriptBlockTracer tracer = new(() => inner, null))
        {
            tracer.StartNewBlockTrace(block);
            foreach (Transaction transaction in transactions)
            {
                using ITxTracer tx = tracer.StartNewTxTrace(transaction);
                tracer.EndTxTrace();
            }
            if (transfer) results = tracer.BuildResult();
        }
        first.Received(1).Dispose();
        second.Received(1).Dispose();
        ((IDisposable)inner).Received(1).Dispose();
        if (transfer)
        {
            resource.DidNotReceive().Dispose();
            Assert.That(results!.Last().TraceError, Is.EqualTo("result failed"));
            foreach (GethLikeTxTrace trace in results!) trace.Dispose();
        }
        resource.Received(1).Dispose();
    }

    [Test]
    public void Debug_traceBlockByNumber_recovery_adapter_can_be_reused([Values] bool constructionError, [Values] bool transfer)
    {
        Transaction firstTransaction = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
        Transaction secondTransaction = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;
        IBlockTracer<GethLikeTxTrace> firstInner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        IBlockTracer<GethLikeTxTrace> secondInner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        IDisposable firstResource = Substitute.For<IDisposable>();
        IDisposable secondResource = Substitute.For<IDisposable>();
        firstInner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(NullTxTracer.Instance);
        secondInner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(NullTxTracer.Instance);
        firstInner.BuildResult().Returns(constructionError ? Array.Empty<GethLikeTxTrace>()
            : new[] { new GethLikeTxTrace(firstResource) { TxHash = firstTransaction.Hash } });
        secondInner.BuildResult().Returns(new[] { new GethLikeTxTrace(secondResource) { TxHash = secondTransaction.Hash } });
        int created = 0;
        IReadOnlyCollection<GethLikeTxTrace>? firstResult = null;
        IReadOnlyCollection<GethLikeTxTrace> secondResult;
        using (RecoveringJavaScriptBlockTracer tracer = new(() => ++created == 1
            ? constructionError ? throw new JavaScriptTraceFailure(new ArgumentException("first construction failed")) : firstInner
            : secondInner, null))
        {
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(firstTransaction).TestObject);
            using (tracer.StartNewTxTrace(firstTransaction)) tracer.EndTxTrace();
            if (transfer) firstResult = tracer.BuildResult();
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(secondTransaction).TestObject);
            if (!constructionError)
            {
                ((IDisposable)firstInner).Received(1).Dispose();
                if (transfer) firstResource.DidNotReceive().Dispose();
                else firstResource.Received(1).Dispose();
            }
            using (tracer.StartNewTxTrace(secondTransaction)) tracer.EndTxTrace();
            secondResult = tracer.BuildResult();
            Assert.That(secondResult, Has.Count.EqualTo(1));
            Assert.That(secondResult.Single().TxHash, Is.EqualTo(secondTransaction.Hash));
            Assert.That(secondResult.Single().TraceError, Is.Null);
            if (firstResult is not null)
            {
                Assert.That(firstResult.Single().TxHash, Is.EqualTo(firstTransaction.Hash));
                Assert.That(firstResult.Single().TraceError, Is.EqualTo(constructionError ? "first construction failed" : null));
            }
            tracer.Dispose();
            tracer.Dispose();
        }
        Assert.That(created, Is.EqualTo(2));
        ((IDisposable)firstInner).Received(constructionError ? 0 : 1).Dispose();
        ((IDisposable)secondInner).Received(1).Dispose();
        secondResource.DidNotReceive().Dispose();
        foreach (GethLikeTxTrace trace in secondResult) trace.Dispose();
        if (firstResult is not null)
        {
            firstResource.DidNotReceive().Dispose();
            foreach (GethLikeTxTrace trace in firstResult) trace.Dispose();
        }
        if (!constructionError) firstResource.Received(1).Dispose();
        secondResource.Received(1).Dispose();
    }

    [Test]
    public void Debug_traceBlockByNumber_recovery_does_not_contain_host_or_cancellation_errors(
        [Values("construction", "start", "end")] string phase, [Values] bool cancellation)
    {
        Exception failure = cancellation ? new OperationCanceledException() : new ScriptEngineException("host failure", new InvalidOperationException("state failed"));
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        if (phase == "start") inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(_ => throw failure);
        if (phase == "end") inner.When(tracer => tracer.EndTxTrace()).Do(_ => throw failure);
        using RecoveringJavaScriptBlockTracer tracer = new(() => phase == "construction" ? throw failure : inner, null);
        Block block = Build.A.Block.TestObject;
        Action run = () =>
        {
            tracer.StartNewBlockTrace(block);
            using ITxTracer tx = tracer.StartNewTxTrace(Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject);
            tracer.EndTxTrace();
        };
        Assert.That(Assert.Catch(run), Is.SameAs(failure));
    }

    private sealed class JsRecoveryObservations
    {
        internal bool Enabled;
        internal Address Contract = Address.Zero;
        internal ConcurrentQueue<JsRecoveryState> States { get; } = new();
    }

    private readonly record struct JsRecoveryState(Hash256? Hash, UInt256 Storage, ulong Nonce, UInt256 Balance);

    private sealed class JsRecoveryProcessor(ITransactionProcessor inner, IWorldState state, JsRecoveryObservations observations) : ITransactionProcessor
    {
        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
        {
            TransactionResult result = inner.Process(transaction, txTracer, options);
            if (observations.Enabled && transaction.SenderAddress == TestItem.AddressB && !options.HasFlag(ExecutionOptions.Warmup))
            {
                state.Get(new StorageCell(observations.Contract, 0), out UInt256 storage);
                observations.States.Enqueue(new(transaction.Hash, storage, state.GetNonce(TestItem.AddressB), state.GetBalance(TestItem.AddressB)));
            }
            return result;
        }
        public void SetBlockExecutionContext(BlockHeader blockHeader) => inner.SetBlockExecutionContext(blockHeader);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);
    }

    private static IEnumerable<TestCaseData> ReplayAvailabilityCases()
    {
        (string Method, bool UsesRlp)[] methods =
        [
            ("debug_traceBlockByHash", false),
            ("debug_traceBlockByNumber", false),
            ("debug_traceBlock", true),
            ("debug_traceTransactionInBlockByHash", true),
            ("debug_traceTransactionInBlockByIndex", true)
        ];
        bool[] values = [false, true];
        string[] suffixes = ["00", "c0"];
        foreach ((string method, bool usesRlp) in methods)
            foreach (bool streaming in values)
                foreach (bool parentAvailable in values)
                {
                    yield return new TestCaseData(method, streaming, parentAvailable, true, false, "");
                    if (method == "debug_traceBlockByNumber")
                    {
                        yield return new TestCaseData(method, streaming, parentAvailable, false, false, "");
                        yield return new TestCaseData(method, streaming, parentAvailable, true, true, "");
                    }
                    if (usesRlp)
                        foreach (string suffix in suffixes)
                            yield return new TestCaseData(method, streaming, parentAvailable, true, false, suffix);
                }
    }

    [TestCaseSource(nameof(ReplayAvailabilityCases))]
    public async Task Debug_traceBlock_checks_replay_parent_state(
        string method, bool streaming, bool parentAvailable, bool bodyAvailable, bool cachedOnly, string suffix)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Transaction transaction = Build.A.Transaction
            .WithNonce(chain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithTo(TestItem.AddressC).WithValue(1).WithGasLimit(100_000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block processed = await chain.AddBlock(transaction);
        Hash256 retainedRoot = processed.StateRoot!;
        if (!parentAvailable)
        {
            BlockHeader unavailableHeader = parent.Clone();
            unavailableHeader.StateRoot = TestItem.KeccakA;
            unavailableHeader.Hash = unavailableHeader.CalculateHash();
            Block unavailableParent = new(unavailableHeader, [], []);
            chain.BlockTree.SuggestBlock(unavailableParent, BlockTreeSuggestOptions.ForceDontSetAsMain);
            parent = unavailableHeader;
        }
        Block unprocessed = Build.A.Block.WithParent(parent).WithStateRoot(parentAvailable ? TestItem.KeccakA : retainedRoot)
            .WithTransactions(transaction).TestObject;
        chain.BlockTree.SuggestBlock(unprocessed, BlockTreeSuggestOptions.ForceDontSetAsMain);
        Assert.That(chain.BlockTree.TryUpdateMainChain(unprocessed.Header, true, true, unprocessed), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.StateReader.HasStateForBlock(parent), Is.EqualTo(parentAvailable));
            Assert.That(chain.StateReader.HasStateForBlock(unprocessed.Header), Is.EqualTo(!parentAvailable));
        }
        if (!bodyAvailable || cachedOnly)
        {
            chain.BlockTree.DeleteOldBlock(unprocessed.Number, unprocessed.Hash!);
            if (cachedOnly) chain.Container.Resolve<IBlockStore>().Cache(unprocessed);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(chain.BlockTree.HasBlock(unprocessed.Number, unprocessed.Hash!), Is.False);
                Assert.That(chain.BlockTree.FindBlock(unprocessed.Hash!, BlockTreeLookupOptions.None), cachedOnly ? Is.Not.Null : Is.Null);
                Assert.That(chain.BlockTree.FindHeader(unprocessed.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
                Assert.That(chain.BlockTree.FindHash(unprocessed.Number), Is.EqualTo(unprocessed.Hash));
            }
        }
        object selector = method switch
        {
            "debug_traceBlockByHash" => unprocessed.Hash!,
            "debug_traceBlockByNumber" => unprocessed.Number,
            _ => Rlp.Encode(unprocessed).ToString() + suffix
        };
        object[] parameters = method switch
        {
            "debug_traceTransactionInBlockByHash" => [selector, transaction.Hash!],
            "debug_traceTransactionInBlockByIndex" => [selector, "0x0"],
            _ => [selector]
        };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, parameters);
        JToken actual = JToken.Parse(response);
        if (!bodyAvailable)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual["result"], Is.Null, response);
                Assert.That(actual["error"]?["code"]?.Value<int>(), Is.EqualTo(ErrorCodes.ResourceNotFound));
                Assert.That(actual["error"]?["message"]?.Value<string>(), Is.EqualTo($"block #{unprocessed.Number} not found"));
            }
            return;
        }
        if (method == "debug_traceBlock" && suffix.Length != 0)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual["result"], Is.Null, response);
                Assert.That(actual["error"]?["code"]?.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput));
                Assert.That(actual["error"]?["message"]?.Value<string>(), Does.Contain("decoding block RLP"));
            }
            return;
        }
        if (!parentAvailable)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual["result"], Is.Null, response);
                Assert.That(actual["error"]?["code"]?.Value<int>(), Is.EqualTo(ErrorCodes.ResourceUnavailable));
                Assert.That(actual["error"]?["message"]?.Value<string>(),
                    Is.EqualTo($"No state available for block {parent.Number} ({parent.Hash})"));
            }
            return;
        }
        Assert.That(actual["error"], Is.Null, response);
        JToken trace = actual["result"]!;
        if (method.StartsWith("debug_traceBlock", StringComparison.Ordinal))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(trace.Count(), Is.EqualTo(1));
                Assert.That(trace[0]!["txHash"]!.Value<string>(), Is.EqualTo(transaction.Hash!.ToString()));
            }
            trace = trace[0]!["result"]!;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace["gas"]!.Value<long>(), Is.EqualTo(21_000));
            Assert.That(trace["failed"]!.Value<bool>(), Is.False);
        }
    }

    [Test]
    public async Task Debug_traceBlock_canonical_nonce_override_preserves_replay(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method, [Values] bool stream)
    {
        using Context context = await Context.Create();
        ulong nonce = context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(context.Blockchain.BlockTree.Head!.Header, TestItem.AddressA);
        Transaction transaction = Build.A.Transaction.WithNonce(nonce).WithTo(TestItem.AddressC).WithValue(1)
            .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = await context.Blockchain.AddBlock(transaction);
        object selector = method == "debug_traceBlockByHash" ? block.Hash! : block.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new GethTraceOptions
            {
                StreamMode = stream,
                StateOverrides = new Dictionary<Address, AccountOverride> { [TestItem.AddressA] = new() { Nonce = nonce + 1 } }
            });
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, response);
            Assert.That((json["result"] as JArray)?.Count, Is.EqualTo(1), response);
            Assert.That((bool?)json["result"]?[0]?["result"]?["failed"], Is.False, response);
            Assert.That((long?)json["result"]?[0]?["result"]?["gas"], Is.EqualTo(21_000), response);
        }
    }

    private static IEnumerable<TestCaseData> SuppliedSignedNonceCases()
    {
        foreach (int nonceDelta in new[] { -1, 0, 1 })
            foreach (bool revert in nonceDelta == 0 ? new[] { false, true } : new[] { false })
                foreach (bool selectTransaction in new[] { false, true })
                    foreach (bool overrideNonce in new[] { false, true })
                        yield return new TestCaseData("debug_traceBlock", nonceDelta, revert, selectTransaction, overrideNonce, false, false);

        foreach (int nonceDelta in new[] { 0, 1 })
            yield return new TestCaseData("debug_traceBlock", nonceDelta, false, false, false, true, false);

        foreach (string method in new[] { "debug_traceTransactionInBlockByHash", "debug_traceTransactionInBlockByIndex" })
            foreach (int nonceDelta in new[] { -1, 1 })
                foreach (bool stream in new[] { false, true })
                    yield return new TestCaseData(method, nonceDelta, false, false, false, stream, false);

        foreach (string method in new[] { "debug_traceBlock", "debug_traceTransactionInBlockByHash", "debug_traceTransactionInBlockByIndex" })
            foreach (int nonceDelta in new[] { -1, 1 })
                foreach (bool stream in new[] { false, true })
                    foreach (bool selectTransaction in method == "debug_traceBlock" ? new[] { false, true } : new[] { false })
                        yield return new TestCaseData(method, nonceDelta, false, selectTransaction, false, stream, true);
    }

    [TestCaseSource(nameof(SuppliedSignedNonceCases))]
    public async Task Debug_traceBlock_supplied_signed_nonce_is_validated(
        string method, int nonceDelta, bool revert, bool selectTransaction, bool overrideNonce, bool stream, bool invalidPrefix)
    {
        using Context context = await Context.Create();
        Block parent = context.Blockchain.BlockTree.Head!;
        ulong stateNonce = context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(parent.Header, TestItem.AddressA);
        ulong effectiveNonce = overrideNonce ? stateNonce + 7 : stateNonce;
        byte[] code = revert ? Prepare.EvmCode.Revert(0, 0).Done : Prepare.EvmCode.STOP().Done;
        Transaction transaction = Build.A.Transaction.WithNonce(invalidPrefix ? effectiveNonce : (ulong)((long)effectiveNonce + nonceDelta))
            .WithCode(code).WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction[] transactions = invalidPrefix
            ? [Build.A.Transaction.WithNonce((ulong)((long)effectiveNonce + nonceDelta)).WithTo(TestItem.AddressC)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject, transaction]
            : [transaction];
        (Block original, Block supplied) = await CreateSuppliedBlock(context, transactions);
        string rlp = Rlp.Encode(supplied).ToString();
        GethTraceOptions options = new()
        {
            Tracer = stream ? string.Empty : NativeCallTracer.CallTracer,
            StreamMode = stream,
            TxHash = selectTransaction ? transaction.Hash : null,
            StateOverrides = overrideNonce
                ? new Dictionary<Address, AccountOverride> { [TestItem.AddressA] = new() { Nonce = effectiveNonce } }
                : null
        };
        object[] parameters = method switch
        {
            "debug_traceTransactionInBlockByHash" => [rlp, transaction.Hash!, options],
            "debug_traceTransactionInBlockByIndex" => [rlp, invalidPrefix ? 1 : 0, options],
            _ => [rlp, options]
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, parameters);
        JToken json = JToken.Parse(response);
        Assert.That(context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(original.Header, TestItem.AddressA), Is.EqualTo(stateNonce),
            "read-only tracing must not persist a sender nonce change");

        if (stream && method != "debug_traceBlock")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(json["error"], Is.Null, response);
                Assert.That((bool?)json["result"]?["failed"], Is.True, response);
                Assert.That((int?)json["result"]?["errorCode"], Is.EqualTo(ErrorCodes.InvalidInput), response);
                Assert.That((string?)json["result"]?["error"] ?? string.Empty,
                    Does.Contain(nonceDelta > 0 ? "nonce too high" : "nonce too low"), response);
            }
        }
        else if (stream)
        {
            Assert.That(json["error"], Is.Null, response);
            if (nonceDelta != 0)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That((int?)json["result"]?.Last?["errorCode"], Is.EqualTo(ErrorCodes.InvalidInput), response);
                    Assert.That((string?)json["result"]?.Last?["error"] ?? string.Empty,
                        Does.Contain(nonceDelta > 0 ? "nonce too high" : "nonce too low"), response);
                }
            }
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That((json["result"] as JArray)?.Count, Is.EqualTo(1), response);
                    Assert.That((bool?)json["result"]?[0]?["result"]?["failed"], Is.False, response);
                    Assert.That((long?)json["result"]?[0]?["result"]?["gas"], Is.GreaterThan(0), response);
                }
            }
        }
        else if (nonceDelta != 0)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
                Assert.That((string?)json["error"]?["message"] ?? string.Empty,
                    Does.Contain(nonceDelta > 0 ? "nonce too high" : "nonce too low"), response);
                Assert.That(json["result"], Is.Null, response);
            }
        }
        else
        {
            Assert.That(json["error"], Is.Null, response);
            JToken frame = json["result"]![0]!["result"]!;
            Assert.That((string?)frame["value"], Is.EqualTo("0x1"), response);
            if (revert)
                Assert.That((string?)frame["error"], Is.EqualTo("execution reverted"), "an EVM REVERT is a trace result, not transaction rejection");
            else
                Assert.That((string?)frame["to"], Is.EqualTo(ContractAddress.From(TestItem.AddressA, effectiveNonce).ToString()), response);
        }
    }

    [TestCase("debug_traceTransactionInBlockByHash", false)]
    [TestCase("debug_traceTransactionInBlockByHash", true)]
    [TestCase("debug_traceTransactionInBlockByIndex", false)]
    [TestCase("debug_traceTransactionInBlockByIndex", true)]
    public async Task Supplied_transaction_trace_preserves_validation_boundary(string method, bool stream)
    {
        using Context context = await Context.Create();
        Block parent = context.Blockchain.BlockTree.Head!;
        ulong nonce = context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(parent.Header, TestItem.AddressA);
        Transaction target = Build.A.Transaction.WithNonce(nonce).WithTo(TestItem.AddressC).WithValue(1)
            .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction trailing = Build.A.Transaction.WithNonce(nonce + 2).WithTo(TestItem.AddressC).WithValue(1)
            .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        (Block original, Block supplied) = await CreateSuppliedBlock(context, target, trailing);
        object selector = method == "debug_traceTransactionInBlockByHash" ? target.Hash! : 0;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method,
            Rlp.Encode(supplied).ToString(), selector, new GethTraceOptions { StreamMode = stream });
        JToken json = JToken.Parse(response);
        Assert.That(context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(original.Header, TestItem.AddressA), Is.EqualTo(nonce));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json["error"], Is.Null, response);
            Assert.That((bool?)json["result"]?["failed"], Is.False, response);
            Assert.That((long?)json["result"]?["gas"], Is.EqualTo(21_000), response);
        }
    }

    private static async Task<(Block Original, Block Supplied)> CreateSuppliedBlock(Context context, params Transaction[] transactions)
    {
        Block original = await context.Blockchain.AddBlock();
        Block supplied = new(original.Header.Clone(), transactions, original.Uncles, original.Withdrawals);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(original.Transactions, Is.Empty);
            Assert.That(supplied.Hash, Is.EqualTo(original.Hash), "the supplied body deliberately disagrees with the empty stored header");
        }
        return (original, supplied);
    }

    [Test]
    public async Task Debug_traceBlock_SuppliedBody_DoesNotUseIndexedHeaderState()
    {
        IParallelBlockTracer parallel = Substitute.For<IParallelBlockTracer>();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton(parallel));
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block original = await chain.AddBlock(transactions);
        GethTraceOptions options = new() { Tracer = "prestateTracer" };
        string baseline = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", original.Hash, options);
        Assert.That(parallel.ReceivedCalls(), Is.Not.Empty, "the RPC tracer must be wired to the indexed path for verified blocks");
        parallel.ClearReceivedCalls();
        Transaction[] changed = (Transaction[])original.Transactions.Clone();
        changed[0] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce)
            .WithValue(100).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block supplied = new(original.Header.Clone(), changed, original.Uncles, original.Withdrawals);

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);
        JToken expected = JToken.Parse(baseline);
        JToken actual = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual["error"], Is.Null);
            Assert.That(parallel.ReceivedCalls(), Is.Empty, "an unverified body must never open indexed prefixes");
            Assert.That(supplied.Hash, Is.EqualTo(original.Hash));
        }
        string sender = TestItem.AddressB.ToString();
        UInt256 before = Bytes.FromHexString(expected["result"]![1]!["result"]![sender]!["balance"]!.Value<string>()!).ToUInt256();
        UInt256 after = Bytes.FromHexString(actual["result"]![1]!["result"]![sender]!["balance"]!.Value<string>()!).ToUInt256();
        Assert.That(after, Is.EqualTo(before - 99), "the second transaction must see the modified first transfer, not the indexed prefix");
    }

    [TestCase("debug_traceBlockByHash")]
    [TestCase("debug_traceBlockByNumber")]
    public async Task Debug_traceBlock_preimage_results_preserve_transaction_hash(string method)
    {
        using Context context = await Context.Create();
        ulong nonce = context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(context.Blockchain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction transaction = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce)
            .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await context.Blockchain.AddBlock(transaction);
        object selector = method == "debug_traceBlockByHash" ? block.Hash! : block.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer = "keccak256PreimageTracer" });
        JToken result = JToken.Parse(response);
        Assert.That(result["error"], Is.Null, response);
        Assert.That(result["result"]!.Count(), Is.EqualTo(block.Transactions.Length));
        for (int i = 0; i < block.Transactions.Length; i++)
            Assert.That(result["result"]![i]!["txHash"]!.Value<string>(), Is.EqualTo(block.Transactions[i].Hash!.ToString()));
    }

    // The supplied body keeps the stored header's hash, and the new header has no stored receipts: both must number
    // logs from the body that runs, not from receipts looked up by hash.
    [TestCase(false, false, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_under_stored_hash")]
    [TestCase(true, false, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_under_unstored_hash")]
    [TestCase(true, true, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_after_reverted_tx")]
    [TestCase(false, false, true, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_filtered_to_one_tx")]
    public async Task Debug_traceBlock_callTracer_log_index_follows_supplied_body(bool unstoredHeader, bool revertFirst, bool filterToSecond)
    {
        using Context context = await Context.Create();

        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        static Transaction LoggingTransaction(ulong txNonce, int logs, bool revert = false)
        {
            Prepare code = Prepare.EvmCode;
            for (int i = 0; i < logs; i++) code = code.Log(0, 0);
            code = revert ? code.Revert(0, 0) : code.STOP();
            return Build.A.Transaction.WithNonce(txNonce).WithCode(code.Done).WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        }

        Block original = await context.Blockchain.AddBlock(LoggingTransaction(nonce, 1), LoggingTransaction(nonce + 1, 1));
        BlockHeader header = original.Header.Clone();
        if (unstoredHeader)
        {
            header.ExtraData = [0x01];
            header.Hash = header.CalculateHash();
        }
        Block supplied = new(header, [LoggingTransaction(nonce, 2, revertFirst), original.Transactions[1]], original.Uncles, original.Withdrawals);

        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}"""),
            TxHash = filterToSecond ? supplied.Transactions[1].Hash : null
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);
        if (filterToSecond)
        {
            string single = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionInBlockByHash",
                Rlp.Encode(supplied).ToString(), supplied.Transactions[1].Hash, options with { TxHash = null });
            Assert.That((string)JToken.Parse(single)["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x2"), single);
        }

        JToken result = JToken.Parse(response)["result"]!;
        if (filterToSecond)
        {
            Assert.That((string)result.Single()["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x2"), response);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0]!["result"]!["logs"]?.Select(static log => (string)log["index"]!), revertFirst ? Is.Null : Is.EqualTo(new[] { "0x0", "0x1" }), response);
            Assert.That((string)result[1]!["result"]!["logs"]![0]!["index"]!, Is.EqualTo(revertFirst ? "0x0" : "0x2"), response);
        }
    }

    [Test]
    public async Task Debug_traceBlock_callTracer_filtered_supplied_body_stops_after_target()
    {
        using Context context = await Context.Create();

        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction target = Build.A.Transaction.WithNonce(nonce).WithCode(Prepare.EvmCode.Log(0, 0).STOP().Done).WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block original = await context.Blockchain.AddBlock(target);
        Transaction belowIntrinsicGas = Build.A.Transaction.WithNonce(nonce + 1).WithTo(TestItem.AddressC).WithGasLimit(1000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block supplied = new(original.Header.Clone(), [target, belowIntrinsicGas], original.Uncles, original.Withdrawals);

        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}"""),
            TxHash = target.Hash
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);

        Assert.That((string)JToken.Parse(response)["result"]!.Single()["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x0"), response);
    }

    [Test]
    public Task Debug_traceBlock_opcode_logger_limit_resets_per_transaction([Values] bool streamMode) =>
        AssertOpcodeStepLimit("debug_traceBlockByNumber", streamMode, 1);

    [Test]
    public Task Debug_traceBlock_opcode_logger_limit_counts_steps(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values] bool streamMode, [Values(0, 2, 4, 10, 20)] int limit) =>
        AssertOpcodeStepLimit(method, streamMode, limit);

    [Test]
    public Task Debug_traceBlock_opcode_logger_step_limit_preserves_buffered_byte_cap(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method) =>
        AssertOpcodeStepLimit(method, streamMode: false, limit: 4, capBufferedTrace: true);

    private static async Task AssertOpcodeStepLimit(string method, bool streamMode, int limit, bool capBufferedTrace = false)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object selector = method == "debug_traceBlockByNumber" ? "latest" : block.Hash!;
        string unlimited = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector, new { streamMode });
        if (capBufferedTrace) context.Blockchain.Container.Resolve<IJsonRpcConfig>().MaxBufferedTraceLogSize = 1;
        string limited = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector, new { streamMode, limit });

        JToken expected = JToken.Parse(unlimited);
        JArray transactions = (JArray)expected["result"]!;
        Assert.That(transactions, Has.Count.EqualTo(2));
        foreach (JToken transaction in transactions)
        {
            JArray entries = (JArray)transaction["result"]!["structLogs"]!;
            Assert.That(entries.Count, Is.GreaterThan(1));
            int expectedLimit = capBufferedTrace ? 1 : limit;
            if (expectedLimit > 0)
                while (entries.Count > expectedLimit)
                    entries.RemoveAt(entries.Count - 1);
        }

        Assert.That(JToken.DeepEquals(JToken.Parse(limited), expected), Is.True, limited);
    }

    [Test]
    public async Task Debug_traceBlock_native_tracer_streams_the_buffered_json(
        [Values("debug_traceBlock", "debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values(NativeCallTracer.CallTracer, NativePrestateTracer.PrestateTracer)] string tracer)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object blockParameter = method switch
        {
            "debug_traceBlock" => Rlp.Encode(block).ToString(),
            "debug_traceBlockByNumber" => block.Number,
            _ => block.Hash!
        };

        using JsonRpcResponse response = await RpcTest.TestRequest(context.DebugRpcModule, method, blockParameter, new { tracer, streamMode = true });
        string streamed = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, blockParameter, new { tracer, streamMode = true });
        string buffered = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, blockParameter, new { tracer, streamMode = false });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.TryGetStreamableResult(out IStreamableResult? result) ? result : null, Is.InstanceOf<GethLikeTxTraceStreamingBlockResult>(), "precondition: the block is streamed");
            Assert.That((JArray)JToken.Parse(streamed)["result"]!, Has.Count.EqualTo(block.Transactions.Length), streamed);
            Assert.That(streamed, Is.EqualTo(buffered));
        }
    }

    [Test]
    public async Task Debug_traceBlock_native_tracer_with_malformed_config_fails_before_streaming(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values(NativeCallTracer.CallTracer, NativePrestateTracer.PrestateTracer)] string tracer)
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object blockParameter = method == "debug_traceBlockByNumber" ? block.Number : block.Hash!;
        // Each tracer rejects the mistyped field of its own config and ignores the other one.
        var tracerConfig = new { withLog = "yes", diffMode = "yes" };

        JToken response = JToken.Parse(await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, blockParameter, new { tracer, tracerConfig, streamMode = true }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["error"], Is.Not.Null, response.ToString());
            Assert.That(response["result"], Is.Null, response.ToString());
        }
    }

    [Test]
    public async Task Debug_traceBlock_empty_body_keeps_outer_rlp_strictness(
        [Values("empty", "empty-list", "extra-field", "valid")] string inputKind, [Values] bool streaming)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = streaming }).Build();
        Block block = await chain.AddBlock();
        byte[] input = inputKind switch
        {
            "empty" => [],
            "empty-list" => [0xc0],
            _ => Rlp.Encode(block).Bytes
        };
        if (inputKind == "extra-field")
        {
            RlpReader reader = new(input);
            int contentLength = reader.ReadSequenceLength();
            byte[] expanded = new byte[Rlp.LengthOfSequence(contentLength + 1)];
            RlpWriter writer = new(expanded);
            writer.StartSequence(contentLength + 1);
            input.AsSpan(reader.Position).CopyTo(expanded.AsSpan(writer.Position));
            expanded[^1] = 0x80;
            input = expanded;
        }
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlock", input);
        JToken json = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            if (inputKind == "valid")
            {
                Assert.That(json["error"], Is.Null, response);
                Assert.That((JArray)json["result"]!, Is.Empty, response);
            }
            else
            {
                Assert.That((int?)json["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
                Assert.That((string?)json["error"]?["message"], Does.Contain("decoding block RLP"), response);
                Assert.That(json["result"], Is.Null, response);
            }
        }
    }

    [Test]
    public async Task Debug_traceBlock_with_invalid_rlp()
    {
        using Context context = await Context.Create();

        const string expected = """{"jsonrpc":"2.0","error":{"code":-32602,"message":"invalid argument 0"},"id":67}""";
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", "xxx");

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlock(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        string rlp = Rlp.Encode(context.Blockchain.BlockTree.Head).ToString();
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", rlp, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlockByNumber(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        ulong blockNumber = context.Blockchain.BlockTree.Head!.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", blockNumber, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlockByHash(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        Hash256? blockHash = context.Blockchain.BlockTree.Head!.Hash;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByHash", blockHash, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [Test]
    public async Task Debug_traceBlockByHash_does_not_fail_bal_validation_when_tracing_osaka_block()
    {
        using Context context = await Context.Create(new TestSingleReleaseSpecProvider(Osaka.Instance));

        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        Hash256 blockHash = context.Blockchain.BlockTree.Head!.Hash!;
        JsonRpcResponse response = await RpcTest.TestRequest(
            context.DebugRpcModule,
            "debug_traceBlockByHash",
            blockHash,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer });

        RpcTest.AssertSuccess<IReadOnlyCollection<GethLikeTxTrace>>(response);
    }

    private static IEnumerable<TestCaseData> TraceBlockSource()
    {
        Func<TestRpcBlockchain, Transaction[]> transactions = CreateTraceBlockTransactions;

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions(),
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "gas": 87700,
                            "failed": false,
                            "returnValue": "0x",
                            "structLogs": [
                                { "pc": 0, "op": "PUSH32", "gas": 46536, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 33, "op": "PUSH1", "gas": 46533, "gasCost": 3, "depth": 1, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000"] },
                                { "pc": 35, "op": "MSTORE", "gas": 46530, "gasCost": 6, "depth": 1, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000", "0x0"] },
                                { "pc": 36, "op": "PUSH32", "gas": 46524, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 69, "op": "PUSH1", "gas": 46521, "gasCost": 3, "depth": 1, "stack": ["0x0"] },
                                { "pc": 71, "op": "PUSH1", "gas": 46518, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x6"] },
                                { "pc": 73, "op": "PUSH1", "gas": 46515, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x6", "0x0"] },
                                { "pc": 75, "op": "CREATE2", "gas": 46512, "gasCost": 32006, "depth": 1, "stack": ["0x0", "0x6", "0x0", "0x0"] },
                                { "pc": 0, "op": "PUSH1", "gas": 14280, "gasCost": 3, "depth": 2, "stack": [] },
                                { "pc": 2, "op": "PUSH1", "gas": 14277, "gasCost": 3, "depth": 2, "stack": ["0x0"] },
                                { "pc": 4, "op": "SSTORE", "gas": 14274, "gasCost": 2200, "depth": 2, "stack": ["0x0", "0x20"], "storage": { "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000000000" } },
                                { "pc": 5, "op": "STOP", "gas": 12074, "gasCost": 0, "depth": 2, "stack": [] },
                                { "pc": 76, "op": "STOP", "gas": 12300, "gasCost": 0, "depth": 1, "stack": ["0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"] }
                            ]
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "gas": 56141,
                            "failed": false,
                            "returnValue": "0x",
                            "structLogs": [
                                { "pc": 0, "op": "PUSH1", "gas": 46480, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 2, "op": "PUSH1", "gas": 46477, "gasCost": 3, "depth": 1, "stack": ["0x0"] },
                                { "pc": 4, "op": "PUSH1", "gas": 46474, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0"] },
                                { "pc": 6, "op": "PUSH1", "gas": 46471, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0"] },
                                { "pc": 8, "op": "PUSH1", "gas": 46468, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0"] },
                                { "pc": 10, "op": "PUSH20", "gas": 46465, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0"] },
                                { "pc": 31, "op": "PUSH3", "gas": 46462, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"] },
                                { "pc": 35, "op": "CALL", "gas": 46459, "gasCost": 45774, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837", "0x186a0"] },
                                { "pc": 36, "op": "STOP", "gas": 43859, "gasCost": 0, "depth": 1, "stack": ["0x1"] }
                            ]
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with blockMemoryTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": [46536,46533,46530,46524,46521,46518,46515,46512,14280,14277,14274,12074,12300],
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": [46480,46477,46474,46471,46468,46465,46462,46459,43859],
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with javaScriptTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {},
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {},
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativeNoopTracer.NoopTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {},
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {},
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeNoopTracer.NoopTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "type": "CREATE",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0x15694",
                            "input": "0x7f60006020550000000000000000000000000000000000000000000000000000006000527f0000000000000000000000000000000000000000000000000000000000000000600660006000f500",
                            "calls": [
                                {
                                    "type": "CREATE2",
                                    "from": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                                    "to": "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837",
                                    "value": "0x0",
                                    "gas": "0x37c8",
                                    "gasUsed": "0x89e",
                                    "input": "0x600060205500"
                                }
                            ]
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "type": "CREATE",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x6b5887043de753ecfa6269f947129068263ffbe2",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0xdb4d",
                            "input": "0x600060006000600060007328156f6fdeeffd5667d51bb8d7d5069a920e0837620186a0f100",
                            "calls": [
                                {
                                    "type": "CALL",
                                    "from": "0x6b5887043de753ecfa6269f947129068263ffbe2",
                                    "to": "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837",
                                    "value": "0x0",
                                    "gas": "0xa8a6",
                                    "gasUsed": "0x0",
                                    "input": "0x"
                                }
                            ]
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeCallTracer.CallTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                                "balance": "0x3635c9adc5de9f09e5",
                                "nonce": 3,
                                "code": "0xabcd",
                                "codeHash": "0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0xf618"
                            }
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                                "balance": "0x3635c9adc5de9db350",
                                "nonce": 4,
                                "code": "0xabcd",
                                "codeHash": "0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0x24cac"
                            },
                            "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837": {
                                "balance": "0x0",
                                "nonce": 1
                            }
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativePrestateTracer.PrestateTracer };
    }

    private static Transaction[] CreateTraceBlockTransactions(TestRpcBlockchain blockchain)
    {
        ulong nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        byte[] contract = Prepare.EvmCode
            .PushData(0)
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        byte[] salt = new byte[32];
        byte[] deployment = Prepare.EvmCode
            .Create2(contract, salt, 0)
            .Op(Instruction.STOP)
            .Done;

        Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);
        Address deploymentAddress = ContractAddress.From(deployingContractAddress, salt, contract);

        byte[] call = Prepare.EvmCode
            .Call(deploymentAddress, 100000)
            .Op(Instruction.STOP)
            .Done;

        return
        [
            Build.A.Transaction
                .WithNonce(nonce)
                .WithCode(deployment)
                .WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject,

            Build.A.Transaction
                .WithNonce(nonce + 1)
                .WithCode(call)
                .WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject,
        ];
    }

    [Test]
    public Task GethLikeTxTraceStreamingResult_WriteToAsync_produces_same_json_as_serializer(
        [Values(1, 100, 1000)] int traceCount, [Values] bool errors) =>
        AssertBlockResultSerialization(traceCount, errors, includeErrorResult: false);

    [Test]
    public Task GethLikeTxTraceStreamingResult_specified_errors_include_null_results(
        [Values(1, 100, 1000)] int traceCount, [Values] bool errors) =>
        AssertBlockResultSerialization(traceCount, errors, includeErrorResult: true);

    private static async Task AssertBlockResultSerialization(int traceCount, bool errors, bool includeErrorResult)
    {
        List<GethLikeTxTrace> traces = new(traceCount);
        for (int i = 0; i < traceCount; i++)
        {
            GethLikeTxTrace trace = new();
            trace.TxHash = new Core.Crypto.Hash256(Keccak.Compute(i.ToString()).Bytes);
            if (errors && i % 2 == 0) trace.TraceError = "tracer failure";
            trace.Entries.Add(new GethTxTraceEntry { ProgramCounter = i, Opcode = "STOP", Gas = 21000, GasCost = 0, Depth = 1 });
            traces.Add(trace);
        }

        using GethLikeTxTraceStreamingResult result = new(traces) { IncludeErrorResult = includeErrorResult };

        string streamedJson = await StreamToStringAsync(result);

        string stjJson = JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(streamedJson).RootElement,
            JsonDocument.Parse(stjJson).RootElement),
            $"Streamed JSON differs from serializer output for {traceCount} traces");
        using GethLikeTxTraceCollection roundTrip = JsonSerializer.Deserialize<GethLikeTxTraceCollection>(streamedJson, EthereumJsonSerializer.JsonOptions)!;
        Assert.That(roundTrip.Traces.Select(trace => trace.TraceError), Is.EqualTo(traces.Select(trace => trace.TraceError)));
        if (errors)
        {
            JToken first = JArray.Parse(streamedJson)[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That((string?)first["error"], Is.EqualTo("tracer failure"));
                if (includeErrorResult) Assert.That(first["result"]?.Type, Is.EqualTo(JTokenType.Null));
                else Assert.That(first["result"], Is.Null);
            }
        }
    }

    [Test]
    public async Task Streamed_call_trace_nested_to_max_call_depth_serializes([Values] bool blockResult)
    {
        using NativeCallTracerCallFrame root = new() { Type = Instruction.CALL, Error = "a<b" };
        NativeCallTracerCallFrame current = root;
        for (int depth = 1; depth < VirtualMachineStatics.MaxCallDepth; depth++)
        {
            NativeCallTracerCallFrame child = new() { Type = Instruction.CALL };
            current.Calls.Add(child);
            current = child;
        }

        GethLikeTxTrace trace = new() { TxHash = TestItem.KeccakA, CustomTracerResult = new GethLikeCustomTrace { Value = root } };
        using IDisposable result = blockResult
            ? new GethLikeTxTraceStreamingBlockResult(
                (writer, _, _) => JsonSerializer.Serialize(writer, trace, EthereumJsonSerializer.JsonOptions),
                new CancellationTokenSource(),
                LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>())
            : new GethLikeTxTraceStreamingResult([trace]);

        string streamedJson = await StreamToStringAsync((IStreamableResult)result);

        using JsonDocument document = JsonDocument.Parse(streamedJson, new JsonDocumentOptions { MaxDepth = EthereumJsonSerializer.DefaultMaxDepth });
        Assert.That(streamedJson, Does.Contain("\"error\":\"a<b\""), "string values must use the serializer's encoder");
    }

    private static async Task<string> StreamToStringAsync(IStreamableResult result)
    {
        // remove buffer limit hits from the equation by using an unbounded Pipe
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        await result.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string streamedJson = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);
        return streamedJson;
    }

    [Test]
    public async Task Debug_traceBlock_returns_error_for_genesis(
        [Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method,
        [Values(null, "bad")] string? timeout)
    {
        using Context context = await Context.Create();

        object? arg = method switch
        {
            "debug_traceBlockByNumber" => context.Blockchain.BlockTree.Genesis!.Number,
            _ => context.Blockchain.BlockTree.Genesis!.Hash
        };

        object options = timeout is null ? new GethTraceOptions() : new { tracer = NativeNoopTracer.NoopTracer, timeout };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, arg, options);

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;

        Assert.That(root.TryGetProperty("error", out JsonElement error), Is.True, "Missing 'error' field");
        Assert.That(error.GetProperty("message").GetString(), Is.EqualTo("genesis is not traceable"));
        Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(-32000));
    }

    [TestCase("debug_traceBlock", -32602)]
    [TestCase("debug_traceBlockByNumber", -32000)]
    [TestCase("debug_traceBlockByHash", -32000)]
    public async Task Debug_traceBlock_routes_reject_malformed_timeout_before_stream_commit(string method, int expectedCode)
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        object? argument = method switch
        {
            "debug_traceBlock" => Rlp.Encode(context.Blockchain.BlockTree.Head).ToString(),
            "debug_traceBlockByNumber" => context.Blockchain.BlockTree.Head!.Number,
            _ => context.Blockchain.BlockTree.Head!.Hash
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, argument,
            new { timeout = "bad", streamMode = true });
        using JsonDocument document = JsonDocument.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.TryGetProperty("result", out _), Is.False);
            Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(expectedCode));
        }
    }

    [Test]
    public async Task Debug_traceBlock_json_rpc_request_returns_valid_json([Values("debug_traceBlock", "debug_traceBlockByNumber", "debug_traceBlockByHash")] string method)
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        object? arg = method switch
        {
            "debug_traceBlock" => Rlp.Encode(context.Blockchain.BlockTree.Head).ToString(),
            "debug_traceBlockByNumber" => context.Blockchain.BlockTree.Head!.Number,
            _ => context.Blockchain.BlockTree.Head!.Hash
        };

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, arg, new GethTraceOptions());

        TestContext.Out.WriteLine(response);

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;

        Assert.That(root.TryGetProperty("result", out JsonElement resultArray), Is.True, "Missing 'result' field");
        Assert.That(resultArray.ValueKind, Is.EqualTo(JsonValueKind.Array), "'result' must be an array");
        Assert.That(resultArray.GetArrayLength(), Is.GreaterThan(0), "Result array must not be empty");

        foreach (JsonElement entry in resultArray.EnumerateArray())
        {
            Assert.That(entry.TryGetProperty("result", out _), Is.True, "Each entry must have 'result'");
            Assert.That(entry.TryGetProperty("txHash", out _), Is.True, "Each entry must have 'txHash'");
        }
    }


    [Test]
    public async Task Capture_does_not_mask_opaque_getter_engine_failure()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        ScriptEngineException failure = Assert.Throws<ScriptEngineException>(() =>
        {
            using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
                "{fault:function(){},get result(){throw Error('getter failure');}}", true);
        })!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Message, Does.Contain("script exception is pending"));
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.False);
        }
    }

    [Test]
    public async Task Capture_mode_handles_null_only_at_callback_boundaries(
        [Values("setup", "result", "step", "return")] string callback, [Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        GethTraceOptions options = new()
        {
            CaptureJavaScriptErrors = capture,
            Tracer = "{fault:function(){},setup:function(){" + (callback == "setup" ? "throw null;" : "")
                + "},step:function(){" + (callback == "step" ? "throw null;" : "")
                + "},result:function(){" + (callback == "result" ? "throw null;" : "return null;") + "}}"
        };
        GethLikeTxTrace Trace()
        {
            using Engine engine = new(Prague.Instance);
            using GethLikeJavaScriptTxTracer tracer = new(engine, new JavaScriptDb(chain.MainWorldState),
                new JavaScriptContext { TxHash = TestItem.KeccakA }, options);
            if (callback == "step") tracer.SetOperationStack(default);
            return tracer.BuildResult();
        }
        if (capture && callback == "setup")
        {
            JavaScriptTraceFailure failure = Assert.Throws<JavaScriptTraceFailure>(() => { using GethLikeTxTrace result = Trace(); })!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(failure.InnerException, Is.InstanceOf<ScriptEngineException>());
                Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.True);
            }
        }
        else if (capture || callback == "return")
        {
            using GethLikeTxTrace result = Trace();
            Assert.That(result.TraceError is null, Is.EqualTo(callback == "return"));
        }
        else Assert.Throws<ScriptEngineException>(() => Trace());
    }

    [Test]
    public async Task Capture_setup_keeps_input_diagnostics_and_unrecoverable_boundaries(
        [Values("input", "host", "fatal", "cancellation", "interrupt", "input-interrupt")] string kind)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        Action fail = kind switch
        {
            "input" => () => engine.AbortInput("setup input marker"),
            "host" => () => throw new InvalidOperationException("setup host failure"),
            "fatal" => () => throw CreateException(true, true, null),
            "cancellation" => () => throw new OperationCanceledException(),
            "interrupt" => engine.Interrupt,
            _ => () => { engine.AbortInput("setup input marker"); engine.Interrupt(); }
        };
        using (ScriptObject installer = (ScriptObject)engine.CreateTracer("{install:function(action){globalThis.failSetup=action;}}"))
            installer.InvokeMethod("install", fail);
        Exception exception = Assert.Catch(() =>
        {
            using GethLikeJavaScriptTxTracer _ = new(engine, new JavaScriptDb(chain.MainWorldState), new JavaScriptContext(),
                new GethTraceOptions
                {
                    CaptureJavaScriptErrors = true,
                    Timeout = TimeSpan.Zero,
                    Tracer = "{fault:function(){},setup:function(){failSetup();throw null;},result:function(){return {};}}"
                });
        })!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JavaScriptTraceFailure.IsRecoverable(exception), Is.EqualTo(kind == "input"), exception.ToString());
            if (kind == "input")
            {
                Assert.That(exception, Is.TypeOf<JavaScriptTraceFailure>());
                Assert.That(exception.Message, Is.EqualTo("setup input marker"));
                Assert.That(exception.InnerException, Is.Not.Null);
            }
            else Assert.That(exception, Is.Not.TypeOf<JavaScriptTraceFailure>());
        }
    }

    [Test]
    public async Task Result_getter_is_read_once_in_both_modes([Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{reads:0,fault:function(){},get result(){this.reads++;return function(){return this.reads;};}}", capture);
        using GethLikeTxTrace result = tracer.BuildResult();
        Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("1"));
    }

    [Test]
    public async Task Capture_reads_callbacks_in_geth_order_and_retains_result_replaced_by_setup()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain, """
            {
                order:[],
                get result(){this.order.push('result');return function(){return this.order.join(',');};},
                get fault(){this.order.push('fault');return function(){};},
                get step(){this.order.push('step');return function(){};},
                get enter(){this.order.push('enter');},
                get exit(){this.order.push('exit');},
                get postStep(){this.order.push('postStep');},
                get setup(){this.order.push('setup');return function(){
                    this.order.push('setup-call');
                    Object.defineProperty(this,'result',{value:function(){return 'replacement';}});
                };}
            }
            """);
        using GethLikeTxTrace result = tracer.BuildResult();
        Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions),
            Is.EqualTo("\"result,fault,step,enter,exit,postStep,setup,setup-call\""));
    }

    [Test]
    public async Task Capture_retains_a_self_replacing_step_callback()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain, """
            {steps:0,fault:function(){},step:function(){
                this.steps++;this.step=function(){throw Error('replacement');};
            },result:function(){return this.steps;}}
            """);
        tracer.SetOperationStack(default);
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TraceError, Is.Null);
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("2"));
        }
    }

    [Test]
    public async Task Capture_validates_required_callbacks_and_pair_before_reading_setup([Values("result", "fault", "pair")] string missing)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        string properties = missing switch
        {
            "result" => "get fault(){throw Error('fault getter should not run');},",
            "fault" => "result:function(){},",
            _ => "result:function(){},fault:function(){},enter:function(){},"
        };
        string script = "{" + properties + "get setup(){throw Error('setup getter should not run');}}";
        JavaScriptTraceFailure failure = Assert.Throws<JavaScriptTraceFailure>(() => CreateTracer(engine, chain, script))!;
        Assert.That(failure.Message, Does.Contain(missing == "pair" ? "both or none" : "required function " + missing));
    }

    private static GethLikeJavaScriptTxTracer CreateTracer(Engine engine, TestRpcBlockchain chain, string script, bool capture = true) =>
        new(engine, new JavaScriptDb(chain.MainWorldState), new JavaScriptContext { TxHash = TestItem.KeccakA },
            new GethTraceOptions { Tracer = script, CaptureJavaScriptErrors = capture });

    [Test]
    public void Null_throw_is_observed_without_changing_the_exception_value_or_message()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("{result:function(){throw null;}}");
        using ScriptObject callback = (ScriptObject)((ScriptObject)receiver).GetProperty("result");
        bool observed = false;
        ScriptEngineException captured = Assert.Throws<ScriptEngineException>(() =>
            engine.InvokeCapturingNull(receiver, callback, null, null, false, out observed))!;
        ScriptEngineException direct = Assert.Throws<ScriptEngineException>(() => ((dynamic)receiver).result(null))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.True);
            Assert.That(captured.ScriptExceptionAsObject, Is.Null);
            Assert.That(captured.Message, Is.EqualTo(direct.Message));
            Assert.That(JavaScriptTraceFailure.IsRecoverable(captured, observed), Is.True);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(direct), Is.False, "untagged calls retain the conservative classifier");
        }
    }

    [Test]
    public void Null_throw_does_not_mark_later_returned_null_or_host_failure()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("{throwing:function(){throw null;},returned:function(){return null;},host:function(callback){callback();}}");
        using ScriptObject throwing = (ScriptObject)((ScriptObject)receiver).GetProperty("throwing");
        using ScriptObject returned = (ScriptObject)((ScriptObject)receiver).GetProperty("returned");
        using ScriptObject hostCallback = (ScriptObject)((ScriptObject)receiver).GetProperty("host");
        bool observed = false;
        Assert.Throws<ScriptEngineException>(() => engine.InvokeCapturingNull(receiver, throwing, null, null, false, out observed));
        Assert.That(observed, Is.True);
        object? result = engine.InvokeCapturingNull(receiver, returned, null, null, false, out observed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(observed, Is.False);
        }
        Assert.Throws<ScriptEngineException>(() => engine.InvokeCapturingNull(receiver, throwing, null, null, false, out observed));
        Assert.That(observed, Is.True);
        Action host = () => throw new InvalidOperationException("host state failed");
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, hostCallback, host, null, false, out observed))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.False);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observed), Is.False);
        }
    }

    [Test]
    public async Task Captured_step_callbacks_keep_allocation_close_to_legacy()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        const int calls = 1000;
        long Measure(bool capture)
        {
            using Engine engine = new(Prague.Instance);
            using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
                "{count:0,fault:function(){},step:function(){this.count++;},result:function(){return this.count;}}", capture);
            for (int i = 0; i < calls; i++) tracer.SetOperationStack(default);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < calls; i++) tracer.SetOperationStack(default);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            using GethLikeTxTrace result = tracer.BuildResult();
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("2000"));
            return allocated;
        }
        long legacy = Measure(false);
        long captured = Measure(true);
        // Allow bridge bookkeeping without accepting a fresh host delegate on each opcode.
        Assert.That(captured - legacy, Is.LessThanOrEqualTo(128L * calls));
    }

    [Test]
    public void Nested_capture_does_not_mark_the_callers_host_failure()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{throwing:function(){throw null;},outer:function(callback){callback();}}");
        using ScriptObject throwing = (ScriptObject)receiver.GetProperty("throwing");
        using ScriptObject outer = (ScriptObject)receiver.GetProperty("outer");
        Action nested = () =>
        {
            bool innerObserved = false;
            Assert.Throws<ScriptEngineException>(() => engine.InvokeCapturingNull(receiver, throwing, null, null, false, out innerObserved));
            Assert.That(innerObserved, Is.True);
            throw new ScriptEngineException("opaque host failure");
        };
        bool observed = false;
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, outer, nested, null, false, out observed))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.False);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observed), Is.False);
            Assert.That(failure.Message, Does.Contain("opaque host failure"));
        }
    }

    [Test]
    public void Capture_preserves_receiver_getter_count_and_arguments([Values] bool twoArguments)
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("""
            {reads:0,marker:7,get result(){this.reads++;return function(a,b){
                return [this.marker,this.reads,arguments.length,a,b].join(':');
            };}}
            """);
        using ScriptObject callback = (ScriptObject)((ScriptObject)receiver).GetProperty("result");
        object? result = engine.InvokeCapturingNull(receiver, callback, 11, 13, twoArguments, out bool observed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(twoArguments ? "7:1:2:11:13" : "7:1:1:11:"));
            Assert.That(observed, Is.False);
        }
    }

    [Test]
    public void Hostile_callback_cannot_reach_the_private_null_marker([Values] bool hostFailure)
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("""
            {exposed:false,count:0,result:function hostile(argument){
                this.count=hostile.arguments.length;
                const caller=hostile.caller;
                if(caller){
                    this.exposed=true;
                    const args=caller.arguments;
                    for(let i=0;args && i<args.length;i++){
                        if(args[i]!==hostile){try{args[i]();}catch(error){}}
                    }
                }
                if(argument==='ordinary')throw Error('ordinary failure');
                argument();
            }}
            """);
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        Action host = () => throw new InvalidOperationException("host failure");
        object argument = hostFailure ? host : "ordinary";
        bool observed = false;
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, callback, argument, null, false, out observed))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiver.GetProperty("exposed"), Is.EqualTo(false));
            Assert.That(receiver.GetProperty("count"), Is.EqualTo(1));
            Assert.That(observed, Is.False, "caller.arguments must not expose the helper's markNull delegate");
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observed), Is.EqualTo(!hostFailure));
            if (!hostFailure) Assert.That(failure.Message, Does.Contain("ordinary failure"));
        }
    }

    [Test]
    public void Capture_uses_the_original_apply_intrinsic()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("{replace:(Reflect.apply=function(){throw Error('hijacked');}),result:function(){return 42;}}");
        using ScriptObject callback = (ScriptObject)((ScriptObject)receiver).GetProperty("result");
        object? result = engine.InvokeCapturingNull(receiver, callback, null, null, false, out bool observed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(observed, Is.False);
        }
    }

    [Test]
    public void Execution_started_alone_never_classifies_an_engine_failure_as_a_script_throw([Values] bool started)
    {
        ScriptEngineException exception = CreateException(false, started, null);
        Assert.That(JavaScriptTraceFailure.IsRecoverable(exception), Is.False);
    }

    [Test]
    public void Null_marker_never_overrides_fatal_or_host_failures([Values] bool observedNullThrow)
    {
        Exception[] failures =
        [
            CreateException(true, true, null),
            CreateException(true, true, new object()),
            CreateException(false, true, null, new InvalidOperationException("host failure")),
            CreateException(false, true, null, new TargetInvocationException(new InvalidOperationException("host failure"))),
            CreateException(false, true, null, new OperationCanceledException()),
            new ScriptInterruptedException(),
            new OperationCanceledException()
        ];
        using (Assert.EnterMultipleScope())
            foreach (Exception failure in failures)
                Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observedNullThrow), Is.False, failure.ToString());
    }

    private static ScriptEngineException CreateException(bool fatal, bool started, object? scriptValue, Exception? inner = null)
    {
        ConstructorInfo? constructor = typeof(ScriptEngineException).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(bool), typeof(object), typeof(Exception)], null);
        Assert.That(constructor, Is.Not.Null);
        return (ScriptEngineException)constructor!.Invoke(["classifier-test", "failure", "failure", 0, fatal, started, scriptValue, inner]);
    }
}
