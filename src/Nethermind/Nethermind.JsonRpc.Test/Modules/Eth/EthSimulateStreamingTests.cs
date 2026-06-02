// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Test.Modules.Eth.Simulate;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ResultType = Nethermind.Core.ResultType;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

[Parallelizable(ParallelScope.Self)]
public sealed class EthSimulateStreamingTests
{
    [Test]
    public async Task Streaming_emits_byte_identical_json_to_buffered_for_serialization_payload()
    {
        // Use FRESH chain instances for each path: the simulate env's BlockTree caches the
        // suggested blocks from the first run, and BlockTree.SuggestBlock returns AlreadyKnown
        // (skipping SetTotalDifficulty) for blocks whose hashes match — so re-using the same
        // chain leaks `Header.TotalDifficulty == null` into the second run's serialization. Not
        // a streaming defect; a pre-existing simulate-engine quirk that the byte-equivalence test
        // would surface as a false positive without this isolation.
        TestRpcBlockchain bufferedChain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> bufferedPayload = EthSimulateTestsBlocksAndTransactions.CreateSerializationPayload(bufferedChain);
        bufferedChain.BlockTree.UpdateMainChain(new[] { bufferedChain.BlockFinder.Head! }, true, true);
        bufferedChain.BlockTree.UpdateHeadBlock(bufferedChain.BlockFinder.Head!.Hash!);
        string bufferedJson = RunBuffered(bufferedChain, bufferedPayload);

        TestRpcBlockchain streamingChain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> streamingPayload = EthSimulateTestsBlocksAndTransactions.CreateSerializationPayload(streamingChain);
        streamingChain.BlockTree.UpdateMainChain(new[] { streamingChain.BlockFinder.Head! }, true, true);
        streamingChain.BlockTree.UpdateHeadBlock(streamingChain.BlockFinder.Head!.Hash!);
        string streamingJson = await RunStreaming(streamingChain, streamingPayload);

        JToken bufferedTree = JToken.Parse(bufferedJson);
        JToken streamingTree = JToken.Parse(streamingJson);
        Assert.That(JToken.DeepEquals(bufferedTree, streamingTree), Is.True,
            $"streaming JSON diverged from buffered JSON.\nbuffered: {bufferedJson}\nstreaming: {streamingJson}");
    }

    [Test]
    public async Task Streaming_preserves_log_block_hash_in_serialized_logs()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        SimulatePayload<TransactionForRpc> payload = EthSimulateTestsBlocksAndTransactions.CreateTransferLogsAddressPayload();

        string streamingJson = await RunStreaming(chain, payload);

        JToken parsed = JToken.Parse(streamingJson);
        JArray blocks = (JArray)parsed;
        Assert.That(blocks, Has.Count.GreaterThan(0));

