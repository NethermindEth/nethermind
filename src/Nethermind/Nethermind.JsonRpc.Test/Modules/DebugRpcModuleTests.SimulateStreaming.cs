// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Test.Modules.Eth;
using Nethermind.JsonRpc.Test.Modules.Eth.Simulate;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ResultType = Nethermind.Core.ResultType;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task DebugSimulateV1_streaming_emits_byte_identical_json_to_buffered()
    {
        // See EthSimulateStreamingTests.Streaming_emits_byte_identical_json_to_buffered_for_serialization_payload
        // for the explanation of why each path needs its own chain instance.
        TestRpcBlockchain bufferedChain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> bufferedPayload = EthSimulateTestsBlocksAndTransactions.CreateSerializationPayload(bufferedChain);
        bufferedChain.BlockTree.UpdateMainChain(new[] { bufferedChain.BlockFinder.Head! }, true, true);
        bufferedChain.BlockTree.UpdateHeadBlock(bufferedChain.BlockFinder.Head!.Hash!);
        string bufferedJson = RunDebugSimulateBuffered(bufferedChain, bufferedPayload);

        TestRpcBlockchain streamingChain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> streamingPayload = EthSimulateTestsBlocksAndTransactions.CreateSerializationPayload(streamingChain);
        streamingChain.BlockTree.UpdateMainChain(new[] { streamingChain.BlockFinder.Head! }, true, true);
        streamingChain.BlockTree.UpdateHeadBlock(streamingChain.BlockFinder.Head!.Hash!);
        string streamingJson = await RunDebugSimulateStreaming(streamingChain, streamingPayload);

        JToken buffered = JToken.Parse(bufferedJson);
        JToken streaming = JToken.Parse(streamingJson);
        Assert.That(JToken.DeepEquals(buffered, streaming), Is.True,
            $"debug_simulateV1 streaming diverged.\nbuffered: {bufferedJson}\nstreaming: {streamingJson}");
    }

    private static string RunDebugSimulateBuffered(TestRpcBlockchain chain, SimulatePayload<TransactionForRpc> payload)
    {
        JsonRpcConfig config = new() { EnableSimulateStreamMode = false };
        SimulateTxExecutor<GethLikeTxTrace> executor = new(
            chain.Bridge,
            chain.BlockFinder,
            config,
            chain.SpecProvider,
            new GethStyleSimulateBlockTracerFactory(GethTraceOptions.Default),
            secondsPerSlot: null,
            logManager: null);
        ResultWrapper<System.Collections.Generic.IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> r =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.That(r.Result.ResultType, Is.EqualTo(ResultType.Success), $"buffered failed: {r.Result.Error}");
        return JsonSerializer.Serialize(r.Data, EthereumJsonSerializer.JsonOptions);
    }

    private static async Task<string> RunDebugSimulateStreaming(TestRpcBlockchain chain, SimulatePayload<TransactionForRpc> payload)
    {
        JsonRpcConfig config = new() { EnableSimulateStreamMode = true };
        SimulateTxExecutor<GethLikeTxTrace> executor = new(
            chain.Bridge,
            chain.BlockFinder,
            config,
            chain.SpecProvider,
            new GethStyleSimulateBlockTracerFactory(GethTraceOptions.Default),
            secondsPerSlot: null,
            logManager: null);
        ResultWrapper<System.Collections.Generic.IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> r =
            executor.Execute(payload, BlockParameter.Latest);
        Assert.That(r.Result.ResultType, Is.EqualTo(ResultType.Success), $"streaming failed: {r.Result.Error}");
        Assert.That(r.Data is IStreamableResult, Is.True);

        IStreamableResult streamable = (IStreamableResult)r.Data!;
        Pipe pipe = new();
        await streamable.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult read = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(read.Buffer.ToArray());
        ((System.IDisposable)r.Data!).Dispose();
        return body;
    }
}
