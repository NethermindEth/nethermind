// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
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

        using JsonRpcResponse response = await RpcTest.TestRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number, options);
        Assert.That(response.TryGetStreamableResult(out IStreamableResult? result) ? result : null,
            Is.InstanceOf<GethLikeTxTraceStreamingBlockResult>());
        string streamed = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number, options);
        Assert.That(parallel.ReceivedCalls(), Is.Not.Empty, "the consumed native stream must use the indexed path");
        string buffered = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { tracer = "callTracer", timeout, streamMode = false });
        Assert.That(streamed, Is.EqualTo(buffered));
        Assert.That((JArray)JToken.Parse(streamed)["result"]!, Has.Count.EqualTo(block.Transactions.Length));
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

        Assert.That((int?)buffered["error"]?["code"], Is.EqualTo(ErrorCodes.InvalidParams), buffered.ToString());
        Assert.That((int?)streamed["error"]?["code"], Is.EqualTo((int?)buffered["error"]?["code"]), streamed.ToString());
        Assert.That((string?)streamed["error"]?["data"], Does.Contain("Cannot convert").And.Not.Contain("Cannot parse 'bad'"));
    }

    [TestCase("json", ErrorCodes.InvalidParams)]
    [TestCase("format", ErrorCodes.InternalError)]
    [TestCase("cancel", ErrorCodes.Timeout)]
    public async Task NativeStreamTimeout_Registered_constructor_failure_precedes_bad_duration(string kind, int expectedCode)
    {
        string tracer = $"native-host-{kind}-{Guid.NewGuid():N}";
        Exception failure = kind switch
        {
            "json" => new JsonException("native host failure"),
            "format" => new FormatException("native host failure"),
            _ => new OperationCanceledException("native host failure")
        };
        GethLikeNativeTracerFactory.RegisterTracer(tracer, (_, _, _, _) => throw failure);
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        JToken response = JToken.Parse(await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { tracer, timeout = "bad", streamMode = true }));

        Assert.That((int?)response["error"]?["code"], Is.EqualTo(expectedCode), response.ToString());
        Assert.That(response["result"], Is.Null);
        Assert.That(response.ToString(), Does.Not.Contain("Cannot parse 'bad'"));
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
}