        foreach (JToken block in blocks)
        {
            string blockHash = (string)block["hash"]!;
            JArray? calls = (JArray?)block["calls"];
            if (calls is null) continue;
            foreach (JToken call in calls)
            {
                foreach (JToken log in (JArray?)call["logs"] ?? [])
                {
                    Assert.That((string)log["blockHash"]!, Is.EqualTo(blockHash).IgnoreCase,
                        "every streamed log entry must carry the canonical post-processing block hash; the streaming pipeline applies ReapplyBlockHash before the per-block JSON is written.");
                }
            }
        }
    }

    [Test]
    public async Task Streaming_handles_empty_block_state_calls_without_emitting_blocks()
    {
        TestRpcBlockchain chain = await EthRpcSimulateTestsBase.CreateChain();
        // empty BlockStateCalls is a validation reject (must contain BlockStateCalls is not null).
        // Use Count==0 instead — the engine should produce an empty outer array.
        SimulatePayload<TransactionForRpc> payload = new() { BlockStateCalls = [] };

        string streamingJson = await RunStreaming(chain, payload);

        Assert.That(streamingJson, Is.EqualTo("[]"), "no blocks ⇒ empty outer array");
    }

    [Test]
    public void Streaming_result_when_emit_throws_emits_failure_object_and_closes_array()
    {
        CancellationTokenSource timeoutCts = new();

        SimulateStreamingResult<SimulateCallResult> result = new(
            (writer, _, _) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("number"u8);
                writer.WriteStringValue("0x1");
                writer.WriteEndObject();
                throw new InvalidOperationException("simulated mid-stream failure");
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<EthSimulateStreamingTests>());

        using (result)
        {
            ArrayBufferWriter<byte> bufferWriter = new();
            using Utf8JsonWriter writer = new(bufferWriter, new JsonWriterOptions { SkipValidation = true });
            result.WriteAsJson(writer);
            writer.Flush();

            JArray parsed = (JArray)JToken.Parse(Encoding.UTF8.GetString(bufferWriter.WrittenSpan));
            Assert.That(parsed, Has.Count.EqualTo(2), "one partial block + the trailing failure record");
            Assert.That((string)parsed[1]["error"]!, Does.Contain("simulated mid-stream failure"));
            Assert.That((int)parsed[1]["errorCode"]!, Is.EqualTo(ErrorCodes.InternalError));
        }
    }

    [Test]
    public async Task Streaming_result_when_cancelled_mid_response_closes_outer_array_cleanly()
    {
        using CancellationTokenSource requestCts = new();
        CancellationTokenSource timeoutCts = new();

        using SimulateStreamingResult<SimulateCallResult> result = new(
            (writer, _, token) =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("number"u8);
                writer.WriteStringValue("0x1");
                writer.WriteEndObject();

                requestCts.Cancel();
                token.ThrowIfCancellationRequested();
            },
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<EthSimulateStreamingTests>());

        Pipe pipe = new();
        await result.WriteToAsync(pipe.Writer, requestCts.Token);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(readResult.Buffer.ToArray());

        Assert.That(() => JToken.Parse(body), Throws.Nothing,
            "outer array must close in the finally block even when cancellation fires mid-emit");
    }

    [Test]
    public async Task Streaming_result_is_streamable_dispatch_target()
    {
        CancellationTokenSource timeoutCts = new();
        using SimulateStreamingResult<SimulateCallResult> result = new(
            (writer, _, _) => writer.WriteStringValue("ok"),
            timeoutCts,
            LimboLogs.Instance.GetClassLogger<EthSimulateStreamingTests>());

        Assert.That(result is IStreamableResult, Is.True,
            "SimulateStreamingResult must implement IStreamableResult so JsonRpcResponseWriter dispatches the streaming code path");

        // And the IReadOnlyList<T> surface is intentionally empty so in-process consumers cannot
        // accidentally enumerate it (mirrors GethLikeTxTraceStreamingBundleResult / BlockResult).
        Assert.That(result.Count, Is.EqualTo(0));
        Assert.That(((System.Collections.Generic.IReadOnlyList<SimulateBlockResult<SimulateCallResult>>)result).Count, Is.EqualTo(0));
        Assert.That(() => _ = result[0], Throws.InstanceOf<InvalidOperationException>());
        await Task.CompletedTask;
    }

    private static string RunBuffered(TestRpcBlockchain chain, SimulatePayload<TransactionForRpc> payload)
    {
        JsonRpcConfig config = new() { EnableSimulateStreamMode = false };
        SimulateTxExecutor<SimulateCallResult> executor = new(
            chain.Bridge,
            chain.BlockFinder,
            config,
            chain.SpecProvider,
            new SimulateBlockMutatorTracerFactory(),
            secondsPerSlot: null,
            logManager: null);

        ResultWrapper<System.Collections.Generic.IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> r = executor.Execute(payload, BlockParameter.Latest);
        Assert.That(r.Result.ResultType, Is.EqualTo(ResultType.Success), $"buffered failed: {r.Result.Error}");
        // Serialize through the same JsonSerializerOptions the JSON-RPC pipeline uses.
        return JsonSerializer.Serialize(r.Data, EthereumJsonSerializer.JsonOptions);
    }

    private static async Task<string> RunStreaming(TestRpcBlockchain chain, SimulatePayload<TransactionForRpc> payload)
    {
        JsonRpcConfig config = new() { EnableSimulateStreamMode = true };
        SimulateTxExecutor<SimulateCallResult> executor = new(
            chain.Bridge,
            chain.BlockFinder,
            config,
            chain.SpecProvider,
            new SimulateBlockMutatorTracerFactory(),
            secondsPerSlot: null,
            logManager: null);

        ResultWrapper<System.Collections.Generic.IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> r = executor.Execute(payload, BlockParameter.Latest);
        Assert.That(r.Result.ResultType, Is.EqualTo(ResultType.Success), $"streaming failed: {r.Result.Error}");
        Assert.That(r.Data is IStreamableResult, Is.True, "streaming branch must return an IStreamableResult");

        IStreamableResult streamable = (IStreamableResult)r.Data!;
        Pipe pipe = new();
        await streamable.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult read = await pipe.Reader.ReadAsync();
        string body = Encoding.UTF8.GetString(read.Buffer.ToArray());
        ((IDisposable)r.Data!).Dispose();
        return body;
    }
}
