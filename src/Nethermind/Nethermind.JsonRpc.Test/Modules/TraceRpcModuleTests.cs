// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.State.OverridableEnv;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Test.Modules;

public class TraceRpcModuleTests
{
    private class Context
    {
        public async Task Build(ISpecProvider? specProvider = null, bool isAura = false)
        {
            JsonRpcConfig = new JsonRpcConfig();
            Blockchain = await TestRpcBlockchain.ForTest(isAura ? SealEngineType.AuRa : SealEngineType.NethDev).Build(specProvider);

            await Blockchain.AddFunds(TestItem.AddressA, 1000.Ether);
            await Blockchain.AddFunds(TestItem.AddressB, 1000.Ether);
            await Blockchain.AddFunds(TestItem.AddressC, 1000.Ether);

            Hash256 stateRoot = Blockchain.BlockTree.Head!.StateRoot!;
            for (ulong i = 1; i < 10; i++)
            {
                List<Transaction> transactions = [];
                for (ulong j = 0; j < i; j++)
                {
                    transactions.Add(Core.Test.Builders.Build.A.Transaction
                        .WithTo(Address.Zero)
                        .WithNonce(Blockchain.StateReader.GetNonce(Blockchain.BlockTree.Head.Header, TestItem.AddressB) + j)
                        .SignedAndResolved(Blockchain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject);
                }
                await Blockchain.AddBlockMayMissTx(transactions.ToArray());

                stateRoot = Blockchain.BlockTree.Head!.StateRoot!;
            }

            TraceRpcModule = Blockchain.TraceRpcModule;
        }

        public ITraceRpcModule TraceRpcModule { get; private set; } = null!;
        public IJsonRpcConfig JsonRpcConfig { get; private set; } = null!;
        public TestRpcBlockchain Blockchain { get; set; } = null!;

    }

    [Test]
    public async Task Trace_filter_from_genesis_skips_only_genesis(
        [Values("earliest", "0x0")] string fromBlock, [Values] bool streaming, [Values] bool genesisOnly)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        string toBlock = genesisOnly ? "0x0" : "latest";
        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", new { fromBlock, toBlock });
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.That(document.RootElement.TryGetProperty("result", out JsonElement result), Is.True, response);
        if (genesisOnly) Assert.That(result.GetArrayLength(), Is.Zero);
        else
        {
            string expected = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", new { fromBlock = "0x1", toBlock });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response, Is.EqualTo(expected));
                Assert.That(result.GetArrayLength(), Is.GreaterThan(0));
            }
        }
    }

    [Test]
    public async Task Trace_block_and_replay_return_no_traces_for_genesis(
        [Values("trace_block", "trace_replayBlockTransactions")] string method, [Values("earliest", "0x0")] string blockParameter, [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;

        object[] parameters = method == "trace_block" ? [blockParameter] : [blockParameter, new[] { "trace" }];
        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, parameters);
        Assert.That(response, Is.EqualTo("""{"jsonrpc":"2.0","result":[],"id":67}"""));
    }

    [Test]
    public async Task Rejects_unknown_trace_type_as_invalid_params(
        [Values("trace_replayTransaction", "trace_replayBlockTransactions", "trace_call", "trace_callMany", "trace_simulateV1")] string method)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        string[] traceTypes = ["unknown"];
        object transaction = new { from = TestItem.AddressA, to = TestItem.AddressB, gas = "0x186a0" };
        object[] parameters = method switch
        {
            // An unknown hash returns null, so this also checks the types are validated before the lookup.
            "trace_replayTransaction" => [TestItem.KeccakA, traceTypes],
            // Genesis short-circuits replay, so this also checks the types are validated before that.
            "trace_replayBlockTransactions" => ["earliest", traceTypes],
            "trace_call" => [transaction, traceTypes, "latest"],
            "trace_callMany" => [new[] { new object[] { transaction, traceTypes } }, "latest"],
            _ => [new { blockStateCalls = new[] { new { calls = new[] { transaction } } } }, "latest", traceTypes],
        };

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, parameters);
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement error = document.RootElement.GetProperty("error");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams), response);
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo("Invalid trace types"), response);
        }
    }

    // No pending block is built for RPC: pending resolves to the head, so tracing it would silently trace latest.
    [Test]
    public async Task Rejects_pending_block_as_invalid_params(
        [Values("trace_call", "trace_callMany", "trace_block", "trace_replayBlockTransactions", "trace_filter fromBlock", "trace_filter toBlock")] string request,
        [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        string[] traceTypes = ["trace"];
        // NUMBER, so a call evaluated at the head would return the head number.
        object transaction = new { from = TestItem.AddressA, gas = "0x927c0", data = "0x4360005260206000f3" };
        object[] parameters = request switch
        {
            "trace_call" => [transaction, traceTypes, "pending"],
            "trace_callMany" => [new[] { new object[] { transaction, traceTypes } }, "pending"],
            "trace_block" => ["pending"],
            "trace_replayBlockTransactions" => ["pending", traceTypes],
            "trace_filter fromBlock" => [new { fromBlock = "pending", toBlock = "latest" }],
            _ => [new { fromBlock = "0x1", toBlock = "pending" }],
        };

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, request.Split(' ')[0], parameters);
        Assert.That(response, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":{{ErrorCodes.InvalidParams}},"message":"Pending block is not supported for tracing"},"id":67}"""));
    }

    [Test]
    public async Task Trace_call_rejects_a_chain_id_for_another_chain_as_invalid_params(
        [Values("trace_call", "trace_callMany")] string method, [Values("0x0", "0x2")] string type, [Values] bool matching)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        ulong chainId = blockchain.BlockTree.ChainId;
        ulong requestedChainId = matching ? chainId : chainId + 1;
        object transaction = new { type, from = TestItem.AddressA, to = TestItem.AddressB, gas = "0x186a0", chainId = $"0x{requestedChainId:x}" };
        string[] traceTypes = ["trace"];
        object other = new { from = TestItem.AddressA, to = TestItem.AddressC, gas = "0x186a0" };
        object[] parameters = method == "trace_call"
            ? [transaction, traceTypes, "latest"]
            : [new[] { new object[] { other, traceTypes }, new object[] { transaction, traceTypes } }, "latest"];

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, parameters);
        using JsonDocument document = JsonDocument.Parse(response);
        if (matching)
        {
            Assert.That(document.RootElement.TryGetProperty("result", out _), Is.True, response);
            return;
        }

        AssertOtherChainError(document, response, chainId, requestedChainId);
    }

    [Test]
    public async Task Trace_call_rejects_a_chain_id_for_another_chain_before_other_errors(
        [Values("trace_call", "trace_callMany")] string method, [Values("latest", "0x100000")] string blockParameter)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        ulong chainId = blockchain.BlockTree.ChainId;
        string otherChainId = $"0x{chainId + 1:x}";
        // A creation without data fails conversion, and block 0x100000 does not exist.
        object[] parameters = method == "trace_call"
            ? [new { from = TestItem.AddressA, gas = "0x186a0", chainId = otherChainId }, new[] { "trace" }, blockParameter]
            : [new[]
            {
                new object[] { new { from = TestItem.AddressA, gas = "0x186a0" }, new[] { "trace" } },
                new object[] { new { from = TestItem.AddressA, to = TestItem.AddressB, gas = "0x186a0", chainId = otherChainId }, new[] { "trace" } },
            }, blockParameter];

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, parameters);
        using JsonDocument document = JsonDocument.Parse(response);
        AssertOtherChainError(document, response, chainId, chainId + 1);
    }

    private static void AssertOtherChainError(JsonDocument document, string response, ulong chainId, ulong requestedChainId)
    {
        JsonElement error = document.RootElement.GetProperty("error");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams), response);
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo($"invalid chain id (have={chainId}, want={requestedChainId})"), response);
        }
    }

    [Test]
    public async Task Trace_replayBlockTransactions_returns_error_for_missing_block_or_parent([Values] bool parentMissing)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        Block head = blockchain.BlockTree.Head!;
        using ILifetimeScope scope = WithStateAvailability(blockchain, _ => true,
            new MissingHeaderBlockTree(blockchain.BlockTree, head.ParentHash!));
        ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();
        ulong blockNumber = parentMissing ? head.Number : head.Number + 1;

        string response = await RpcTest.TestSerializedRequest(module, "trace_replayBlockTransactions", $"0x{blockNumber:x}", new[] { "trace" });
        Assert.That(response, Is.EqualTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.ResourceNotFound},\"message\":\"{BlockFinderExtensions.HeaderNotFound}\"}},\"id\":67}}"));
    }

    [Test]
    public async Task Trace_filter_returns_error_for_missing_state(
        [Values] bool streaming, [Values(0, 1, 2)] int missingStateOffset)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        long headNumber = (long)blockchain.BlockTree.Head!.Number;
        long missingStateNumber = headNumber - missingStateOffset;
        using ILifetimeScope scope = WithStateAvailability(blockchain,
            header => header.Number != (ulong)missingStateNumber);
        ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();

        // Cover unavailable state at either end of the range and at its initial parent.
        string response = await RpcTest.TestSerializedRequest(module, "trace_filter",
            new { fromBlock = $"0x{headNumber - 1:x}", toBlock = $"0x{headNumber:x}" });
        using JsonDocument document = JsonDocument.Parse(response);
        Assert.That(document.RootElement.TryGetProperty("error", out JsonElement error), Is.True, response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain($"No state available for block {missingStateNumber} "));
            Assert.That(document.RootElement.TryGetProperty("result", out _), Is.False);
        }
    }

    [Test]
    public async Task Trace_filter_reports_reversed_range_before_missing_state()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        ulong headNumber = blockchain.BlockTree.Head!.Number;
        using ILifetimeScope scope = WithStateAvailability(blockchain, _ => false);
        ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();

        string response = await RpcTest.TestSerializedRequest(module, "trace_filter",
            new { fromBlock = $"0x{headNumber:x}", toBlock = $"0x{headNumber - 2:x}" });
        Assert.That(response, Is.EqualTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.InvalidParams},\"message\":\"From block number: {headNumber} is greater than to block number {headNumber - 2}\"}},\"id\":67}}"));
    }

    [Test]
    public async Task Trace_filter_returns_error_for_missing_parent_header([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Block head = blockchain.BlockTree.Head!;
        using ILifetimeScope scope = WithStateAvailability(blockchain, _ => true,
            new MissingHeaderBlockTree(blockchain.BlockTree, head.ParentHash!));
        ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();

        string response = await RpcTest.TestSerializedRequest(module, "trace_filter",
            new { fromBlock = $"0x{head.Number:x}", toBlock = "latest" });
        Assert.That(response, Is.EqualTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.ResourceNotFound},\"message\":\"{BlockFinderExtensions.HeaderNotFound}\"}},\"id\":67}}"));
    }

    private static ILifetimeScope WithStateAvailability(TestRpcBlockchain blockchain, Func<BlockHeader, bool> hasState, IBlockFinder? blockFinder = null)
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(call => hasState(call.Arg<BlockHeader>()));
        return blockchain.Container.BeginLifetimeScope(builder =>
        {
            builder.AddSingleton<IBlockchainBridge>(bridge).AddSingleton<TraceModuleFactory>();
            if (blockFinder is not null) builder.AddSingleton<IBlockFinder>(blockFinder);
        });
    }

    private sealed class MissingHeaderBlockTree(IBlockTree inner, Hash256 missingHash) : BlockTreeTestDouble(inner)
    {
        public override BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
            blockHash == missingHash ? null : base.FindHeader(blockHash, options, blockNumber);
    }

    [Test]
    [NonParallelizable]
    public async Task Trace_filter_observes_timeout_during_state_preflight([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        TimeoutTestHelper.TrackingCancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();
        int stateChecks = 0;
        using ILifetimeScope scope = WithStateAvailability(blockchain, _ =>
        {
            stateChecks++;
            timeout.Cancel();
            return true;
        });
        try
        {
            ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();
            Assert.Throws<OperationCanceledException>(() => module.trace_filter(new TraceFilterForRpc
            {
                FromBlock = new BlockParameter(1),
                ToBlock = BlockParameter.Latest
            }));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stateChecks, Is.EqualTo(1));
                Assert.That(timeout.DisposeCount, Is.EqualTo(1));
            }
        }
        finally
        {
            TimeoutTestHelper.DisposeIfNotAlreadyObserved(timeout);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Trace_filter_keeps_preflight_timeout_for_deferred_execution()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = true;
        TimeoutTestHelper.TrackingCancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();
        try
        {
            using (ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = context.TraceRpcModule.trace_filter(new TraceFilterForRpc
            {
                FromBlock = new BlockParameter(1),
                ToBlock = BlockParameter.Latest
            }))
            {
                Assert.That(timeout.DisposeCount, Is.Zero);
                timeout.Cancel();
                Assert.Throws<OperationCanceledException>(() => result.Data.ToArray());
            }
            Assert.That(timeout.DisposeCount, Is.EqualTo(1));
        }
        finally
        {
            TimeoutTestHelper.DisposeIfNotAlreadyObserved(timeout);
        }
    }

    // The timeout is already cancelled when the result is produced, so executing any block would fail: a filter
    // that can accept nothing more must leave the blocks unexecuted on both the buffered and the streamed path.
    [Test]
    [NonParallelizable]
    public async Task Trace_filter_WhenCountIsZero_ExecutesNoBlock([Values] bool streamed)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = true;
        TimeoutTestHelper.TrackingCancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();
        try
        {
            using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = context.TraceRpcModule.trace_filter(new TraceFilterForRpc
            {
                FromBlock = new BlockParameter(1),
                ToBlock = BlockParameter.Latest,
                Count = 0
            });
            timeout.Cancel();

            if (streamed)
            {
                ArrayBufferWriter<byte> buffer = new();
                using (Utf8JsonWriter writer = new(buffer))
                {
                    ((JsonStreamingResultBase)result.Data).WriteAsJson(writer);
                }

                Assert.That(Encoding.UTF8.GetString(buffer.WrittenSpan), Is.EqualTo("[]"), "no trace is requested, so the stream must finish without executing a block");
            }
            else
            {
                Assert.That(result.Data.ToArray(), Is.Empty, "no trace is requested, so no block may be executed");
            }
        }
        finally
        {
            TimeoutTestHelper.DisposeIfNotAlreadyObserved(timeout);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Trace_filter_disposes_timeout_after_buffered_execution()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = false;
        TimeoutTestHelper.TrackingCancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();
        try
        {
            using ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = context.TraceRpcModule.trace_filter(new TraceFilterForRpc
            {
                FromBlock = new BlockParameter(1),
                ToBlock = BlockParameter.Latest
            });
            Assert.That(timeout.DisposeCount, Is.EqualTo(1));
        }
        finally
        {
            TimeoutTestHelper.DisposeIfNotAlreadyObserved(timeout);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Trace_get_disposes_materialized_stream([Values] bool replayFails)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        IJsonRpcConfig config = blockchain.Container.Resolve<IJsonRpcConfig>();
        config.EnableTracingStreamMode = true;
        // A zero timeout cancels the replay as soon as it starts.
        if (replayFails) config.Timeout = 0;
        using CancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();
        Hash256 txHash = blockchain.BlockTree.Head!.Transactions[0].Hash!;

        if (replayFails)
        {
            Assert.That(() => context.TraceRpcModule.trace_get(txHash, []), Throws.InstanceOf<OperationCanceledException>());
        }
        else
        {
            using ResultWrapper<ParityTxTraceFromStore?> result = context.TraceRpcModule.trace_get(txHash, []);
            Assert.That(result.Data!.TransactionHash, Is.EqualTo(txHash));
        }

        Assert.Throws<ObjectDisposedException>(() => _ = timeout.Token);
    }

    [Test]
    public async Task Transaction_lookups_return_null_for_missing_transaction([Values] bool streaming, [Values] bool pending)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Hash256 txHash = TestItem.KeccakA;
        if (pending)
        {
            Transaction transaction = Build.A.Transaction.WithNonce(blockchain.ReadOnlyState.GetNonce(TestItem.AddressB))
                .WithTo(TestItem.AddressC).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Assert.That(blockchain.TxPool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            txHash = transaction.Hash!;
        }

        const string expected = "{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_transaction", txHash), Is.EqualTo(expected));
            Assert.That(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_get", txHash, Array.Empty<string>()), Is.EqualTo(expected));
            Assert.That(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_get", txHash, new[] { "0x0" }), Is.EqualTo(expected));
            Assert.That(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_replayTransaction", txHash, new[] { "trace" }), Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task Transaction_lookups_keep_errors_for_unavailable_history([Values] UnavailableHistory unavailable)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        Transaction transaction = Build.A.Transaction.WithNonce(blockchain.ReadOnlyState.GetNonce(TestItem.AddressB))
            .WithTo(TestItem.AddressC).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        await blockchain.AddBlock(transaction);
        Block block = blockchain.BlockTree.Head!;
        await blockchain.AddBlock();
        using ILifetimeScope scope = unavailable switch
        {
            UnavailableHistory.State => WithStateAvailability(blockchain, _ => false),
            UnavailableHistory.ParentHeader => WithStateAvailability(blockchain, _ => true, new MissingHeaderBlockTree(blockchain.BlockTree, block.ParentHash!)),
            _ => WithStateAvailability(blockchain, _ => true, new PrunedBodyBlockTree(blockchain.BlockTree, block)),
        };
        ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();
        (int Code, IResolveConstraint Message) expected = unavailable switch
        {
            UnavailableHistory.State => (ErrorCodes.ResourceUnavailable, Does.StartWith("No state available for block")),
            UnavailableHistory.ParentHeader => (ErrorCodes.ResourceNotFound, Is.EqualTo(BlockFinderExtensions.HeaderNotFound)),
            _ => (ErrorCodes.PrunedHistoryUnavailable, Does.StartWith(ErrorMessages.PrunedHistoryUnavailable)),
        };

        foreach (string response in new[]
        {
            await RpcTest.TestSerializedRequest(module, "trace_transaction", transaction.Hash!),
            await RpcTest.TestSerializedRequest(module, "trace_get", transaction.Hash!, Array.Empty<string>()),
            await RpcTest.TestSerializedRequest(module, "trace_transaction", transaction.Hash!, true),
            await RpcTest.TestSerializedRequest(module, "trace_replayTransaction", transaction.Hash!, new[] { "trace" }),
            await RpcTest.TestSerializedRequest(module, "trace_replayTransaction", transaction.Hash!, new[] { "trace" }, true),
        })
        {
            using JsonDocument document = JsonDocument.Parse(response);
            Assert.That(document.RootElement.TryGetProperty("error", out JsonElement error), Is.True, response);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(expected.Code), response);
                Assert.That(error.GetProperty("message").GetString(), expected.Message, response);
            }
        }
    }

    public enum UnavailableHistory { State, ParentHeader, PrunedBody }

    private sealed class PrunedBodyBlockTree(IBlockTree inner, Block prunedBlock) : BlockTreeTestDouble(inner)
    {
        public override Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
            blockHash == prunedBlock.Hash ? null : base.FindBlock(blockHash, options, blockNumber);

        public override ulong GetLowestBlock() => prunedBlock.Number + 1;
    }

    [Test]
    public async Task Trace_get_returns_the_trace_at_a_trace_address_path([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        // Y calls D; the transaction's init code calls Y and then D, so its paths are [], [0], [0, 0] and [1].
        ulong nonceA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        await blockchain.AddBlock(Build.A.Transaction.WithNonce(nonceA).WithTo(null).WithGasLimit(200_000)
            .WithData(Prepare.EvmCode.ForInitOf(Prepare.EvmCode.Call(TestItem.AddressD, 50_000).Op(Instruction.STOP).Done).Done)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        Address y = ContractAddress.From(TestItem.AddressA, nonceA);
        Transaction transaction = Build.A.Transaction.WithNonce(blockchain.ReadOnlyState.GetNonce(TestItem.AddressB)).WithTo(null).WithGasLimit(300_000)
            .WithData(Prepare.EvmCode.Call(y, 100_000).Call(TestItem.AddressD, 50_000).Op(Instruction.STOP).Done)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        await blockchain.AddBlock(transaction);

        async Task<JToken> Request(string method, params object[] parameters) =>
            JToken.Parse(await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, parameters))["result"]!;

        JToken traces = await Request("trace_transaction", transaction.Hash!);
        Assert.That(traces.Select(static trace => trace["traceAddress"]!.ToString(Newtonsoft.Json.Formatting.None)), Is.EqualTo(new[] { "[]", "[0]", "[0,0]", "[1]" }));

        using (Assert.EnterMultipleScope())
        {
            foreach ((string[] path, JToken expected) in new (string[], JToken)[]
            {
                ([], traces[0]!),
                (["0x0"], traces[1]!),
                (["0x0", "0x0"], traces[2]!),
                (["0x1"], traces[3]!),
                (["0x2"], JValue.CreateNull()),
                (["0x0", "0x1"], JValue.CreateNull()),
                (["0x1", "0x0"], JValue.CreateNull()),
                (["0x0", "0x0", "0x0"], JValue.CreateNull()),
                (["0x100000000"], JValue.CreateNull()),
                (["0xffffffffffffffff"], JValue.CreateNull()),
            })
            {
                Assert.That(await Request("trace_get", transaction.Hash!, path), Is.EqualTo(expected).Using(JToken.EqualityComparer), string.Join(",", path));
            }
        }
    }

    [Test]
    public async Task Trace_get_rejects_path_entries_that_are_not_hex_quantities(
        [Values("[0]", "[\"0x0\",1]", "[\"0x00\"]", "[\"0x10000000000000000\"]", "[null]", "\"0x0\"")] string path)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        using JsonDocument parameter = JsonDocument.Parse(path);

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_get", blockchain.BlockTree.Head!.Transactions[0].Hash!, parameter.RootElement);

        using JsonDocument document = JsonDocument.Parse(response);
        Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams), response);
    }

    [Test]
    public void Trace_get_selects_trace_address_path()
    {
        // The root calls [0] (which calls [0, 0]) and then [1].
        ParityTraceAction Action(params int[] traceAddress) => new() { TraceAddress = traceAddress };
        ParityTraceAction root = Action();
        ParityTraceAction first = Action(0);
        first.Subtraces.Add(Action(0, 0));
        root.Subtraces.AddRange([first, Action(1)]);
        ParityTxTraceFromStore[] traces = [.. ParityTxTraceFromStore.FromTxTrace(new ParityLikeTxTrace { Action = root })];

        ParityTxTraceFromStore? Select(params long[] traceAddress)
        {
            using ResultWrapper<ParityTxTraceFromStore?> result = TraceRpcModule.SelectTraceAddress(ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Success(traces), traceAddress);
            return result.Data;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Select(), Is.SameAs(traces[0]));
            Assert.That(Select(0), Is.SameAs(traces[1]));
            Assert.That(Select(0, 0), Is.SameAs(traces[2]));
            Assert.That(Select(1), Is.SameAs(traces[3]));
            Assert.That(Select(2), Is.Null);
            Assert.That(Select(0, 1), Is.Null);
            Assert.That(Select(1, 0), Is.Null);
            Assert.That(Select(0, 0, 0), Is.Null);
            Assert.That(Select(-1), Is.Null);
            Assert.That(Select(long.MaxValue), Is.Null);
        }
    }

    [Test]
    public async Task Tx_positions_are_fine()
    {
        Context context = new();
        await context.Build();
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_block", "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xa1e0e640b433d5a8931881b8eee7b1a125474b04e430c0bf8afff52584c53273\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x5cf5d4a0a93000beb1cfb373508ce4c0153ab491be99b3c927f482346c86a0e1\",\"transactionPosition\":1,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x02d2cde9120e37722f607771ebaa0d4e98c5d99a8a9e7df6872e8c8c9f5c0bc5\",\"transactionPosition\":2,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xe50a2a2d170011b1f9ee080c3810bed0c63dbb1b2b2c541c78ada5b222cc3fd2\",\"transactionPosition\":3,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xff0d4524d379fc15c41a9b0444b943e1a530779b7d09c8863858267c5ef92b24\",\"transactionPosition\":4,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xf9b69366c82084e3799dc4a7ad87dc173ef4923d853bc250de86b81786f2972a\",\"transactionPosition\":5,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x28171c29b23cd96f032fe43f444402af4555ee5f074d5d0d0a1089d940f136e7\",\"transactionPosition\":6,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x09b01caf4b7ecfe9d02251b2e478f2da0fdf08412e3fa1ff963fa80635dab031\",\"transactionPosition\":7,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xd82382905afbe4ca4c2b8e54cea43818c91e0014c3827e3020fbd82b732b8239\",\"transactionPosition\":8,\"type\":\"call\"},{\"action\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"rewardType\":\"block\",\"value\":\"0x1bc16d674ec80000\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}"));
    }

    // As in eth_getLogs, a bound past the head is invalid params, including a from bound that is also above to.
    [Test]
    public async Task Trace_filter_return_invalid_params_for_a_bound_past_the_head(
        [Values(
            "{\"fromBlock\":\"0x154\",\"after\":0}",
            "{\"fromBlock\":\"head+1\",\"toBlock\":\"head\"}",
            "{\"fromBlock\":\"head\",\"toBlock\":\"head+1\"}",
            "{\"fromBlock\":\"head-1\",\"toBlock\":\"0xffff\"}",
            "{\"toBlock\":\"head+1\"}")] string request,
        [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        ulong head = blockchain.BlockTree.Head!.Number;
        request = request.Replace("head+1", $"0x{head + 1:x}").Replace("head-1", $"0x{head - 1:x}").Replace("head", $"0x{head:x}");
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_filter", request);
        Assert.That(
            serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.InvalidParams},\"message\":\"requested block range is in the future\"}},\"id\":67}}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Trace_filter_accepts_the_head_as_a_bound([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        ulong head = blockchain.BlockTree.Head!.Number;
        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter",
            new { fromBlock = $"0x{head - 1:x}", toBlock = $"0x{head:x}" });
        string expected = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter",
            new { fromBlock = $"0x{head - 1:x}", toBlock = "latest" });
        using JsonDocument document = JsonDocument.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.GetProperty("result").GetArrayLength(), Is.GreaterThan(0), response);
            Assert.That(response, Is.EqualTo(expected));
        }
    }

    // An omitted fromBlock is latest, so an older toBlock alone is a reversed range.
    [TestCase("{\"fromBlock\":\"0x8\",\"toBlock\":\"0x6\"}", 8UL, 6UL)]
    [TestCase("{\"toBlock\":\"0x2\"}", null, 2UL)]
    public async Task Trace_filter_return_invalid_params_from_block_higher_than_to_block(string request, ulong? fromBlock, ulong toBlock)
    {
        Context context = new();
        await context.Build();
        ulong from = fromBlock ?? context.Blockchain.BlockTree.Head!.Number;
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_filter", request);
        Assert.That(
            serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.InvalidParams},\"message\":\"From block number: {from} is greater than to block number {toBlock}\"}},\"id\":67}}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Trace_filter_return_empty_result_with_count_0()
    {
        Context context = new();
        await context.Build();
        string request = "{\"count\":\"0x0\", \"fromBlock\":\"0x3\",\"toBlock\":\"0x3\"}";
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_filter", request);
        Assert.That(
            serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Trace_filter_null_member_is_omitted(
        [Values("fromAddress", "toAddress", "mode", "after", "count")] string member, [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        const string range = "\"fromBlock\":\"0x1\",\"toBlock\":\"latest\"";
        using JsonDocument withNull = JsonDocument.Parse($"{{{range},\"{member}\":null}}");
        using JsonDocument omitted = JsonDocument.Parse($"{{{range}}}");

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", withNull.RootElement);

        using JsonDocument document = JsonDocument.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.TryGetProperty("result", out JsonElement result) && result.GetArrayLength() > 0, Is.True, response);
            Assert.That(response, Is.EqualTo(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", omitted.RootElement)));
        }
    }

    // As blockHash does in eth_getLogs: exactly that block, filtered and paged as its single-block range is.
    [Test]
    public async Task Trace_filter_block_hash_selects_exactly_that_block(
        [Values("", ",\"after\":2,\"count\":3", ",\"toAddress\":[\"0x0000000000000000000000000000000000000000\"]",
            ",\"toAddress\":[\"beneficiary\"]", ",\"fromAddress\":[\"beneficiary\"],\"toAddress\":[\"beneficiary\"],\"mode\":\"union\"",
            ",\"fromAddress\":[\"0x0000000000000000000000000000000000000001\"]")] string fields,
        [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        // Below the head, so an answer for latest would differ.
        Block block = blockchain.BlockTree.FindBlock(blockchain.BlockTree.Head!.Number - 2)!;
        fields = fields.Replace("beneficiary", block.Beneficiary!.ToString());

        string byHash = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", $"{{\"blockHash\":\"{block.Hash}\"{fields}}}");
        string byNumber = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", $"{{\"fromBlock\":\"0x{block.Number:x}\",\"toBlock\":\"0x{block.Number:x}\"{fields}}}");
        string withNullBounds = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", $"{{\"blockHash\":\"{block.Hash}\",\"fromBlock\":null,\"toBlock\":null{fields}}}");

        using JsonDocument document = JsonDocument.Parse(byHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.TryGetProperty("result", out JsonElement result), Is.True, byHash);
            if (fields == "") Assert.That(result.GetArrayLength(), Is.GreaterThan(1), byHash);
            Assert.That(result.EnumerateArray().Select(static trace => trace.GetProperty("blockHash").GetString()), Is.All.EqualTo(block.Hash!.ToString()));
            Assert.That(byHash, Is.EqualTo(byNumber));
            Assert.That(withNullBounds, Is.EqualTo(byHash));
        }
    }

    public enum UnservedBlock { Unknown, SideChain, Unprocessed, MissingState, PrunedBody }

    // Neither [] nor another block's records: the hash names a block this node cannot trace as canonical.
    [Test]
    public async Task Trace_filter_block_hash_returns_an_error_for_a_block_it_cannot_serve(
        [Values] UnservedBlock unserved, [Values("", ",\"count\":0")] string count)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        Block block = blockchain.BlockTree.FindBlock(blockchain.BlockTree.Head!.Number - 2)!;
        Hash256 hash = block.Hash!;
        if (unserved == UnservedBlock.Unknown) hash = TestItem.KeccakH;
        if (unserved == UnservedBlock.SideChain)
        {
            // A known sibling of the block, never executed.
            Block sibling = Build.A.Block.WithParent(blockchain.BlockTree.FindHeader(block.ParentHash!)!).WithExtraData([1]).TestObject;
            blockchain.BlockTree.SuggestBlock(sibling, BlockTreeSuggestOptions.ForceDontSetAsMain);
            hash = sibling.Hash!;
        }

        Block next = Build.A.Block.WithParent(blockchain.BlockTree.Head!.Header).TestObject;
        if (unserved == UnservedBlock.Unprocessed) hash = next.Hash!;

        using ILifetimeScope scope = unserved switch
        {
            UnservedBlock.MissingState => WithStateAvailability(blockchain, header => header.Hash != hash),
            UnservedBlock.PrunedBody => WithStateAvailability(blockchain, _ => true, new PrunedBodyBlockTree(blockchain.BlockTree, block)),
            UnservedBlock.Unprocessed => WithStateAvailability(blockchain, _ => true, new UnprocessedCanonicalBlockTree(blockchain.BlockTree, next)),
            _ => WithStateAvailability(blockchain, _ => true),
        };
        ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();

        string response = await RpcTest.TestSerializedRequest(module, "trace_filter", $"{{\"blockHash\":\"{hash}\"{count}}}");

        using JsonDocument document = JsonDocument.Parse(response);
        Assert.That(document.RootElement.TryGetProperty("error", out JsonElement error), Is.True, response);
        (int Code, IResolveConstraint Message) expected = unserved switch
        {
            UnservedBlock.Unknown => (ErrorCodes.ResourceNotFound, Is.EqualTo(BlockFinderExtensions.HeaderNotFound)),
            UnservedBlock.SideChain => (ErrorCodes.InvalidInput, Is.EqualTo($"{hash} block is not canonical")),
            UnservedBlock.Unprocessed => (ErrorCodes.ResourceUnavailable, Is.EqualTo($"{hash} block is not processed")),
            UnservedBlock.MissingState => (ErrorCodes.ResourceUnavailable, Does.Contain($"No state available for block {block.Number} ")),
            _ => (ErrorCodes.PrunedHistoryUnavailable, Does.StartWith(ErrorMessages.PrunedHistoryUnavailable)),
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(expected.Code), response);
            Assert.That(error.GetProperty("message").GetString(), expected.Message, response);
        }
    }

    // Canonical, as a block can be during sync, but past the processed head.
    private sealed class UnprocessedCanonicalBlockTree(IBlockTree inner, Block unprocessed) : BlockTreeTestDouble(inner)
    {
        public override Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
            blockHash == unprocessed.Hash ? unprocessed : base.FindBlock(blockHash, options, blockNumber);

        public override BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null) =>
            blockHash == unprocessed.Hash ? unprocessed.Header : base.FindHeader(blockHash, options, blockNumber);

        public override bool IsMainChain(BlockHeader blockHeader) => blockHeader.Hash == unprocessed.Hash || base.IsMainChain(blockHeader);
    }

    // The head's hash was read before the head moved on, as when a block is added mid-request.
    private sealed class MovedHeadBlockTree(IBlockTree inner, Hash256 previousHeadHash) : BlockTreeTestDouble(inner)
    {
        public override Hash256 HeadHash => previousHeadHash;
    }

    [Test]
    public async Task Trace_filter_block_hash_is_not_answered_by_a_moved_head([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Block block = blockchain.BlockTree.FindBlock(blockchain.BlockTree.Head!.Number - 1)!;
        using ILifetimeScope scope = WithStateAvailability(blockchain, _ => true, new MovedHeadBlockTree(blockchain.BlockTree, block.Hash!));
        ITraceRpcModule module = scope.Resolve<TraceModuleFactory>().Create();

        string byHash = await RpcTest.TestSerializedRequest(module, "trace_filter", $"{{\"blockHash\":\"{block.Hash}\"}}");

        Assert.That(byHash, Is.EqualTo(await RpcTest.TestSerializedRequest(module, "trace_filter", $"{{\"fromBlock\":\"0x{block.Number:x}\",\"toBlock\":\"0x{block.Number:x}\"}}")));
    }

    [Test]
    public async Task Trace_filter_rejects_a_malformed_block_hash([Values("\"\"", "\"0x\"", "\"0x1234\"", "\"latest\"", "1")] string blockHash)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", $"{{\"blockHash\":{blockHash}}}");

        using JsonDocument document = JsonDocument.Parse(response);
        Assert.That(document.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetProperty("code").GetInt32() : 0, Is.EqualTo(ErrorCodes.InvalidParams), response);
    }

    // As in eth_getLogs, blockHash excludes a range; a null bound is omitted.
    [Test]
    public async Task Trace_filter_rejects_block_hash_with_a_bound(
        [Values("\"fromBlock\":\"0x1\"", "\"toBlock\":\"latest\"", "\"fromBlock\":\"0x1\",\"toBlock\":null", "\"fromBlock\":\"earliest\",\"toBlock\":\"latest\"")] string bounds)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter",
            $"{{\"blockHash\":\"{blockchain.BlockTree.Head!.Hash}\",{bounds}}}");

        Assert.That(response, Is.EqualTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.InvalidParams},\"message\":\"cannot specify both BlockHash and FromBlock/ToBlock, choose one or the other\"}},\"id\":67}}"));
    }

    [Test]
    public async Task Trace_filter_reads_null_block_hash_as_omitted([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;

        string withNull = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", "{\"blockHash\":null,\"fromBlock\":\"0x1\",\"toBlock\":\"latest\"}");

        using JsonDocument document = JsonDocument.Parse(withNull);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.TryGetProperty("result", out JsonElement result) && result.GetArrayLength() > 0, Is.True, withNull);
            Assert.That(withNull, Is.EqualTo(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", "{\"fromBlock\":\"0x1\",\"toBlock\":\"latest\"}")));
        }
    }

    // A hash bound names only a height, so it is rejected; blockHash selects a block.
    [Test]
    public async Task Trace_filter_rejects_a_block_hash_as_a_bound(
        [Values("fromBlock", "toBlock")] string bound,
        [Values("\"hash\"", "{\"blockHash\":\"hash\"}", "{\"blockHash\":\"hash\",\"requireCanonical\":true}")] string value)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        value = value.Replace("hash", blockchain.BlockTree.FindHeader(1)!.Hash!.ToString());

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", $"{{\"{bound}\":{value}}}");

        Assert.That(response, Is.EqualTo(
            $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{ErrorCodes.InvalidParams},\"message\":\"block hash is not a block number or tag\"}},\"id\":67}}"));
    }

    [Test]
    public async Task Trace_filter_reads_numbers_and_tags_as_bounds(
        [Values("\"0x1\"", "{\"blockNumber\":\"0x1\"}", "\"earliest\"", "\"latest\"")] string fromBlock)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", $"{{\"fromBlock\":{fromBlock},\"toBlock\":\"latest\"}}");

        using JsonDocument document = JsonDocument.Parse(response);
        Assert.That(document.RootElement.TryGetProperty("result", out JsonElement result) && result.GetArrayLength() > 0, Is.True, response);
    }

    [Test]
    public async Task Trace_filter_rejects_unknown_member_or_negative_bound_as_invalid_params(
        [Values("\"unknownDiagnosticFlag\":true", "\"count\":-1", "\"after\":-1")] string member)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        using JsonDocument filter = JsonDocument.Parse($"{{\"fromBlock\":\"0x1\",\"toBlock\":\"latest\",{member}}}");

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", filter.RootElement);

        using JsonDocument document = JsonDocument.Parse(response);
        Assert.That(document.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetProperty("code").GetInt32() : 0, Is.EqualTo(ErrorCodes.InvalidParams), response);
    }

    [Test]
    public async Task Trace_filter_reads_count_beyond_int_range(
        [Values("\"0xffffffff\"", "4294967296", "18446744073709551615")] string count, [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        const string range = "\"fromBlock\":\"0x1\",\"toBlock\":\"latest\"";
        using JsonDocument withCount = JsonDocument.Parse($"{{{range},\"count\":{count}}}");
        using JsonDocument omitted = JsonDocument.Parse($"{{{range}}}");

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", withCount.RootElement);

        Assert.That(response, Is.EqualTo(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", omitted.RootElement)));
    }

    [Test]
    public async Task Trace_filter_return_expected_json()
    {
        Context context = new();
        await context.Build();
        TraceFilterForRpc traceFilterRequest = new();
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_filter", new EthereumJsonSerializer().Serialize(traceFilterRequest));

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xa1e0e640b433d5a8931881b8eee7b1a125474b04e430c0bf8afff52584c53273\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x5cf5d4a0a93000beb1cfb373508ce4c0153ab491be99b3c927f482346c86a0e1\",\"transactionPosition\":1,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x02d2cde9120e37722f607771ebaa0d4e98c5d99a8a9e7df6872e8c8c9f5c0bc5\",\"transactionPosition\":2,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xe50a2a2d170011b1f9ee080c3810bed0c63dbb1b2b2c541c78ada5b222cc3fd2\",\"transactionPosition\":3,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xff0d4524d379fc15c41a9b0444b943e1a530779b7d09c8863858267c5ef92b24\",\"transactionPosition\":4,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xf9b69366c82084e3799dc4a7ad87dc173ef4923d853bc250de86b81786f2972a\",\"transactionPosition\":5,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x28171c29b23cd96f032fe43f444402af4555ee5f074d5d0d0a1089d940f136e7\",\"transactionPosition\":6,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0x09b01caf4b7ecfe9d02251b2e478f2da0fdf08412e3fa1ff963fa80635dab031\",\"transactionPosition\":7,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xd82382905afbe4ca4c2b8e54cea43818c91e0014c3827e3020fbd82b732b8239\",\"transactionPosition\":8,\"type\":\"call\"},{\"action\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"rewardType\":\"block\",\"value\":\"0x1bc16d674ec80000\"},\"blockHash\":\"0xcd3d2c10309822aec4cbbfa80ba905ba1de62834a3b40f8012520734db2763ca\",\"blockNumber\":15,\"subtraces\":0,\"traceAddress\":[],\"type\":\"reward\"}],\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Trace_filter_skip_expected_number_of_traces()
    {
        Context context = new();
        await context.Build();
        TraceFilterForRpc traceRequest = new();
        traceRequest.After = 3;
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> secondTraces = context.TraceRpcModule.trace_filter(traceRequest);
        Assert.That(secondTraces.Data.Count(), Is.EqualTo(7));
    }
    [Test]
    public async Task Trace_filter_get_given_amount_of_traces()
    {
        Context context = new();
        await context.Build();
        TraceFilterForRpc traceFilterRequest = new();
        traceFilterRequest.Count = 3;
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        Assert.That(traces.Data.Count(), Is.EqualTo(3));
    }
    [Test]
    public async Task Trace_filter_skip_and_get_the_rest_of_traces()
    {
        Context context = new();
        await context.Build();
        TraceFilterForRpc traceFilterRequest = new();
        traceFilterRequest.Count = 3;
        traceFilterRequest.After = 7;
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        // Total 9 transactions in block + 1 reward trace - after skipping 7 - it should be 3
        Assert.That(traces.Data.Count(), Is.EqualTo(3));
    }
    [Test]
    public async Task Trace_filter_with_filtering_by_receiver_address()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(blockchain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;
        await context.Blockchain.AddBlock(transaction);

        TraceFilterForRpc traceFilterRequest = new();
        ulong lastBLockNumber = blockchain.BlockTree.Head!.Number;
        traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber);
        traceFilterRequest.ToBlock = new BlockParameter(lastBLockNumber);
        traceFilterRequest.ToAddress = new[] { TestItem.AddressA };
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        Assert.That(traces.Data.Count(), Is.EqualTo(1));
    }
    [Test]
    public async Task Trace_filter_with_filtering_by_sender()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(blockchain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;
        await context.Blockchain.AddBlock(transaction);
        await context.Blockchain.AddBlock();
        ulong lastBLockNumber = blockchain.BlockTree.Head!.Number;
        TraceFilterForRpc traceFilterRequest = new();
        traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber - 1);
        traceFilterRequest.ToBlock = BlockParameter.Latest;
        traceFilterRequest.FromAddress = new[] { TestItem.PrivateKeyA.Address };
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        Assert.That(traces.Data.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Trace_filter_with_filtering_by_internal_transaction_receiver()
    {

        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong currentNonceAddressB = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);
        byte[] deployedCode = new byte[3];
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;
        byte[] createCode = Prepare.EvmCode
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
            .WithData(createCode)
            .WithTo(null)
            .WithGasLimit(93548).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await blockchain.AddBlock(transaction1);
        Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
        byte[] code = Prepare.EvmCode
            .Call(contractAddress, 50000)
            .Call(contractAddress, 50000)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressB++)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
            .WithTo(null)
            .WithGasLimit(93548).TestObject;
        await blockchain.AddBlock(transaction2);
        await blockchain.AddBlock();
        ulong lastBLockNumber = blockchain.BlockTree.Head!.Number;

        TraceFilterForRpc traceFilterRequest = new();
        traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber - 1);
        traceFilterRequest.ToBlock = BlockParameter.Latest;
        traceFilterRequest.ToAddress = new[] { contractAddress };
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        Assert.That(traces.Data.Count(), Is.EqualTo(2));

    }
    [Test]
    public async Task Trace_filter_with_filtering_by_sender_and_receiver()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        await context.Blockchain.AddBlockMayMissTx(
            new[]
            {
                Build.A.Transaction.WithNonce(currentNonceAddressA + 1UL).WithTo(TestItem.AddressD)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.Transaction.WithNonce(blockchain.ReadOnlyState.GetNonce(TestItem.AddressB) + 1UL).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyB).TestObject,
                Build.A.Transaction.WithNonce(currentNonceAddressA).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject
            }
        );
        await context.Blockchain.AddBlock();
        TraceFilterForRpc traceFilterRequest = new();
        ulong lastBLockNumber = blockchain.BlockTree.Head!.Number;
        traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber - 1);
        traceFilterRequest.ToBlock = BlockParameter.Latest;
        traceFilterRequest.FromAddress = new[] { TestItem.PrivateKeyA.Address };
        traceFilterRequest.ToAddress = new[] { TestItem.AddressC };
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        Assert.That(traces.Data.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Trace_filter_matches_rewards_by_author([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Block head = blockchain.BlockTree.Head!;
        string fromBlock = $"0x{head.Number - 2:x}";
        string author = head.Beneficiary!.ToString();

        async Task<string[]> Filter(object filter)
        {
            string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", filter);
            using JsonDocument document = JsonDocument.Parse(response);
            return [.. document.RootElement.GetProperty("result").EnumerateArray().Select(static trace => trace.GetRawText())];
        }

        string[] rewards = [.. (await Filter(new { fromBlock, toBlock = "latest" })).Where(trace => trace.Contains($"\"author\":\"{author}\""))];
        string[] byAuthor = await Filter(new { fromBlock, toBlock = "latest", toAddress = new[] { author } });
        string[] paged = await Filter(new { fromBlock, toBlock = "latest", toAddress = new[] { author }, after = 1, count = 1 });
        string[] bySenderAndAuthor = await Filter(new { fromBlock, toBlock = "latest", fromAddress = new[] { TestItem.AddressB }, toAddress = new[] { author } });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewards, Has.Length.EqualTo(3));
            Assert.That(byAuthor, Is.EqualTo(rewards));
            Assert.That(paged, Is.EqualTo(rewards[1..2]));
            Assert.That(bySenderAndAuthor, Is.Empty);
        }
    }

    [Test]
    public async Task Trace_filter_combines_address_lists_by_mode([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Block head = blockchain.BlockTree.Head!;
        string range = $"\"fromBlock\":\"0x{head.Number - 2:x}\",\"toBlock\":\"latest\"";
        string author = head.Beneficiary!.ToString();
        string sender = TestItem.AddressB.ToString();
        string other = TestItem.AddressE.ToString();
        string recipient = Address.Zero.ToString();

        async Task<string[]> Filter(string fields)
        {
            using JsonDocument filter = JsonDocument.Parse($"{{{range}{fields}}}");
            string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", filter.RootElement);
            using JsonDocument document = JsonDocument.Parse(response);
            Assert.That(document.RootElement.TryGetProperty("result", out JsonElement result), Is.True, response);
            return [.. result.EnumerateArray().Select(static trace => trace.GetRawText())];
        }

        string[] all = await Filter("");
        string[] rewards = [.. all.Where(static trace => trace.Contains("\"type\":\"reward\""))];
        string[] calls = [.. all.Where(static trace => !trace.Contains("\"type\":\"reward\""))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewards, Has.Length.EqualTo(3));
            Assert.That(calls, Is.Not.Empty.And.All.Contains($"\"from\":\"{sender}\"").And.All.Contains($"\"to\":\"{recipient}\""));
            // A call matches by sender and a reward by author; a reward has no sender side.
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{author}\"],\"mode\":\"union\""), Is.EqualTo(all));
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{other}\"],\"mode\":\"union\""), Is.EqualTo(calls));
            Assert.That(await Filter($",\"fromAddress\":[\"{other}\"],\"toAddress\":[\"{author}\"],\"mode\":\"union\""), Is.EqualTo(rewards));
            Assert.That(await Filter($",\"toAddress\":[\"{author}\"],\"mode\":\"union\""), Is.EqualTo(rewards));
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{author}\"],\"mode\":\"union\",\"after\":2,\"count\":3"), Is.EqualTo(all[2..5]));
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{author}\"],\"mode\":\"intersection\""), Is.Empty);
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{author}\"]"), Is.Empty);
            // An explicit null mode is the same as omitting it, so it means intersection.
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{author}\"],\"mode\":null"), Is.Empty);
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{recipient}\"]"), Is.EqualTo(calls));
            Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[\"{recipient}\"],\"mode\":null"), Is.EqualTo(calls));
            // An empty list does not restrict, the same as an omitted one, under every mode.
            foreach (string mode in new[] { "", ",\"mode\":\"intersection\"", ",\"mode\":\"union\"" })
            {
                string[] byAuthor = await Filter($",\"toAddress\":[\"{author}\"]{mode}");
                string[] bySender = await Filter($",\"fromAddress\":[\"{sender}\"]{mode}");
                Assert.That(byAuthor, Is.EqualTo(rewards), mode);
                Assert.That(bySender, Is.EqualTo(calls), mode);
                Assert.That(await Filter($",\"fromAddress\":[],\"toAddress\":[\"{author}\"]{mode}"), Is.EqualTo(byAuthor), mode);
                Assert.That(await Filter($",\"fromAddress\":[\"{sender}\"],\"toAddress\":[]{mode}"), Is.EqualTo(bySender), mode);
                Assert.That(await Filter($",\"fromAddress\":[],\"toAddress\":[]{mode}"), Is.EqualTo(all), mode);
                Assert.That(await Filter($",\"fromAddress\":[]{mode}"), Is.EqualTo(all), mode);
                Assert.That(await Filter($",\"toAddress\":[]{mode}"), Is.EqualTo(all), mode);
            }
        }
    }

    [Test]
    public async Task Trace_filter_rejects_unknown_mode(
        [Values("\"garbage\"", "\"Union\"", "\"INTERSECTION\"", "\"\"", "0", "true", "[\"union\"]", "{}")] string mode)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        using JsonDocument filter = JsonDocument.Parse($"{{\"fromBlock\":\"latest\",\"toBlock\":\"latest\",\"mode\":{mode}}}");

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", filter.RootElement);

        Assert.That(response, Is.EqualTo("""{"jsonrpc":"2.0","error":{"code":-32602,"message":"invalid trace filter mode, expected \"intersection\" or \"union\""},"id":67}"""));
    }

    [Test]
    public async Task Trace_filter_complex_scenario()
    {
        Context context = new();
        await context.Build();
        TraceFilterForRpc traceFilterRequest = new();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong lastBLockNumber = blockchain.BlockTree.Head!.Number;
        traceFilterRequest.After = 3;
        traceFilterRequest.Count = 4;
        traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber + 1);
        traceFilterRequest.ToBlock = BlockParameter.Latest;
        traceFilterRequest.FromAddress = new[] { TestItem.PrivateKeyA.Address, TestItem.PrivateKeyD.Address };
        traceFilterRequest.ToAddress = new[] { TestItem.AddressC, TestItem.AddressA, TestItem.AddressB };
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong currentNonceAddressC = blockchain.ReadOnlyState.GetNonce(TestItem.AddressC);
        ulong currentNonceAddressD = blockchain.ReadOnlyState.GetNonce(TestItem.AddressD);
        // first block skipped: After 3 -> 1
        await blockchain.AddBlock(
            new[]
            {
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressD)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // skipped
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --After
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressB) // --After
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject
            }
        );
        // second block: After 1 -> 0, Count 4 -> 3
        await blockchain.AddBlock(
            new[]
            {
                Build.A.Transaction.WithNonce(currentNonceAddressC++).WithTo(TestItem.AddressA)
                    .SignedAndResolved(TestItem.PrivateKeyC).TestObject, // skipped
                Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // --After
                Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressB)
                    .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // --Count
                Build.A.Transaction.WithNonce(currentNonceAddressD++)
                    .SignedAndResolved(TestItem.PrivateKeyD).TestObject // skipped
            }
        );
        // third block: Count 3 -> 1
        await blockchain.AddBlock(
            new[]
            {
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressB)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --Count
                Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressD)
                    .SignedAndResolved(TestItem.PrivateKeyD).TestObject, // skipped
                Build.A.Transaction.WithNonce(currentNonceAddressD++).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyD).TestObject // skipped
            }
        );
        // fourth block: Count 1 -> 0
        await blockchain.AddBlock(
            new[]
            {
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressD)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // skipped
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject, // --Count
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject // skipped (Count == 0)
            }
        );
        // the last block: skipped
        await blockchain.AddBlock(
            new[]
            {
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressB)
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject
            }
        );
        await blockchain.AddBlock();
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        Assert.That(traces.Data.Count(), Is.EqualTo(traceFilterRequest.Count));
    }

    [Test]
    public async Task Trace_filter_complex_scenario_openethereum()
    {
        Context context = new();
        await context.Build();
        TraceFilterForRpc traceFilterRequest = new();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong lastBLockNumber = blockchain.BlockTree.Head!.Number;
        // traceFilterRequest.After = 3;
        // traceFilterRequest.Count = 4;
        traceFilterRequest.FromBlock = new BlockParameter(lastBLockNumber + 1);
        traceFilterRequest.ToBlock = BlockParameter.Latest;
        // traceFilterRequest.FromAddress = new[] {TestItem.PrivateKeyA.Address, TestItem.PrivateKeyD.Address};
        // traceFilterRequest.ToAddress = new[] {TestItem.AddressC, TestItem.AddressA, TestItem.AddressB};
        ulong currentNonceAddressC = blockchain.ReadOnlyState.GetNonce(TestItem.AddressC);
        await blockchain.AddBlock();
        await blockchain.AddBlock();
        await blockchain.AddBlock(
            new[]
            {
                Build.A.Transaction.WithNonce(currentNonceAddressC++).WithTo(TestItem.AddressA)
                    .SignedAndResolved(TestItem.PrivateKeyC).TestObject,
            }
        );
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> traces = context.TraceRpcModule.trace_filter(traceFilterRequest);
        Assert.That(traces.Data.Count(), Is.EqualTo(4));
    }

    [Test]
    public async Task trace_transaction_and_get_simple_tx()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await blockchain.AddBlock(transaction);
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> traces = context.TraceRpcModule.trace_transaction(transaction.Hash!);
        Assert.That(traces.Data!.Select(static trace => trace.TransactionHash), Is.EqualTo(new[] { transaction.Hash }));

        using ResultWrapper<ParityTxTraceFromStore?> traceGet = context.TraceRpcModule.trace_get(transaction.Hash!, [0]);
        Assert.That(traceGet.Data, Is.Null);
    }

    [Test]
    public async Task Trace_get_can_trace_simple_tx()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await blockchain.AddBlock(transaction);

        using ResultWrapper<ParityTxTraceFromStore?> trace = context.TraceRpcModule.trace_get(transaction.Hash!, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Data!.TransactionHash, Is.EqualTo(transaction.Hash));
            Assert.That(trace.Data.TraceAddress.Length, Is.Zero);
        }
    }

    [Test]
    public async Task trace_transaction_and_get_internal_tx()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong currentNonceAddressB = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);
        byte[] deployedCode = new byte[3];
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;
        byte[] createCode = Prepare.EvmCode
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
            .WithData(createCode)
            .WithTo(null)
            .WithGasLimit(93548).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await blockchain.AddBlock(transaction1);
        Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
        byte[] code = Prepare.EvmCode
            .Call(contractAddress, 50000)
            .Call(contractAddress, 50000)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressB++)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
            .WithTo(null)
            .WithGasLimit(93548).TestObject;
        await blockchain.AddBlock(transaction2);

        IEnumerable<ParityTxTraceFromStore> traces = context.TraceRpcModule.trace_transaction(transaction2.Hash!).Data!;
        Assert.That(System.Linq.Enumerable.Count(traces), Is.EqualTo(3));
        Assert.That(traces.ElementAt(0).TransactionHash, Is.EqualTo(transaction2.Hash!));

        using ResultWrapper<ParityTxTraceFromStore?> traceGet = context.TraceRpcModule.trace_get(transaction2.Hash!, [0]);
        Assert.That(traces.ElementAt(0).TransactionHash, Is.EqualTo(transaction2.Hash));
        EthereumJsonSerializer serializer = new();
        Assert.That(JToken.Parse(serializer.Serialize(traces.ElementAt(1))), Is.EqualTo(JToken.Parse(serializer.Serialize(traceGet.Data))).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Trace_transaction_with_error_reverted()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong currentNonceAddressB = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);
        byte[] deployedCode = new byte[3];
        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;
        byte[] createCode = Prepare.EvmCode
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
            .WithData(createCode)
            .WithTo(null)
            .WithGasLimit(93548).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await blockchain.AddBlock(transaction1);
        Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
        byte[] code = Prepare.EvmCode
            .Call(contractAddress, 50000)
            .Call(contractAddress, 50000)
            .Op(Instruction.REVERT)
            .Done;

        Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressB++)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
            .WithTo(null)
            .WithGasLimit(93548).TestObject;
        await blockchain.AddBlock(transaction2);
        IEnumerable<ParityTxTraceFromStore> traces = context.TraceRpcModule.trace_transaction(transaction2.Hash!).Data!;
        Assert.That(System.Linq.Enumerable.Count(traces), Is.EqualTo(3));
        Assert.That(traces.ElementAt(0).TransactionHash, Is.EqualTo(transaction2.Hash!));
        string serialized = new EthereumJsonSerializer().Serialize(traces);

        Assert.That(serialized, Is.EqualTo("[{\"action\":{\"creationMethod\":\"create\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"gas\":\"0x9a6c\",\"init\":\"0x60006000600060006000736b5887043de753ecfa6269f947129068263ffbe261c350f160006000600060006000736b5887043de753ecfa6269f947129068263ffbe261c350f1fd\",\"value\":\"0x1\"},\"blockHash\":\"0xeb0d05efb43e565c4a677e64dde4cd1339459310afe8f578acab57ad45dd8f44\",\"blockNumber\":18,\"result\":{\"gasUsed\":\"0xab9\",\"output\":\"0x00\"},\"subtraces\":2,\"traceAddress\":[],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"create\",\"error\":\"Reverted\"},{\"action\":{\"callType\":\"call\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"gas\":\"0x8def\",\"input\":\"0x\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"value\":\"0x0\"},\"blockHash\":\"0xeb0d05efb43e565c4a677e64dde4cd1339459310afe8f578acab57ad45dd8f44\",\"blockNumber\":18,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[0],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0xd6a48bcd4c5ad5adacfab677519c25ce7b2805a5\",\"gas\":\"0x8d78\",\"input\":\"0x\",\"to\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"value\":\"0x0\"},\"blockHash\":\"0xeb0d05efb43e565c4a677e64dde4cd1339459310afe8f578acab57ad45dd8f44\",\"blockNumber\":18,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[1],\"transactionHash\":\"0x787616b8756424622f162fc3817331517ef941366f28db452defc0214bc36b22\",\"transactionPosition\":0,\"type\":\"call\"}]"), serialized.Replace("\"", "\\\""));
    }

    // PUSH4 PUSH1 MSTORE (with one word of memory) PUSH1 PUSH1 REVERT: 3 + 3 + 6 + 3 + 3 = 18 gas.
    private static readonly byte[] RevertDeadbeefCode = Prepare.EvmCode
        .PushData("0xdeadbeef")
        .PushData(0)
        .Op(Instruction.MSTORE)
        .Revert(4, 28)
        .Done;

    [Test]
    public async Task Trace_call_keeps_result_of_reverted_frame([Values] bool nested, [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        byte[] data = nested ? Prepare.EvmCode.Create(RevertDeadbeefCode, 0).Op(Instruction.STOP).Done : RevertDeadbeefCode;
        object transaction = new { from = TestItem.AddressA, gas = "0x186a0", data = data.ToHexString(true) };

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", transaction, new[] { "trace" }, "latest");
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement frame = document.RootElement.GetProperty("result").GetProperty("trace")[nested ? 1 : 0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.GetProperty("type").GetString(), Is.EqualTo("create"), response);
            Assert.That(frame.GetProperty("error").GetString(), Is.EqualTo("Reverted"), response);
            Assert.That(frame.GetProperty("result").GetRawText(), Is.EqualTo("""{"gasUsed":"0x12","output":"0xdeadbeef"}"""), response);
        }
    }

    // A reverted, a successful and a halted creation in one request, so the streaming tracer reuses its frames.
    [Test]
    public async Task Trace_callMany_keeps_result_only_for_reverted_and_successful_frames([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        byte[] deploy = Prepare.EvmCode.ForInitOf([0x2a]).Done;
        byte[] halt = Prepare.EvmCode.Op(Instruction.ADD).Done;
        object[] calls = new[] { RevertDeadbeefCode, deploy, halt }
            .Select(static object (byte[] data) => new object[] { new { from = TestItem.AddressA, gas = "0x186a0", data = data.ToHexString(true) }, new[] { "trace" } })
            .ToArray();

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls, "latest");
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement[] frames = document.RootElement.GetProperty("result").EnumerateArray().Select(static r => r.GetProperty("trace")[0]).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(frames[0].GetProperty("error").GetString(), Is.EqualTo("Reverted"), response);
            Assert.That(frames[0].GetProperty("result").GetRawText(), Is.EqualTo("""{"gasUsed":"0x12","output":"0xdeadbeef"}"""), response);
            Assert.That(frames[1].TryGetProperty("error", out _), Is.False, response);
            Assert.That(frames[1].GetProperty("result").GetProperty("code").GetString(), Is.EqualTo("0x2a"), response);
            Assert.That(frames[2].GetProperty("error").GetString(), Is.EqualTo("Stack underflow"), response);
            Assert.That(frames[2].TryGetProperty("result", out _), Is.False, response);
        }
    }

    // A reverted top-level creation, and a successful creation whose nested creation reverts, in one block.
    [Test]
    public async Task Trace_filter_matches_failed_creation_only_by_its_creator([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        ulong nonceA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong nonceB = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
        Address failed = ContractAddress.From(TestItem.AddressA, nonceA);
        Address creator = ContractAddress.From(TestItem.AddressB, nonceB);
        Address nestedFailed = ContractAddress.From(creator, 1);
        byte[] createReverting = Prepare.EvmCode.Create(RevertDeadbeefCode, 0).Op(Instruction.STOP).Done;
        await blockchain.AddBlock(
            Build.A.Transaction.WithNonce(nonceA).WithTo(null).WithData(RevertDeadbeefCode).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(nonceB).WithTo(null).WithData(createReverting).WithGasLimit(200_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        string block = $"0x{blockchain.BlockTree.Head!.Number:x}";

        // Each match as its transaction position and trace address.
        async Task<string[]> Filter(Address[]? fromAddress, Address[]? toAddress)
        {
            string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_filter", new { fromBlock = block, toBlock = block, fromAddress, toAddress });
            using JsonDocument document = JsonDocument.Parse(response);
            return [.. document.RootElement.GetProperty("result").EnumerateArray()
                .Select(static trace => $"{trace.GetProperty("transactionPosition").GetInt32()}:{trace.GetProperty("traceAddress").GetRawText()}")];
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Filter(null, [failed]), Is.Empty, "a failed top-level creation has no created address");
            Assert.That(await Filter(null, [nestedFailed]), Is.Empty, "a failed nested creation has no created address");
            Assert.That(await Filter([TestItem.AddressA], [failed]), Is.Empty, "a failed top-level creation has no recipient side");
            Assert.That(await Filter([creator], [nestedFailed]), Is.Empty, "a failed nested creation has no recipient side");
            Assert.That(await Filter([TestItem.AddressA], null), Is.EqualTo(new[] { "0:[]" }), "a failed top-level creation matches its creator");
            Assert.That(await Filter([creator], null), Is.EqualTo(new[] { "1:[0]" }), "a failed nested creation matches its creator");
            Assert.That(await Filter(null, [creator]), Is.EqualTo(new[] { "1:[]" }), "a successful creation matches its created address");
        }
    }

    [Test]
    public async Task trace_timeout_is_separate_for_rpc_calls()
    {
        Context context = new();
        await context.Build();
        IJsonRpcConfig jsonRpcConfig = context.JsonRpcConfig;
        jsonRpcConfig.Timeout = 25;
        ITraceRpcModule traceRpcModule = context.TraceRpcModule;
        BlockParameter searchParameter = new(number: 0);
        Assert.DoesNotThrow(() => traceRpcModule.trace_block(searchParameter));
        await Task.Delay(jsonRpcConfig.Timeout +
                         25); //additional second just to show that in this time span timeout should occur if given one for whole class
        Assert.DoesNotThrow(() => traceRpcModule.trace_block(searchParameter));
    }
    [Test]
    public async Task Trace_replayTransaction_test()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong currentNonceAddressB = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);
        byte[] deployedCode = new byte[3];
        _ = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;

        Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
        byte[] code = Prepare.EvmCode
            .Call(contractAddress, 50000)
            .Call(contractAddress, 50000)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressB++)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
            .WithTo(TestItem.AddressC)
            .WithGasLimit(93548).TestObject;
        await blockchain.AddBlock(transaction);
        string[] traceTypes = { "trace" };
        ResultWrapper<ParityTxTraceFromReplay?> traces = context.TraceRpcModule.trace_replayTransaction(transaction.Hash!, traceTypes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Data!.Action!.From, Is.EqualTo(TestItem.AddressB));
            Assert.That(traces.Data.Action.To, Is.EqualTo(TestItem.AddressC));
            Assert.That(traces.Data.Action.CallType, Is.EqualTo("call"));
            Assert.That(traces.Result.ResultType == ResultType.Success, Is.True);
        }
    }

    [Test]
    public async Task Trace_replayTransaction_reward_test()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong currentNonceAddressB = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);
        Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
        byte[] code = Prepare.EvmCode
            .Call(contractAddress, 50000)
            .Call(contractAddress, 50000)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressB++)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
            .WithTo(TestItem.AddressC)
            .WithGasLimit(93548).TestObject;
        await blockchain.AddBlock(transaction);
        string[] traceTypes = { "rewards" };
        ResultWrapper<ParityTxTraceFromReplay?> traces = context.TraceRpcModule.trace_replayTransaction(transaction.Hash!, traceTypes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Data!.Action!.CallType, Is.EqualTo("reward"));
            Assert.That(traces.Data.Action.Value, Is.EqualTo(UInt256.Parse("2000000000000000000")));
            Assert.That(traces.Result.ResultType == ResultType.Success, Is.True);
        }
    }

    [Test]
    public async Task trace_replayBlockTransactions_zeroGasUsed_test()
    {
        Context context = new();
        OverridableReleaseSpec releaseSpec = new(London.Instance);
        releaseSpec.Eip1559TransitionBlock = 1;
        TestSpecProvider specProvider = new(releaseSpec);
        await context.Build(specProvider, isAura: true);
        TestRpcBlockchain blockchain = context.Blockchain;
        await blockchain.AddFunds(TestItem.AddressC, 10.Ether);
        ulong currentNonceAddressC = blockchain.ReadOnlyState.GetNonce(TestItem.AddressC);

        Transaction serviceTransaction = Build.A.Transaction.WithNonce(currentNonceAddressC++)
            .WithTo(TestItem.AddressE)
            .WithGasPrice(875000000)
            .SignedAndResolved(TestItem.PrivateKeyC)
            .WithIsServiceTransaction(true).TestObject;
        await blockchain.AddBlockMayMissTx(serviceTransaction);
        BlockParameter blockParameter = new(BlockParameterType.Latest);
        string[] traceTypes = { "trace" };
        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> traces = context.TraceRpcModule.trace_replayBlockTransactions(blockParameter, traceTypes);
        Assert.That(traces.Data.First().Action!.Result!.GasUsed, Is.EqualTo(0));
    }

    [Test]
    public async Task Trace_call_without_blockParameter_provided_test()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        ulong currentNonceAddressB = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);

        Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);
        byte[] code = Prepare.EvmCode
            .Call(contractAddress, 50000)
            .Call(contractAddress, 50000)
            .Op(Instruction.STOP)
            .Done;

        Transaction transaction = Build.A.Transaction.WithNonce(currentNonceAddressB++)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
            .WithTo(TestItem.AddressC)
            .WithGasLimit(93548).TestObject;
        await blockchain.AddBlock(transaction);


        Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressB++)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyB)
            .WithTo(TestItem.AddressC)
            .WithGasLimit(93548).TestObject;
        await blockchain.AddBlock(transaction2);

        TransactionForRpc transactionRpc = TransactionForRpc.FromTransaction(transaction2);

        string[] traceTypes = { "trace" };

        ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_call(transactionRpc, traceTypes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.Data.Action!.CallType, Is.EqualTo("call"));
            Assert.That(traces.Data.Action.From, Is.EqualTo(TestItem.AddressB));
            Assert.That(traces.Data.Action.To, Is.EqualTo(TestItem.AddressC));
        }
    }

    [Test]
    public async Task Trace_call_runs_on_top_of_specified_block()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;

        PrivateKey addressKey = Build.A.PrivateKey.TestObject;
        Address address = addressKey.Address;
        UInt256 balance = 100.Ether, send = balance / 2;

        await blockchain.AddFunds(address, balance);
        Hash256 lastBlockHash = blockchain.BlockTree.Head!.Hash!;

        string[] traceTypes = ["stateDiff"];
        Transaction transaction = Build.A.Transaction
            .SignedAndResolved(addressKey)
            .WithTo(TestItem.AddressC)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(0)
            .WithValue(send)
            .TestObject;

        ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_call(
            TransactionForRpc.FromTransaction(transaction), traceTypes, new(lastBlockHash)
        );

        AssertBalanceChange(traces.Data.StateChanges?.GetValueOrDefault(address), balance, balance - send);
    }

    [Test]
    public async Task Trace_callMany_runs_on_top_of_specified_block()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;

        PrivateKey addressKey = Build.A.PrivateKey.TestObject;
        Address address = addressKey.Address;
        UInt256 balance = 100.Ether, send = balance / 2;

        await blockchain.AddFunds(address, balance);
        Hash256 lastBlockHash = blockchain.BlockTree.Head!.Hash!;

        string[] traceTypes = ["stateDiff"];
        Transaction transaction = Build.A.Transaction
            .SignedAndResolved(addressKey)
            .WithTo(TestItem.AddressC)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(0)
            .WithValue(send)
            .TestObject;

        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> traces = context.TraceRpcModule.trace_callMany(
            new(new(1) { new() { Transaction = TransactionForRpc.FromTransaction(transaction), TraceTypes = traceTypes } }),
            new(lastBlockHash)
        );

        AssertBalanceChange(traces.Data.Single().StateChanges?.GetValueOrDefault(address), balance, balance - send);
    }

    [Test]
    public async Task Trace_rawTransaction_runs_on_top_of_specified_block()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;

        PrivateKey addressKey = Build.A.PrivateKey.TestObject;
        Address address = addressKey.Address;
        UInt256 balance = 100.Ether, send = balance / 2;

        await blockchain.AddFunds(address, balance);

        string[] traceTypes = ["stateDiff"];
        Transaction transaction = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithValue(send)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(0)
            .SignedAndResolved(addressKey)
            .TestObject;

        ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_rawTransaction(
            TxDecoder.Instance.Encode(transaction).Bytes, traceTypes
        );

        AssertBalanceChange(traces.Data.StateChanges?.GetValueOrDefault(address), balance, balance - send);
    }

    [Test]
    public async Task Trace_rawTransaction_returns_invalid_rlp_for_empty_list()
    {
        Context context = new();
        await context.Build();

        ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_rawTransaction([0xC0], ["trace"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.ErrorCode, Is.EqualTo(ErrorCodes.TransactionRejected));
            Assert.That(traces.Result.Error, Is.EqualTo("Invalid RLP."));
        }
    }

    [Test]
    public async Task Trace_rawTransaction_rejects_a_transaction_signed_for_another_chain(
        [Values(TxType.Legacy, TxType.EIP1559)] TxType type, [Values] bool otherChain)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        ulong chainId = blockchain.SpecProvider.ChainId;
        ulong signedChainId = otherChain ? chainId + 1 : chainId;

        ResultWrapper<ParityTxTraceFromReplay> traces = TraceRaw(context, SignedTransaction(type, signedChainId));

        using (Assert.EnterMultipleScope())
        {
            if (otherChain)
            {
                Assert.That(traces.ErrorCode, Is.EqualTo(ErrorCodes.TransactionRejected));
                Assert.That(traces.Result.Error, Is.EqualTo(TxErrorMessages.InvalidTxChainId(chainId, signedChainId)),
                    () => $"traced from {traces.Data?.Action?.From}");
            }
            else
            {
                AssertTracedFromAddressA(traces);
            }
        }
    }

    [Test]
    public async Task Trace_rawTransaction_rejects_a_frame_transaction_for_another_chain()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        ulong chainId = blockchain.SpecProvider.ChainId;
        Transaction transaction = FrameTxTestFrames.FrameTx(FrameTxTestFrames.SelfVerify());
        transaction.ChainId = chainId + 1;

        ResultWrapper<ParityTxTraceFromReplay> traces = TraceRaw(context, transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.ErrorCode, Is.EqualTo(ErrorCodes.TransactionRejected));
            Assert.That(traces.Result.Error, Is.EqualTo(TxErrorMessages.InvalidTxChainId(chainId, chainId + 1)));
        }
    }

    [Test]
    public async Task Trace_rawTransaction_accepts_a_pre_eip155_legacy_transaction()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        Transaction transaction = SignedTransaction(TxType.Legacy, blockchain.SpecProvider.ChainId, isEip155Enabled: false);

        ResultWrapper<ParityTxTraceFromReplay> traces = TraceRaw(context, transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Signature!.V, Is.EqualTo(27).Or.EqualTo(28));
            AssertTracedFromAddressA(traces);
        }
    }

    // v 29 and 30 carry the pre-EIP-155 recovery id of 27 and 28 but aren't valid legacy signatures.
    [Test]
    public async Task Trace_rawTransaction_rejects_a_legacy_signature_v_that_is_neither_pre_eip155_nor_eip155()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        Transaction transaction = SignedTransaction(TxType.Legacy, blockchain.SpecProvider.ChainId, isEip155Enabled: false);
        Signature signature = transaction.Signature!;
        transaction.Signature = new Signature(signature.R.Span, signature.S.Span, signature.V + 2);

        ResultWrapper<ParityTxTraceFromReplay> traces = TraceRaw(context, transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.ErrorCode, Is.EqualTo(ErrorCodes.TransactionRejected));
            Assert.That(traces.Result.Error, Is.EqualTo(TxErrorMessages.InvalidTxSignature));
        }
    }

    [Test]
    public async Task Trace_rawTransaction_rejects_a_high_s_signature(
        [Values(TxType.Legacy, TxType.EIP1559)] TxType type, [Values] bool otherChain)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        ulong chainId = blockchain.SpecProvider.ChainId;
        Transaction transaction = SignedTransaction(type, otherChain ? chainId + 1 : chainId);
        Signature signature = transaction.Signature!;
        UInt256 highS = SecP256k1Curve.N - new UInt256(signature.SAsSpan, isBigEndian: true);
        transaction.Signature = new Signature(new UInt256(signature.RAsSpan, isBigEndian: true), highS, signature.V);

        ResultWrapper<ParityTxTraceFromReplay> traces = TraceRaw(context, transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces.ErrorCode, Is.EqualTo(ErrorCodes.TransactionRejected));
            Assert.That(traces.Result.Error, Is.EqualTo(TxErrorMessages.InvalidTxSignature));
        }
    }

    // As eth_sendRawTransaction, a spec that doesn't validate chain ids accepts a legacy signature for another chain,
    // and recovery then uses the signature's chain id; a typed transaction's chain id is checked regardless.
    [Test]
    public async Task Trace_rawTransaction_follows_a_spec_that_does_not_validate_legacy_chain_ids(
        [Values(TxType.Legacy, TxType.EIP1559)] TxType type)
    {
        Context context = new();
        await context.Build(new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { ValidateChainId = false }));
        using TestRpcBlockchain blockchain = context.Blockchain;
        ulong chainId = blockchain.SpecProvider.ChainId;

        ResultWrapper<ParityTxTraceFromReplay> traces = TraceRaw(context, SignedTransaction(type, chainId + 1));

        using (Assert.EnterMultipleScope())
        {
            if (type == TxType.Legacy)
            {
                AssertTracedFromAddressA(traces);
            }
            else
            {
                Assert.That(traces.ErrorCode, Is.EqualTo(ErrorCodes.TransactionRejected));
                Assert.That(traces.Result.Error, Is.EqualTo(TxErrorMessages.InvalidTxChainId(chainId, chainId + 1)));
            }
        }
    }

    private static Transaction SignedTransaction(TxType type, ulong chainId, bool isEip155Enabled = true) =>
        Build.A.Transaction
            .WithType(type)
            .WithChainId(type == TxType.Legacy ? null : chainId)
            .WithTo(TestItem.AddressC)
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(0)
            .SignedAndResolved(new EthereumEcdsa(chainId), TestItem.PrivateKeyA, isEip155Enabled)
            .TestObject;

    private static ResultWrapper<ParityTxTraceFromReplay> TraceRaw(Context context, Transaction transaction) =>
        context.TraceRpcModule.trace_rawTransaction(TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes, ["trace"]);

    private static void AssertTracedFromAddressA(ResultWrapper<ParityTxTraceFromReplay> traces)
    {
        Assert.That(traces.Result.ResultType, Is.EqualTo(ResultType.Success), traces.Result.Error);
        Assert.That(traces.Data?.Action?.From, Is.EqualTo(TestItem.AddressA));
    }

    [Test]
    public async Task Trace_call_simple_tx_test()
    {
        Context context = new();
        await context.Build();
        object transaction = new { from = "0xaaaaaaaa8583de65cc752fe3fad5098643244d22", to = "0xd6a8d04cb9846759416457e2c593c99390092df6", gas = "0xf4240" };
        string[] traceTypes = { "trace" };
        string blockParameter = "latest";
        string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xaaaaaaaa8583de65cc752fe3fad5098643244d22\",\"gas\":\"0xef038\",\"input\":\"0x\",\"to\":\"0xd6a8d04cb9846759416457e2c593c99390092df6\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null},\"id\":67}";

        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_call", transaction, traceTypes, blockParameter);

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedResult)).Using(JToken.EqualityComparer));
    }

    // Sends one call with the given fee fields through trace_call, or through trace_callMany after a free call, and
    // the same call through eth_call, on a London chain whose head has a base fee.
    private static async Task<(string Trace, string Eth)> TraceAndEthCall(string method, string feeFields, bool streaming)
    {
        Context context = new();
        await context.Build(new TestSpecProvider(new OverridableReleaseSpec(London.Instance) { Eip1559TransitionBlock = 1 }));
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Assert.That(blockchain.BlockTree.Head!.BaseFeePerGas, Is.GreaterThan(UInt256.Zero), "precondition: the head block has a base fee");
        string free = $$"""{"from":"{{TestItem.AddressA}}","to":"{{TestItem.AddressB}}","gas":"0x186a0"}""";
        string priced = $$"""{"from":"{{TestItem.AddressA}}","to":"{{TestItem.AddressB}}","gas":"0x186a0",{{feeFields}}}""";
        using JsonDocument call = JsonDocument.Parse(priced);
        using JsonDocument calls = JsonDocument.Parse($$"""[[{{free}},["trace"]],[{{priced}},["trace"]]]""");

        string trace = method == "trace_call"
            ? await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, call.RootElement, new[] { "trace" }, "latest")
            : await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, calls.RootElement, "latest");
        return (trace, await RpcTest.TestSerializedRequest(blockchain.EthRpcModule, "eth_call", call.RootElement, "latest"));
    }

    [Test]
    public async Task Trace_call_and_callMany_reject_a_priority_fee_above_the_fee_cap_as_eth_call_does(
        [Values("trace_call", "trace_callMany")] string method, [Values] bool streaming)
    {
        // Only a priority fee: the fee cap defaults to zero, below both the priority fee and the base fee.
        (string trace, string eth) = await TraceAndEthCall(method, "\"maxPriorityFeePerGas\":\"0x1\"", streaming);

        string expected = $"{TxErrorMessages.TipAboveFeeCap}: address {TestItem.AddressA.ToString(withEip55Checksum: true)}, maxPriorityFeePerGas: 1, maxFeePerGas: 0";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(trace)["error"]?["code"]?.Value<int>(), Is.EqualTo(ErrorCodes.InvalidInput), trace);
            Assert.That(JToken.Parse(trace)["error"]?["message"]?.Value<string>(), Is.EqualTo(expected), trace);
            Assert.That(JToken.Parse(eth)["error"]?["message"]?.Value<string>(), Does.Contain(expected), eth);
        }
    }

    [Test]
    public async Task Trace_call_and_callMany_run_fees_the_tip_check_accepts(
        [Values("trace_call", "trace_callMany")] string method,
        [Values(
            "\"maxFeePerGas\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\"",
            "\"maxFeePerGas\":\"0x4a817c800\",\"maxPriorityFeePerGas\":\"0x1\"",
            "\"gasPrice\":\"0x4a817c800\"")] string feeFields)
    {
        (string trace, string eth) = await TraceAndEthCall(method, feeFields, streaming: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(trace)["error"], Is.Null, trace);
            Assert.That(JToken.Parse(eth)["error"], Is.Null, eth);
        }
    }

    // Shaped like trace-interop's field-null-members probe: a null `input` after `data`, and the other optional members null.
    [Test]
    public async Task Trace_call_null_input_keeps_data()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        string fields = $"\"from\":\"{TestItem.AddressA}\",\"gas\":\"0x493e0\",\"gasPrice\":\"0x77359400\",\"data\":\"0x602a60005260206000f3\"";
        using JsonDocument withNulls = JsonDocument.Parse(
            $"{{{fields},\"input\":null,\"accessList\":null,\"authorizationList\":null,\"blobVersionedHashes\":null,\"chainId\":null,\"maxFeePerBlobGas\":null,\"maxFeePerGas\":null,\"maxPriorityFeePerGas\":null,\"nonce\":null,\"type\":null,\"value\":null}}");
        using JsonDocument omitted = JsonDocument.Parse($"{{{fields}}}");

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", withNulls.RootElement, new[] { "trace" }, "latest");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(response)["result"]?["output"]?.Value<string>(), Is.EqualTo($"0x{new UInt256(42).ToBigEndian().ToHexString()}"), response);
            Assert.That(response, Is.EqualTo(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", omitted.RootElement, new[] { "trace" }, "latest")));
        }
    }

    // Shaped like trace-interop's field-null-blobVersionedHashes-unpriced and field-null-authorizationList-unpriced probes.
    [TestCase("blobVersionedHashes", RpcTransactionErrors.AtLeastOneBlobInBlobTransaction)]
    [TestCase("authorizationList", TxErrorMessages.NotAllowedCreateTransaction)]
    public async Task Trace_call_null_blob_or_authorization_list_selects_no_type(string list, string typeError)
    {
        Context context = new();
        await context.Build(new TestSpecProvider(Prague.Instance));
        using TestRpcBlockchain blockchain = context.Blockchain;
        string call = $"\"from\":\"{TestItem.AddressA}\",\"gas\":\"0x493e0\",\"data\":\"0x602a60005260206000f3\"";
        using JsonDocument withNull = JsonDocument.Parse($"{{{call},\"{list}\":null}}");
        using JsonDocument withList = JsonDocument.Parse($"{{{call},\"{list}\":[]}}");
        using JsonDocument omitted = JsonDocument.Parse($"{{{call}}}");

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", withNull.RootElement, new[] { "trace" }, "latest");
        string typed = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", withList.RootElement, new[] { "trace" }, "latest");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(response)["result"]?["output"]?.Value<string>(), Is.EqualTo($"0x{new UInt256(42).ToBigEndian().ToHexString()}"), response);
            Assert.That(response, Is.EqualTo(await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", omitted.RootElement, new[] { "trace" }, "latest")));
            // A non-null list still selects its type, which can't create a contract.
            Assert.That(JToken.Parse(typed)["error"]?["message"]?.Value<string>(), Does.Contain(typeError), typed);
        }
    }

    private static readonly IEnumerable<(object, string[], string)> Trace_call_without_blockParameter_test_cases = [
        (new { from = TestItem.AddressA, to = "0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f", value = "0x0", gasPrice = "0x119e04a40a", gas = "0xf4240" }, ["trace"], $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{{\"action\":{{\"callType\":\"call\",\"from\":\"{TestItem.AddressA}\",\"gas\":\"0xef038\",\"input\":\"0x\",\"to\":\"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f\",\"value\":\"0x0\"}},\"result\":{{\"gasUsed\":\"0x0\",\"output\":\"0x\"}},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}}],\"vmTrace\":null}},\"id\":67}}"),
        (new { from = TestItem.AddressA, to = "0xa760e26aa76747020171fcf8bda108dfde8eb930", value = "0x0", gasPrice = "0x2108eea5bc", gas = "0xf4240" }, ["trace"], $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{{\"action\":{{\"callType\":\"call\",\"from\":\"{TestItem.AddressA}\",\"gas\":\"0xef038\",\"input\":\"0x\",\"to\":\"0xa760e26aa76747020171fcf8bda108dfde8eb930\",\"value\":\"0x0\"}},\"result\":{{\"gasUsed\":\"0x0\",\"output\":\"0x\"}},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}}],\"vmTrace\":null}},\"id\":67}}")
    ];
    [TestCaseSource(nameof(Trace_call_without_blockParameter_test_cases))]
    public async Task Trace_call_without_blockParameter_test((object transaction, string[] traceTypes, string expectedResult) testCase)
    {
        Context context = new();
        await context.Build();
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_call", testCase.transaction, testCase.traceTypes);

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(testCase.expectedResult)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Trace_callMany_internal_transactions_test()
    {
        Context context = new();
        await context.Build();

        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);

        Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressC)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        TransactionForRpc txForRpc1 = TransactionForRpc.FromTransaction(transaction1);
        string[] traceTypes1 = { "Trace" };

        Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressA++).WithTo(TestItem.AddressD)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        await blockchain.AddBlock(transaction1, transaction2);

        TransactionForRpc txForRpc2 = TransactionForRpc.FromTransaction(transaction2);
        string[] traceTypes2 = { "Trace" };

        BlockParameter numberOrTag = new(16);
        TransactionForRpcWithTraceTypes tr1 = new();
        TransactionForRpcWithTraceTypes tr2 = new();
        tr1.Transaction = txForRpc1;
        tr1.TraceTypes = traceTypes1;
        tr2.Transaction = txForRpc2;
        tr2.TraceTypes = traceTypes2;

        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> tr = context.TraceRpcModule.trace_callMany(new(new(2) { tr1, tr2 }), numberOrTag);
        Assert.That(System.Linq.Enumerable.Count(tr.Data), Is.EqualTo(2));
    }

    [Test]
    public async Task Trace_callMany_is_blockParameter_optional_test()
    {
        Context context = new();
        await context.Build();
        string calls = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\",\"gas\":\"0xf4240\"}},[\"trace\"]],[{{\"from\":\"{TestItem.AddressB}\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"gasPrice\":\"0xb2b29a6dc\",\"gas\":\"0xf4240\"}},[\"trace\"]]]";

        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_callMany", calls, "latest");

        string serialized_without_blockParameter_param = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_callMany", calls);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":[{{\"vmTrace\":null,\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{{\"action\":{{\"callType\":\"call\",\"from\":\"{TestItem.AddressA}\",\"gas\":\"0xef038\",\"input\":\"0x\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\",\"value\":\"0x0\"}},\"result\":{{\"gasUsed\":\"0x0\",\"output\":\"0x\"}},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}}]}},{{\"vmTrace\":null,\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{{\"action\":{{\"callType\":\"call\",\"from\":\"{TestItem.AddressB}\",\"gas\":\"0xef038\",\"input\":\"0x\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"value\":\"0x0\"}},\"result\":{{\"gasUsed\":\"0x0\",\"output\":\"0x\"}},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}}]}}],\"id\":67}}"), serialized.Replace("\"", "\\\""));
            Assert.That(serialized_without_blockParameter_param, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":[{{\"vmTrace\":null,\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{{\"action\":{{\"callType\":\"call\",\"from\":\"{TestItem.AddressA}\",\"gas\":\"0xef038\",\"input\":\"0x\",\"to\":\"0x8cf85548ae57a91f8132d0831634c0fcef06e505\",\"value\":\"0x0\"}},\"result\":{{\"gasUsed\":\"0x0\",\"output\":\"0x\"}},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}}]}},{{\"vmTrace\":null,\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{{\"action\":{{\"callType\":\"call\",\"from\":\"{TestItem.AddressB}\",\"gas\":\"0xef038\",\"input\":\"0x\",\"to\":\"0xab736519b5433974059da38da74b8db5376942cd\",\"value\":\"0x0\"}},\"result\":{{\"gasUsed\":\"0x0\",\"output\":\"0x\"}},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}}]}}],\"id\":67}}"), serialized_without_blockParameter_param.Replace("\"", "\\\""));
        }
    }

    [Test]
    public async Task Trace_callMany_accumulates_state_changes()
    {
        Context context = new();
        await context.Build();

        string calls = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"1\",\"gas\":\"0xf4240\"}},[\"statediff\"]],[{{\"from\":\"{TestItem.AddressA}\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"1\",\"gas\":\"0xf4240\"}},[\"statediff\"]]]";

        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_callMany", calls);

        string expected =
            "{\"jsonrpc\":\"2.0\",\"result\":[{\"vmTrace\":null,\"output\":\"0x\",\"stateDiff\":{\"0x0000000000000000000000000000000000000000\":{\"balance\":{\"*\":{\"from\":\"0x2d\",\"to\":\"0x2e\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}},\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x3635c9adc5de9f09e5\",\"to\":\"0x3635c9adc5de9f09e4\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x3\",\"to\":\"0x4\"}},\"storage\":{}}},\"trace\":[]},{\"vmTrace\":null,\"output\":\"0x\",\"stateDiff\":{\"0x0000000000000000000000000000000000000000\":{\"balance\":{\"*\":{\"from\":\"0x2e\",\"to\":\"0x2f\"}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}},\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":{\"*\":{\"from\":\"0x3635c9adc5de9f09e4\",\"to\":\"0x3635c9adc5de9f09e3\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x4\",\"to\":\"0x5\"}},\"storage\":{}}},\"trace\":[]}],\"id\":67}";

        Assert.That(serialized, Is.EqualTo(expected), serialized.Replace("\"", "\\\""));
    }

    public enum CallFees { Omitted, ZeroGasPrice, ZeroFeeCaps, GasPrice, FeeCap }

    private static bool IsPriced(CallFees fees) => fees is CallFees.GasPrice or CallFees.FeeCap;

    private static readonly byte[] BaseFeeReturnCode = Prepare.EvmCode
        .Op(Instruction.BASEFEE)
        .PushData(0)
        .Op(Instruction.MSTORE)
        .PushData("0x20")
        .PushData("0x0")
        .Op(Instruction.RETURN)
        .Done;

    private static string Word(UInt256 value) => $"0x{value.ToBigEndian().ToHexString()}";

    private static async Task<(Context Context, Address Contract, UInt256 BaseFee)> BuildWithBaseFeeContract()
    {
        Context context = new();
        await context.Build(new TestSpecProvider(new OverridableReleaseSpec(London.Instance) { Eip1559TransitionBlock = 1 }));
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        await blockchain.AddBlock(Build.A.Transaction.WithNonce(nonce).WithTo(null).WithGasLimit(200_000)
            .WithType(TxType.EIP1559).WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei).WithChainId(blockchain.SpecProvider.ChainId)
            .WithData(Prepare.EvmCode.ForInitOf(BaseFeeReturnCode).Done)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        UInt256 baseFee = blockchain.BlockTree.Head!.BaseFeePerGas;
        Assert.That(baseFee, Is.GreaterThan(UInt256.Zero), "precondition: the head block has a base fee");
        return (context, ContractAddress.From(TestItem.AddressA, nonce), baseFee);
    }

    private static string BaseFeeCall(Address contract, CallFees fees, UInt256 baseFee)
    {
        string price = baseFee.ToHexString(skipLeadingZeros: true);
        string feeFields = fees switch
        {
            CallFees.Omitted => "",
            CallFees.ZeroGasPrice => ",\"gasPrice\":\"0x0\"",
            CallFees.ZeroFeeCaps => ",\"maxFeePerGas\":\"0x0\",\"maxPriorityFeePerGas\":\"0x0\"",
            CallFees.GasPrice => $",\"gasPrice\":\"{price}\"",
            _ => $",\"maxFeePerGas\":\"{price}\",\"maxPriorityFeePerGas\":\"0x0\"",
        };
        return $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{contract}\",\"gas\":\"0x186a0\"{feeFields}}}";
    }

    [Test]
    public async Task Trace_call_sees_the_base_fee_eth_call_sees([Values] CallFees fees, [Values] bool streaming)
    {
        (Context context, Address contract, UInt256 baseFee) = await BuildWithBaseFeeContract();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        using JsonDocument call = JsonDocument.Parse(BaseFeeCall(contract, fees, baseFee));

        string trace = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", call.RootElement, new[] { "trace" }, "latest");
        string eth = await RpcTest.TestSerializedRequest(blockchain.EthRpcModule, "eth_call", call.RootElement, "latest");

        string expected = Word(IsPriced(fees) ? baseFee : UInt256.Zero);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(trace)["result"]?["output"]?.Value<string>(), Is.EqualTo(expected), trace);
            Assert.That(JToken.Parse(eth)["result"]?.Value<string>(), Is.EqualTo(expected), eth);
        }
    }

    [Test]
    public async Task Trace_callMany_gives_each_call_the_base_fee_of_its_own_price([Values] bool streaming)
    {
        (Context context, Address contract, UInt256 baseFee) = await BuildWithBaseFeeContract();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        CallFees[] fees = [CallFees.Omitted, CallFees.GasPrice, CallFees.ZeroFeeCaps];
        using JsonDocument calls = JsonDocument.Parse(
            $"[{string.Join(",", fees.Select(f => $"[{BaseFeeCall(contract, f, baseFee)},[\"trace\",\"stateDiff\"]]"))}]");
        ulong nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls.RootElement, "latest");

        JToken[] results = [.. JToken.Parse(response)["result"] ?? new JArray()];
        Assert.That(results, Has.Length.EqualTo(fees.Length), response);
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < fees.Length; i++)
            {
                JToken? sender = results[i]["stateDiff"]?[TestItem.AddressA.ToString()];
                Assert.That(results[i]["output"]?.Value<string>(), Is.EqualTo(Word(IsPriced(fees[i]) ? baseFee : UInt256.Zero)), $"call {i}: {response}");
                // Each call runs on the state the previous one left, and only the priced one is charged.
                Assert.That(sender?["nonce"]?["*"]?["from"]?.Value<string>(), Is.EqualTo($"0x{nonce + (ulong)i:x}"), $"call {i}: {response}");
                Assert.That(sender?["balance"]?.Type, Is.EqualTo(IsPriced(fees[i]) ? JTokenType.Object : JTokenType.String), $"call {i}: {response}");
            }
        }
    }

    [Test]
    public async Task Trace_rawTransaction_sees_a_zero_base_fee_only_when_priced_at_zero([Values] bool priced)
    {
        (Context context, Address contract, UInt256 baseFee) = await BuildWithBaseFeeContract();
        using TestRpcBlockchain blockchain = context.Blockchain;
        Transaction transaction = Build.A.Transaction
            .WithNonce(blockchain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithTo(contract)
            .WithGasLimit(100_000)
            .WithGasPrice(priced ? baseFee : UInt256.Zero)
            .WithChainId(blockchain.SpecProvider.ChainId)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        ResultWrapper<ParityTxTraceFromReplay> trace = context.TraceRpcModule.trace_rawTransaction(TxDecoder.Instance.Encode(transaction).Bytes, ["trace"]);

        Assert.That(trace.Data.Output, Is.EqualTo((priced ? baseFee : UInt256.Zero).ToBigEndian()), trace.Result.Error);
    }

    [Test]
    public async Task Trace_replayBlockTransactions_transactions_deploying_contract()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);
        Address? contractAddress = ContractAddress.From(TestItem.AddressA, currentNonceAddressA);

        byte[] code = Prepare.EvmCode
            .Call(contractAddress, 50000)
            .Done;

        Transaction transaction1 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
            .WithSenderAddress(TestItem.AddressA)
            .WithData(code)
            .WithTo(null)
            .WithGasLimit(93548).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        Transaction transaction2 = Build.A.Transaction.WithNonce(currentNonceAddressA++)
            .WithSenderAddress(TestItem.AddressA)
            .WithData(code).SignedAndResolved(TestItem.PrivateKeyA)
            .WithTo(null)
            .WithGasLimit(93548).TestObject;

        string[] traceTypes = { "trace" };

        await blockchain.AddBlock(transaction1, transaction2);

        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> traces = context.TraceRpcModule.trace_replayBlockTransactions(new BlockParameter(blockchain.BlockFinder.FindLatestBlock()!.Number), traceTypes);
        Assert.That(System.Linq.Enumerable.Count(traces.Data), Is.EqualTo(2));
        Assert.That(traces.Data.ElementAt(0).Action!.From, Is.EqualTo(traces.Data.ElementAt(1).Action!.From));
        string serialized = new EthereumJsonSerializer().Serialize(traces.Data);
        Assert.That(serialized, Is.EqualTo("[{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"creationMethod\":\"create\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c70\",\"init\":\"0x60006000600060006000730ffd3e46594919c04bcfd4e146203c825567082861c350f1\",\"value\":\"0x1\"},\"result\":{\"address\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"code\":\"0x\",\"gasUsed\":\"0x79\"},\"subtraces\":1,\"traceAddress\":[],\"type\":\"create\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"gas\":\"0x9988\",\"input\":\"0x\",\"to\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[0],\"type\":\"call\"}],\"transactionHash\":\"0x8513c9083ec27fa8e3ca7e3ffa732d61562e2d17e2e1af6e773bc810dc4c3452\",\"vmTrace\":null},{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"creationMethod\":\"create\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x9c70\",\"init\":\"0x60006000600060006000730ffd3e46594919c04bcfd4e146203c825567082861c350f1\",\"value\":\"0x1\"},\"result\":{\"address\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"code\":\"0x\",\"gasUsed\":\"0xa3d\"},\"subtraces\":1,\"traceAddress\":[],\"type\":\"create\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x6b5887043de753ecfa6269f947129068263ffbe2\",\"gas\":\"0x8feb\",\"input\":\"0x\",\"to\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[0],\"type\":\"call\"}],\"transactionHash\":\"0xa6a56c7927deae778a749bcdab7bbf409c0d8a5d2420021a3ba328240ae832d8\",\"vmTrace\":null}]"));
    }

    [Test]
    public async Task Trace_replayBlockTransactions_stateDiff()
    {
        Context context = new();
        await context.Build();
        TestRpcBlockchain blockchain = context.Blockchain;
        ulong currentNonceAddressA = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        await blockchain.AddFunds(TestItem.AddressA, 10000.Ether);

        Transaction tx = Build.A.Transaction.WithNonce(currentNonceAddressA++)
            .WithValue(10)
            .WithTo(TestItem.AddressF)
            .WithGasLimit(50000)
            .WithGasPrice(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        blockchain.ReadOnlyState.TryGetAccount(TestItem.AddressA, out AccountStruct accountA);
        blockchain.ReadOnlyState.TryGetAccount(TestItem.AddressD, out AccountStruct accountD);
        blockchain.ReadOnlyState.TryGetAccount(TestItem.AddressF, out AccountStruct accountF);

        string[] traceTypes = ["stateDiff"];

        await blockchain.AddBlock(tx);

        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> traces = context.TraceRpcModule.trace_replayBlockTransactions(new BlockParameter(blockchain.BlockFinder.FindLatestBlock()!.Number), traceTypes);
        Assert.That(System.Linq.Enumerable.Count(traces.Data), Is.EqualTo(1));
        Dictionary<Address, ParityAccountStateChange> state = traces.Data.ElementAt(0).StateChanges!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Count, Is.EqualTo(3));
            Assert.That(state[TestItem.AddressA].Nonce!.Before, Is.EqualTo((UInt256)accountA.Nonce));
            Assert.That(state[TestItem.AddressD].Balance!.Before, Is.EqualTo(accountD.Balance));
            Assert.That(state[TestItem.AddressA].Balance!.Before, Is.EqualTo(accountA.Balance));
            Assert.That(state[TestItem.AddressF].Balance!.Before, Is.EqualTo(null));

            Assert.That(state[TestItem.AddressA].Nonce!.After, Is.EqualTo((UInt256)(accountA.Nonce + 1)));
            Assert.That(state[TestItem.AddressD].Balance!.After, Is.EqualTo(accountD.Balance + 21000 * tx.GasPrice));
            Assert.That(state[TestItem.AddressA].Balance!.After, Is.EqualTo(accountA.Balance - 21000 * tx.GasPrice - tx.Value));
            Assert.That(state[TestItem.AddressF].Balance!.After, Is.EqualTo(accountF.Balance + tx.Value));
        }
    }

    [Test]
    public async Task Trace_call_reports_a_call_failing_its_balance_precheck_as_a_failed_frame([Values] bool streaming)
    {
        Context context = new();
        await context.Build(new TestSpecProvider(Prague.Instance));
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;

        Address contract = new("0xc200000000000000000000000000000000000000");
        byte[] code = Prepare.EvmCode
            .CallWithValue(TestItem.AddressD, 50000, 1.Ether)
            .Op(Instruction.POP)
            .Call(TestItem.AddressD, 50000)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;
        object transaction = new { from = TestItem.AddressA, to = contract, gas = "0xf4240" };
        Dictionary<string, object> stateOverride = new() { [contract.ToString()] = new { code = code.ToHexString(true) } };

        string response = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule, "trace_call", transaction, new[] { "trace", "vmTrace" }, "latest", stateOverride);

        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement result = document.RootElement.GetProperty("result");
        JsonElement[] traces = result.GetProperty("trace").EnumerateArray().ToArray();
        JsonElement[] calls = result.GetProperty("vmTrace").GetProperty("ops").EnumerateArray()
            .Where(static op => op.GetProperty("cost").GetUInt64() > GasCostOf.CallStipend).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(traces, Has.Length.EqualTo(3), response);
            Assert.That(traces[0].GetProperty("subtraces").GetInt32(), Is.EqualTo(2), "root subtraces");
            Assert.That(traces[1].GetProperty("error").GetString(), Is.EqualTo("Insufficient balance for transfer"), "[0] error");
            Assert.That(traces[1].TryGetProperty("result", out _), Is.False, "[0] result");
            Assert.That(traces[1].GetProperty("subtraces").GetInt32(), Is.Zero, "[0] subtraces");
            Assert.That(traces[1].GetProperty("traceAddress").EnumerateArray().Select(static a => a.GetInt32()), Is.EqualTo(new[] { 0 }), "[0] address");
            Assert.That(traces[1].GetProperty("action").GetProperty("value").GetString(), Is.EqualTo("0xde0b6b3a7640000"), "[0] value");
            Assert.That(traces[2].TryGetProperty("error", out _), Is.False, "[1] error");
            Assert.That(traces[2].GetProperty("traceAddress").EnumerateArray().Select(static a => a.GetInt32()), Is.EqualTo(new[] { 1 }), "[1] address");
            Assert.That(calls.Select(static op => op.GetProperty("sub").ValueKind), Is.EqualTo(new[] { JsonValueKind.Null, JsonValueKind.Object }), "vmTrace subs");
        }
    }

    [TestCase(
        "Nonce increments from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","gas":"0xf4240"}""",
        "stateDiff",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"nonce":"0x123"}}""",
        """{"jsonrpc":"2.0","result":{"output":"0x","stateDiff":{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"=","code":"=","nonce":{"*":{"from":"0x123","to":"0x124"}},"storage":{}}},"trace":[],"vmTrace":null},"id":67}"""
    )]
    [TestCase(
        "Uses account balance from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f","value":"0x100","gas":"0xf4240"}""",
        "stateDiff",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x100"}}""",
        """{"jsonrpc":"2.0","result":{"output":"0x","stateDiff":{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":{"*":{"from":"0x100","to":"0x0"}},"code":"=","nonce":{"*":{"from":"0x0","to":"0x1"}},"storage":{}},"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f":{"balance":{"\u002B":"0x100"},"code":{"\u002B":"0x"},"nonce":{"\u002B":"0x0"},"storage":{}}},"trace":[],"vmTrace":null},"id":67}"""
    )]
    [TestCase(
        "Executes code from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0x60fe47b1112233445566778899001122334455667788990011223344556677889900112233445566778899001122","gas":"0xf4240"}""",
        "stateDiff",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x6080604052348015600e575f80fd5b50600436106030575f3560e01c80632a1afcd914603457806360fe47b114604d575b5f80fd5b603b5f5481565b60405190815260200160405180910390f35b605c6058366004605e565b5f55565b005b5f60208284031215606d575f80fd5b503591905056fea2646970667358221220fd4e5f3894be8e57fc7460afebb5c90d96c3486d79bf47b00c2ed666ab2f82b364736f6c634300081a0033"}}""",
        """{"jsonrpc":"2.0","result":{"output":"0x","stateDiff":{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":{"\u002B":"0x0"},"code":{"\u002B":"0x"},"nonce":{"\u002B":"0x1"},"storage":{}},"0xc200000000000000000000000000000000000000":{"balance":"=","code":"=","nonce":"=","storage":{"0x0000000000000000000000000000000000000000000000000000000000000000":{"*":{"from":"0x0000000000000000000000000000000000000000000000000000000000000000","to":"0x1122334455667788990011223344556677889900112233445566778899001122"}}}}},"trace":[],"vmTrace":null},"id":67}"""
    )]
    [TestCase(
        "Uses storage from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0x60fe47b1112233445566778899001122334455667788990011223344556677889900112233445566778899001122","gas":"0xf4240"}""",
        "stateDiff",
        """{"0xc200000000000000000000000000000000000000":{"state": {"0x0000000000000000000000000000000000000000000000000000000000000000": "0x0000000000000000000000000000000000000000000000000000000000123456"}, "code":"0x6080604052348015600e575f80fd5b50600436106030575f3560e01c80632a1afcd914603457806360fe47b114604d575b5f80fd5b603b5f5481565b60405190815260200160405180910390f35b605c6058366004605e565b5f55565b005b5f60208284031215606d575f80fd5b503591905056fea2646970667358221220fd4e5f3894be8e57fc7460afebb5c90d96c3486d79bf47b00c2ed666ab2f82b364736f6c634300081a0033"}}""",
        """{"jsonrpc":"2.0","result":{"output":"0x","stateDiff":{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":{"\u002B":"0x0"},"code":{"\u002B":"0x"},"nonce":{"\u002B":"0x1"},"storage":{}},"0xc200000000000000000000000000000000000000":{"balance":"=","code":"=","nonce":"=","storage":{"0x0000000000000000000000000000000000000000000000000000000000000000":{"*":{"from":"0x0000000000000000000000000000000000000000000000000000000000123456","to":"0x1122334455667788990011223344556677889900112233445566778899001122"}}}}},"trace":[],"vmTrace":null},"id":67}"""
    )]
    [TestCase(
        "Executes precompile using overridden address",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0x0000000000000000000000000000000000123456","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055","gas":"0xf4240"}""",
        "trace",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0x0000000000000000000000000000000000123456", "code": "0x"}}""",
        """{"jsonrpc":"2.0","result":{"output":"0x000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099","stateDiff":null,"trace":[{"action":{"callType":"call","from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","gas":"0xee9b8","input":"0xb6e16d27ac5ab427a7f68900ac5559ce272dc6c37c82b3e052246c82244c50e4000000000000000000000000000000000000000000000000000000000000001c7b8b1991eb44757bc688016d27940df8fb971d7c87f77a6bc4e938e3202c44037e9267b0aeaa82fa765361918f2d8abd9cdd86e64aa6f2b81d3c4e0b69a7b055","to":"0x0000000000000000000000000000000000123456","value":"0x0"},"result":{"gasUsed":"0xbb8","output":"0x000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"},"subtraces":0,"traceAddress":[],"type":"call"}],"vmTrace":null},"id":67}"""
    )]
    public async Task Trace_call_with_state_override(string name, string transactionJson, string traceType, string stateOverrideJson, string expectedResult)
    {
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        Context context = new();
        await context.Build(new TestSpecProvider(Prague.Instance));
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule,
            "trace_call", transaction, new[] { traceType }, "latest", stateOverride);

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedResult)).Using(JToken.EqualityComparer));
    }

    /// <summary>A CREATE colliding with an existing account fails before any action is reported, so its root
    /// carries the error alone.</summary>
    [Test]
    public async Task Trace_call_create_address_collision_reports_the_error_without_a_result([Values] bool streaming)
    {
        Context context = new();
        await context.Build(new TestSpecProvider(Prague.Instance));
        context.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Address sender = TestItem.AddressF;
        Address collision = ContractAddress.From(sender, 0);
        object call = new { from = sender, input = "0x6000", gas = "0xf4240" };
        object? stateOverride = JsonSerializer.Deserialize<object>($"{{\"{collision}\":{{\"code\":\"0x6000\"}}}}");

        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", call, new[] { "trace" }, "latest", stateOverride);
        JToken? root = JToken.Parse(response)["result"]?["trace"]?[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root?["error"]?.Value<string>(), Is.Not.Null, response);
            Assert.That(root?["result"], Is.Null, response);
        }
    }

    [TestCase(
        """{"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0xc200000000000000000000000000000000000000","gas":"0xf4240"}""",
        "stateDiff",
        """{"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099":{"balance":"0x123", "nonce": "0x123"}}"""
    )]
    [TestCase(
        """{"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0xc200000000000000000000000000000000000000","input":"0xf8b2cb4f000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099","gas":"0xf4240"}""",
        "trace",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033"}}"""
    )]
    public async Task Trace_call_with_state_override_does_not_affect_other_calls(string transactionJson, string traceType, string stateOverrideJson)
    {
        object? transaction = JsonSerializer.Deserialize<object>(transactionJson);
        object? stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        Context context = new();
        await context.Build();

        string[] traceTypes = new[] { traceType };

        string resultOverrideBefore = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call",
            transaction, traceTypes, null, stateOverride);

        string resultNoOverride = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call",
            transaction, traceTypes, null);

        string resultOverrideAfter = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call",
            transaction, traceTypes, null, stateOverride);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(resultOverrideBefore), Is.EqualTo(JToken.Parse(resultOverrideAfter)).Using(JToken.EqualityComparer));
            Assert.That(JToken.Parse(resultNoOverride), Is.Not.EqualTo(JToken.Parse(resultOverrideAfter)).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task Trace_call_caps_gas_to_gas_cap()
    {
        Context context = new();
        await context.Build();
        ulong gasCap = 50_000;
        IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        TransactionForRpc call = new LegacyTransactionForRpc
        {
            From = TestItem.AddressA,
            To = TestItem.AddressC,
            Gas = 100_000
        };

        ResultWrapper<ParityTxTraceFromReplay> traces = context.TraceRpcModule.trace_call(call, ["trace"]);

        Assert.That(traces.Data.Action!.Gas, Is.LessThan(gasCap));
    }

    [Test]
    public async Task Trace_callMany_caps_gas_to_gas_cap()
    {
        Context context = new();
        await context.Build();
        ulong gasCap = 50_000;
        IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> traces = TraceCallMany(context, gas: 100_000);

        Assert.That(traces.Data.Single().Action!.Gas, Is.LessThan(gasCap));
    }

    [Test]
    public async Task Trace_callMany_without_gas_defaults_to_gas_cap_not_block_gas_limit()
    {
        Context context = new();
        await context.Build();
        ulong blockGasLimit = context.Blockchain.BlockTree.Head!.Header.GasLimit;
        ulong gasCap = blockGasLimit * 10;
        IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> omittedGasTraces = TraceCallMany(context);
        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> explicitGasCapTraces = TraceCallMany(context, gas: gasCap);

        Assert.That(omittedGasTraces.Result.Error, Is.Null);
        Assert.That(explicitGasCapTraces.Result.Error, Is.Null);
        Assert.That(omittedGasTraces.Data.Single().Action!.Gas, Is.EqualTo(explicitGasCapTraces.Data.Single().Action!.Gas));
    }

    [Test]
    public async Task Trace_callMany_with_zero_gas_keeps_literal_zero_gas_semantics()
    {
        Context context = new();
        await context.Build();
        IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = 50_000;

        // Force enumeration with .Single(): the intrinsic-gas check is hit inside
        // TraceBlock, surfaced through the lazy IEnumerable returned in Data.
        Assert.That(() => TraceCallMany(context, gas: 0).Data.Single(),
            Throws.InstanceOf<InvalidTransactionException>()
                .With.Message.Contains("intrinsic gas too low"));
    }

    private static ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> TraceCallMany(Context context, ulong? gas = null) =>
        context.TraceRpcModule.trace_callMany(
            new(new(1)
            {
                new()
                {
                    Transaction = new LegacyTransactionForRpc
                    {
                        From = TestItem.AddressA,
                        To = TestItem.AddressC,
                        Gas = gas
                    },
                    TraceTypes = ["trace"]
                }
            }));

    [TestCase(0, 0, false)]
    [TestCase(2, 2, false)]
    [TestCase(3, 3, false)]
    [TestCase(1024, 1024, false)]
    [TestCase(1025, 0, true)]
    public void TraceCallManyRequest_enforces_hard_limit(int callCount, int expectedCount, bool shouldThrow)
    {
        string call = """[{"from":"0x0000000000000000000000000000000000000001","to":"0x0000000000000000000000000000000000000002"},["trace"]]""";
        string json = "[" + string.Join(",", Enumerable.Repeat(call, callCount)) + "]";

        using JsonDocument doc = JsonDocument.Parse(json);
        using TraceCallManyRequest request = new();

        if (shouldThrow)
        {
            Action act = () => request.ReadJson(doc.RootElement, EthereumJsonSerializer.JsonOptions);
            Assert.That(act, Throws.TypeOf<JsonException>().With.Message.Contains(@"Too many calls"));
        }
        else
        {
            request.ReadJson(doc.RootElement, EthereumJsonSerializer.JsonOptions);
            Assert.That(request.Calls, Has.Count.EqualTo(expectedCount));
        }
    }

    [Test]
    public async Task Trace_rawTransaction_rejection_returns_complete_error(
        [Values] bool streaming, [Values("trace", "vmTrace")] string traceType)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        Transaction transaction = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithGasLimit(100_000)
            .WithValue(10_000.Ether)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        string serialized = await RpcTest.TestSerializedRequest(context.TraceRpcModule,
            "trace_rawTransaction", TxDecoder.Instance.Encode(transaction).Bytes, new[] { traceType });
        using JsonDocument document = JsonDocument.Parse(serialized);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.RootElement.GetProperty("id").GetInt32(), Is.EqualTo(67));
            Assert.That(document.RootElement.GetProperty("error").GetProperty("message").GetString(),
                Does.Contain("insufficient"));
            Assert.That(document.RootElement.TryGetProperty("result", out _), Is.False);
        }
    }

    [Test]
    public async Task Trace_rawTransaction_preserves_signed_gas_or_rejects_above_cap(
        [Values(null, 0UL, 50_000UL, 100_000UL, 200_000UL)] ulong? gasCap,
        [Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        IJsonRpcConfig config = blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;
        config.EnableTracingStreamMode = streaming;

        Transaction transaction = Build.A.Transaction
            .WithTo(TestItem.AddressC)
            .WithGasLimit(100_000)
            .WithValue(0)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(0)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        string serialized = await RpcTest.TestSerializedRequest(context.TraceRpcModule,
            "trace_rawTransaction", TxDecoder.Instance.Encode(transaction).Bytes, new[] { "trace" });
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement response = document.RootElement;
        if (gasCap is > 0 and < 100_000)
        {
            Assert.That(response.TryGetProperty("error", out JsonElement error), Is.True, serialized);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ClientLimitExceededError));
                Assert.That(error.GetProperty("message").GetString(), Does.Contain("gas").And.Contain("cap"));
                Assert.That(response.TryGetProperty("result", out _), Is.False);
            }
        }
        else
        {
            Assert.That(response.TryGetProperty("result", out JsonElement result), Is.True, serialized);
            Assert.That(result.GetProperty("trace")[0].GetProperty("action").GetProperty("gas").GetString(),
                Is.EqualTo("0x13498")); // 100,000 signed gas minus 21,000 intrinsic gas.
        }
    }

    [Test]
    public void trace_transaction_WhenReceiptPointsToNonCanonicalBlock_ReturnsNotCanonicalError()
    {
        TraceRpcModule module = BuildModuleWithNonCanonicalReceipt(TestItem.KeccakA, TestItem.KeccakB);

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> result = module.trace_transaction(TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure), "must not proceed on a block that is not on the canonical chain");
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput), "non-canonical block lookup should signal an invalid request, not a server error");
            Assert.That(result.Result.Error, Does.Contain("not canonical"), "error message must identify the cause so callers can distinguish this from a missing block");
        }
    }

    [Test]
    public void trace_replayTransaction_WhenReceiptPointsToNonCanonicalBlock_ReturnsNotCanonicalError()
    {
        TraceRpcModule module = BuildModuleWithNonCanonicalReceipt(TestItem.KeccakA, TestItem.KeccakB);

        ResultWrapper<ParityTxTraceFromReplay?> result = module.trace_replayTransaction(TestItem.KeccakA, ["trace"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure), "must not proceed on a block that is not on the canonical chain");
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput), "non-canonical block lookup should signal an invalid request, not a server error");
            Assert.That(result.Result.Error, Does.Contain("not canonical"), "error message must identify the cause so callers can distinguish this from a missing block");
        }
    }

    [Test]
    public void trace_transaction_WhenTraceNonCanonicalAndReceiptPointsToNonCanonicalBlock_ProceedsBeyondCanonicalCheck()
    {
        TraceRpcModule module = BuildModuleWithNonCanonicalReceipt(TestItem.KeccakA, TestItem.KeccakB, traceNonCanonical: true);

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> result = module.trace_transaction(TestItem.KeccakA, traceNonCanonical: true);

        Assert.That(result.Result.Error, Does.Not.Contain("not canonical"), "traceNonCanonical=true must bypass the canonical block check");
    }

    [Test]
    public void trace_replayTransaction_WhenTraceNonCanonicalAndReceiptPointsToNonCanonicalBlock_ProceedsBeyondCanonicalCheck()
    {
        TraceRpcModule module = BuildModuleWithNonCanonicalReceipt(TestItem.KeccakA, TestItem.KeccakB, traceNonCanonical: true);

        ResultWrapper<ParityTxTraceFromReplay?> result = module.trace_replayTransaction(TestItem.KeccakA, ["trace"], traceNonCanonical: true);

        Assert.That(result.Result.Error, Does.Not.Contain("not canonical"), "traceNonCanonical=true must bypass the canonical block check");
    }

    [Test]
    public async Task Trace_call_preserves_output_without_action_traces(
        [Values] bool streaming, [Values] bool revert, [Values] bool stateDiff)
    {
        Context context = new();
        await context.Build(new TestSpecProvider(Prague.Instance));
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        string terminalOpcode = revert ? "fd" : "f3";
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"{{TestItem.AddressC}}","gas":"0x186a0"}""");
        object? stateOverride = JsonSerializer.Deserialize<object>(
            $$$"""{"{{{TestItem.AddressC}}}":{"code":"0x602a60005260206000{{{terminalOpcode}}}"}}""");

        string serialized = await RpcTest.TestSerializedRequest(context.TraceRpcModule,
            "trace_call", transaction, stateDiff ? new[] { "stateDiff" } : [], "latest", stateOverride);
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement result = document.RootElement.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("output").GetString(), Is.EqualTo("0x" + new string('0', 62) + "2a"));
            Assert.That(result.GetProperty("trace").GetArrayLength(), Is.Zero);
            Assert.That(result.GetProperty("vmTrace").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(result.GetProperty("stateDiff").ValueKind,
                Is.EqualTo(stateDiff ? JsonValueKind.Object : JsonValueKind.Null));
        }
    }

    [Test]
    public async Task Trace_callMany_accepts_empty_trace_selection([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        object? calls = JsonSerializer.Deserialize<object>(
            $$"""[[{"from":"{{TestItem.AddressA}}","to":"0x0000000000000000000000000000000000000004","data":"0x1234","gas":"0x186a0"},[]],[{"from":"{{TestItem.AddressA}}","to":"0x0000000000000000000000000000000000000004","data":"0x5678","gas":"0x186a0"},[]]]""");

        string serialized = await RpcTest.TestSerializedRequest(context.TraceRpcModule,
            "trace_callMany", calls, "latest");
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.That(result.GetArrayLength(), Is.EqualTo(2));
        string[] expectedOutputs = ["0x1234", "0x5678"];
        for (int i = 0; i < expectedOutputs.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result[i].GetProperty("output").GetString(), Is.EqualTo(expectedOutputs[i]));
                Assert.That(result[i].GetProperty("trace").GetArrayLength(), Is.Zero);
                Assert.That(result[i].GetProperty("stateDiff").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(result[i].GetProperty("vmTrace").ValueKind, Is.EqualTo(JsonValueKind.Null));
            }
        }
    }

    [Test]
    public async Task Trace_callMany_rejects_null_trace_selection_as_invalid_params()
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        object? calls = JsonSerializer.Deserialize<object>(
            $$"""[[{"from":"{{TestItem.AddressA}}","to":"0x0000000000000000000000000000000000000004","data":"0x1234","gas":"0x186a0"},null]]""");

        string serialized = await RpcTest.TestSerializedRequest(context.TraceRpcModule,
            "trace_callMany", calls, "latest");
        using JsonDocument document = JsonDocument.Parse(serialized);
        Assert.That(document.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    public async Task Trace_call_reports_selfdestruct_deletion_only_before_cancun(
        [Values] bool streaming, [Values] bool cancun)
    {
        JsonElement change = await TraceCallStateDiffOfAddressC(
            new TestSpecProvider(cancun ? Cancun.Instance : Shanghai.Instance),
            streaming, """{"code":"0x6000ff","balance":"0x64","nonce":"0x1"}""");
        using (Assert.EnterMultipleScope())
        {
            if (cancun)
            {
                Assert.That(change.GetProperty("code").GetString(), Is.EqualTo("="));
                Assert.That(change.GetProperty("nonce").GetString(), Is.EqualTo("="));
                Assert.That(change.GetProperty("balance").GetProperty("*").GetProperty("to").GetString(),
                    Is.EqualTo("0x0"));
            }
            else
            {
                Assert.That(change.GetProperty("code").GetProperty("-").GetString(), Is.EqualTo("0x6000ff"));
                Assert.That(change.GetProperty("nonce").GetProperty("-").GetString(), Is.EqualTo("0x1"));
                Assert.That(change.GetProperty("balance").GetProperty("-").GetString(), Is.EqualTo("0x64"));
            }
        }
    }

    [Test]
    public async Task Trace_call_reports_eip161_removal_of_empty_account_as_deletion([Values] bool streaming)
    {
        JsonElement change = await TraceCallStateDiffOfAddressC(null, streaming, """{"balance":"0x0"}""");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.GetProperty("balance").GetRawText(), Is.EqualTo("""{"-":"0x0"}"""));
            Assert.That(change.GetProperty("nonce").GetRawText(), Is.EqualTo("""{"-":"0x0"}"""));
            Assert.That(change.GetProperty("code").GetRawText(), Is.EqualTo("""{"-":"0x"}"""));
        }
    }

    private static async Task<JsonElement> TraceCallStateDiffOfAddressC(ISpecProvider? specProvider, bool streaming, string accountOverrideJson)
    {
        Context context = new();
        await context.Build(specProvider);
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;
        object? transaction = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressA}}","to":"{{TestItem.AddressC}}","gas":"0x186a0"}""");
        object? stateOverride = JsonSerializer.Deserialize<object>($$$"""{"{{{TestItem.AddressC}}}":{{{accountOverrideJson}}}}""");

        string serialized = await RpcTest.TestSerializedRequest(context.TraceRpcModule,
            "trace_call", transaction, new[] { "stateDiff" }, "latest", stateOverride);
        using JsonDocument document = JsonDocument.Parse(serialized);
        return document.RootElement.GetProperty("result").GetProperty("stateDiff")
            .GetProperty(TestItem.AddressC.ToString().ToLowerInvariant()).Clone();
    }

    private static IEnumerable<TestCaseData> StreamingEquivalenceCases()
    {
        string callManyParams = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"1\",\"gas\":\"0xf4240\"}},[\"statediff\"]],[{{\"from\":\"{TestItem.AddressA}\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"1\",\"gas\":\"0xf4240\"}},[\"statediff\"]]]";

        yield return new TestCaseData(
                "trace_replayBlockTransactions",
                new object?[] { "latest", new[] { "trace", "stateDiff" } },
                /*byteIdentical:*/ false)
            .SetName("trace_replayBlockTransactions (Replay, vmTrace field reorder OK)");

        yield return new TestCaseData(
                "trace_callMany",
                new object?[] { callManyParams },
                /*byteIdentical:*/ false)
            .SetName("trace_callMany (Replay, vmTrace field reorder OK)");

        yield return new TestCaseData(
                "trace_block",
                new object?[] { "latest" },
                /*byteIdentical:*/ true)
            .SetName("trace_block (Store, byte-identical)");

        yield return new TestCaseData(
                "trace_filter",
                new object?[] { "{\"fromBlock\":\"0x3\",\"toBlock\":\"0x5\"}" },
                /*byteIdentical:*/ true)
            .SetName("trace_filter (Store, byte-identical)");
    }

    [TestCaseSource(nameof(StreamingEquivalenceCases))]
    public async Task Streaming_matches_buffered(string method, object?[] parameters, bool byteIdentical)
    {
        Context context = new();
        await context.Build();
        IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();

        config.EnableTracingStreamMode = false;
        string buffered = await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, parameters);

        config.EnableTracingStreamMode = true;
        string streamed = await RpcTest.TestSerializedRequest(context.TraceRpcModule, method, parameters);

        if (byteIdentical)
        {
            Assert.That(streamed, Is.EqualTo(buffered));
        }
        else
        {
            Assert.That(JToken.Parse(streamed), Is.EqualTo(JToken.Parse(buffered)).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task Streaming_vmTrace_matches_buffered_with_real_opcodes()
    {
        Context context = new();
        await context.Build();
        IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();

        // Contract creation with init code that exercises PUSH1, PUSH1, SSTORE, PUSH1, MSTORE, RETURN.
        // Produces 6 opcodes in the vmTrace + 1 storage write + memory write.
        string bytecode = "0x60016000556020600060f3";
        string calls = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":null,\"data\":\"{bytecode}\",\"gas\":\"0xf4240\"}},[\"vmTrace\",\"trace\",\"stateDiff\"]]]";

        config.EnableTracingStreamMode = false;
        string buffered = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls);

        config.EnableTracingStreamMode = true;
        string streamed = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls);

        JToken bufferedTrace = JToken.Parse(buffered);
        JToken ops = bufferedTrace["result"]![0]!["vmTrace"]!["ops"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ops[0]!["ex"]!["push"]![0]!.Value<string>(), Is.EqualTo("0x1"));
            Assert.That(ops[2]!["ex"]!["store"]!["key"]!.Value<string>(), Is.EqualTo("0x0"));
            Assert.That(ops[2]!["ex"]!["store"]!["val"]!.Value<string>(), Is.EqualTo("0x1"));
            Assert.That(JToken.Parse(streamed), Is.EqualTo(bufferedTrace).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task VmTrace_create_cost_includes_forwarded_gas([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;

        // PUSH1 0 (size), PUSH1 0 (offset), PUSH1 0 (value), CREATE, STOP: the empty-initcode CREATE is ops[3].
        string calls = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":null,\"data\":\"0x600060006000f000\",\"gas\":\"0xf4240\"}},[\"vmTrace\",\"trace\"]]]";
        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls);
        JToken result = JToken.Parse(response)["result"]![0]!;

        ulong forwardedGas = Convert.ToUInt64(result["trace"]![1]!["action"]!["gas"]!.Value<string>(), 16);
        Assert.That(result["vmTrace"]!["ops"]![3]!["cost"]!.Value<ulong>(), Is.EqualTo(GasCostOf.Create + forwardedGas), response);
    }

    [Test]
    public async Task VmTrace_store_does_not_depend_on_stateDiff_selection([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;

        // PUSH1 1, PUSH1 0, SSTORE, PUSH1 0x20, PUSH1 0, RETURN: the SSTORE is ops[2].
        const string bytecode = "0x60016000556020600060f3";
        JToken vmTraceOnly = await TraceCallVmTrace(context, bytecode, "vmTrace");
        JToken withStateDiff = await TraceCallVmTrace(context, bytecode, "vmTrace", "stateDiff");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vmTraceOnly["ops"]![2]!["ex"]!["store"]!.Type, Is.EqualTo(JTokenType.Object));
            Assert.That(vmTraceOnly, Is.EqualTo(withStateDiff).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task VmTrace_call_mem_is_the_output_window([Values] bool streaming)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;

        // Fill the window [0x100, 0x120) with 0x11, then CALL the identity precompile with the 2-byte input 0xffee.
        string tail = new('1', 64);
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0x100, tail)
            .StoreDataInMemory(0, "ffee")
            .PushData(0x20)
            .PushData(0x100)
            .PushData(2)
            .PushData(0)
            .PushData(0)
            .PushData(IdentityPrecompile.Address)
            .PushData(50000)
            .Op(Instruction.CALL)
            .Op(Instruction.STOP)
            .Done;

        JToken ops = (await TraceCallVmTrace(context, code.ToHexString(true), "vmTrace"))["ops"]!;
        JToken call = ops.Single(op => op["pc"]!.Value<int>() == code.Length - 2);
        Assert.That(call["ex"]!["mem"], Is.EqualTo(JToken.Parse($$"""{"data":"0xffee{{tail[4..]}}","off":256}""")).Using(JToken.EqualityComparer));
    }

    private static async Task<JToken> TraceCallVmTrace(Context context, string bytecode, params string[] traceTypes)
    {
        string calls = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":null,\"data\":\"{bytecode}\",\"gas\":\"0xf4240\"}},{JsonSerializer.Serialize(traceTypes)}]]";
        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls);
        return JToken.Parse(response)["result"]![0]!["vmTrace"]!;
    }

    [Test]
    public async Task VmTrace_top_level_precompile_failure_returns_empty_operations(
        [Values] bool streaming, [Values] bool includeTrace)
    {
        Context context = new();
        await context.Build();
        using TestRpcBlockchain blockchain = context.Blockchain;
        blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = streaming;

        string[] traceTypes = includeTrace ? ["vmTrace", "trace"] : ["vmTrace"];
        // Blake2F requires exactly 213 input bytes.
        object call = new { from = TestItem.AddressA, to = Blake2FPrecompile.Address, data = "0x", gas = "0x186a0" };
        string response = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_call", call, traceTypes, "latest");
        JToken parsed = JToken.Parse(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed["error"], Is.Null, response);
            Assert.That(parsed["result"]?["output"]?.Value<string>(), Is.EqualTo("0x"), response);
            Assert.That(parsed["result"]?["vmTrace"]?["ops"], Is.Empty, response);
            if (includeTrace)
                Assert.That(parsed["result"]?["trace"]?[0]?["error"]?.Value<string>(), Is.EqualTo("Out of gas"), response);
        }
    }

    [Test]
    public async Task Streaming_vmTrace_matches_buffered_across_nested_call_frame()
    {
        Context context = new();
        await context.Build();
        IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();

        // PUSH1 0 ×5, PUSH1 0x04, PUSH2 0x5000, CALL, STOP — CALL to identity precompile.
        string bytecode = "0x600060006000600060006004615000f100";
        string calls = $"[[{{\"from\":\"{TestItem.AddressA}\",\"to\":null,\"data\":\"{bytecode}\",\"gas\":\"0xf4240\"}},[\"vmTrace\",\"trace\"]]]";

        config.EnableTracingStreamMode = false;
        string buffered = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls);

        config.EnableTracingStreamMode = true;
        string streamed = await RpcTest.TestSerializedRequest(context.TraceRpcModule, "trace_callMany", calls);

        JToken bufferedTrace = JToken.Parse(buffered);
        Assert.That(bufferedTrace["result"]![0]!["vmTrace"]!["ops"]![7]!["ex"]!["push"]![0]!.Value<string>(), Is.EqualTo("0x1"));
        Assert.That(JToken.Parse(streamed), Is.EqualTo(bufferedTrace).Using(JToken.EqualityComparer));
    }

    private static IEnumerable<TestCaseData> StreamingResourceSafetyCases()
    {
        yield return new TestCaseData((Func<Task>)(async () =>
        {
            Context context = new();
            await context.Build();
            IJsonRpcConfig config = context.Blockchain.Container.Resolve<IJsonRpcConfig>();
            config.EnableTracingStreamMode = true;

            ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = context.TraceRpcModule.trace_block(BlockParameter.Latest);
            Assert.That(result.Data, Is.AssignableTo<IStreamableResult>());
            IStreamableResult streaming = (IStreamableResult)result.Data;
            using CancellationTokenSource cts = new();
            cts.Cancel();
            System.IO.Pipelines.Pipe pipe = new();
            try
            {
                Assert.ThrowsAsync<OperationCanceledException>(async () => await streaming.WriteToAsync(pipe.Writer, cts.Token));
            }
            finally
            {
                await pipe.Writer.CompleteAsync();
                await pipe.Reader.CompleteAsync();
                (streaming as IDisposable)?.Dispose();
            }
        })).SetName("Pre-cancelled token: WriteToAsync propagates OperationCanceledException");

        yield return new TestCaseData((Func<Task>)(() =>
        {
            Block block = Build.A.Block.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ArrayBufferWriter<byte> sink = new();
            using Utf8JsonWriter writer = new(sink, new JsonWriterOptions { SkipValidation = true });
            StreamingParityLikeTxTracer tracer = new(
                block, tx, ParityTraceTypes.Trace,
                writer, pipeWriter: null, CancellationToken.None,
                fillVmTraceSlot: false);
            tracer.ReleaseResources();
            tracer.ReleaseResources();
            return Task.CompletedTask;
        })).SetName("ReleaseResources: idempotent across repeated calls");
    }

    [TestCaseSource(nameof(StreamingResourceSafetyCases))]
    public void Streaming_resource_safety(Func<Task> scenario) =>
        Assert.DoesNotThrowAsync(() => scenario());

    private static TraceRpcModule BuildModuleWithNonCanonicalReceipt(Hash256 txHash, Hash256 nonCanonicalBlockHash, bool traceNonCanonical = false)
    {
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.FindBlockHash(txHash).Returns(nonCanonicalBlockHash);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.HeadHash.Returns(TestItem.KeccakC);
        blockFinder.FindBlock(nonCanonicalBlockHash, BlockTreeLookupOptions.RequireCanonical, Arg.Any<ulong?>())
            .Returns((Block?)null);
        if (!traceNonCanonical)
            blockFinder.FindHeader(nonCanonicalBlockHash).Returns(Build.A.BlockHeader.TestObject);

        return new TraceRpcModule(
            receiptFinder,
            Substitute.For<IOverridableEnv<ITracer>>(),
            blockFinder,
            new JsonRpcConfig(),
            Substitute.For<IBlockchainBridge>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlocksConfig>(),
            NullPrefixStateSeedSource.Instance,
            LimboLogs.Instance);
    }

    // AllowTestChainOverride=false prevents TestBlockchain from wrapping this instance and stripping IForkAwareSpecProvider.
    private sealed class ForkAwareTestSpecProvider : TestSpecProvider, IForkAwareSpecProvider
    {
        private readonly IForkAwareSpecProvider _forkAware;

        public ForkAwareTestSpecProvider(IReleaseSpec blockchainSpec, IForkAwareSpecProvider forkAware) : base(blockchainSpec)
        {
            _forkAware = forkAware;
            AllowTestChainOverride = false;
        }

        public IEnumerable<string> AvailableForks => _forkAware.AvailableForks;
        public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec) => _forkAware.TryGetForkSpec(forkName, out spec);
    }

    [Test]
    public async Task trace_block_with_valid_fork_name_returns_success([Values(nameof(Berlin), nameof(Istanbul), nameof(Cancun))] string forkName)
    {
        Context context = new();
        await context.Build(new ForkAwareTestSpecProvider(Berlin.Instance, MainnetSpecProvider.Instance));

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result =
            context.TraceRpcModule.trace_block(BlockParameter.Latest, forkName);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
    }

    [Test]
    public async Task trace_block_fork_parameter_json_applies_berlin_gas_rules()
    {
        (Context context, BlockParameter block) = await BuildModExpBlockAsync();

        string blockHex = "0x" + context.Blockchain.BlockTree.Head!.Number.ToString("x");
        string serialized = await RpcTest.TestSerializedRequest(
            context.TraceRpcModule, "trace_block", blockHex, "berlin");

        Assert.That(serialized, Does.Contain("\"gasUsed\":\"0x550\""),
            "Berlin fork (EIP-2565) ModExp gas for 32-byte inputs must be 1360 (0x550)");
    }

    [Test]
    public async Task trace_block_unknown_fork_returns_invalid_params_failure_listing_known_forks(
        [Values(BlockParameterType.Latest, BlockParameterType.Earliest)] BlockParameterType blockType)
    {
        Context context = new();
        await context.Build(new ForkAwareTestSpecProvider(Berlin.Instance, MainnetSpecProvider.Instance));

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result =
            context.TraceRpcModule.trace_block(new BlockParameter(blockType), "NonExistentFork");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Result.Error, Does.Contain(nameof(Berlin)),
                "the error message must list known fork names so callers can correct the request");
        }
    }

    [Test]
    public async Task trace_block_non_fork_aware_provider_returns_invalid_params_failure_mentioning_not_supported()
    {
        Context context = new();
        await context.Build(new TestSpecProvider(Berlin.Instance));

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result =
            context.TraceRpcModule.trace_block(BlockParameter.Latest, nameof(Berlin));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Result.Error, Does.Contain("does not support fork overrides"));
        }
    }

    private static byte[] BuildModExpInput()
    {
        byte[] input = new byte[96 + 32 + 32 + 32];
        input[31] = 32;          // base_length = 32
        input[63] = 32;          // exp_length  = 32
        input[95] = 32;          // mod_length  = 32
        input[96 + 31] = 2;      // base = 2
        input[96 + 32] = 0x80;   // exp = 2^255 (MSB set)
        input[96 + 64 + 31] = 3; // mod = 3
        return input;
    }

    private static async Task<(Context context, BlockParameter block)> BuildModExpBlockAsync()
    {
        Context context = new();
        await context.Build(new ForkAwareTestSpecProvider(Byzantium.Instance, MainnetSpecProvider.Instance));

        ulong nonce = context.Blockchain.StateReader.GetNonce(
            context.Blockchain.BlockTree.Head!.Header, TestItem.AddressB);

        Transaction tx = Build.A.Transaction
            .WithTo(Address.FromNumber(5))
            .WithData(BuildModExpInput())
            .WithGasLimit(50_000)
            .WithNonce(nonce)
            .SignedAndResolved(context.Blockchain.EthereumEcdsa, TestItem.PrivateKeyB)
            .TestObject;

        await context.Blockchain.AddBlock(tx);
        return (context, new BlockParameter(context.Blockchain.BlockTree.Head!.Number));
    }

    private static ulong ModExpGasUsed(
        Context context, BlockParameter block, string forkName) =>
        context.TraceRpcModule
            .trace_block(block, forkName)
            .Data.First(t => t.Type == "call").Result!.GasUsed;

    [TestCase(nameof(Istanbul), 13056UL, "pre-EIP-2565 formula: 32^2 * 255 / 20 = 13056")]
    [TestCase(nameof(Berlin), 1360UL, "EIP-2565 formula: ceil(32/8)^2 * 255 / 3 = 16 * 255 / 3 = 1360")]
    public async Task trace_block_modexp_gas_cost_respects_fork_override(string forkName, ulong expectedGas, string reason)
    {
        (Context context, BlockParameter block) = await BuildModExpBlockAsync();

        ulong gasUsed = ModExpGasUsed(context, block, forkName);

        Assert.That(gasUsed, Is.EqualTo(expectedGas), reason);
    }

    [Test]
    public async Task trace_block_fork_override_does_not_persist_between_calls()
    {
        (Context context, BlockParameter block) = await BuildModExpBlockAsync();

        ulong firstIstanbulGas = ModExpGasUsed(context, block, nameof(Istanbul));
        ModExpGasUsed(context, block, nameof(Berlin));
        ulong secondIstanbulGas = ModExpGasUsed(context, block, nameof(Istanbul));

        Assert.That(secondIstanbulGas, Is.EqualTo(firstIstanbulGas),
            "the Berlin override from the intervening call must not leak into subsequent calls");
    }

    [Test]
    public async Task trace_block_pre_1559_fork_override_on_london_block_zeroes_base_fee()
    {
        OverridableReleaseSpec londonSpec = new(London.Instance) { Eip1559TransitionBlock = 1 };
        ForkAwareTestSpecProvider specProvider = new(londonSpec, MainnetSpecProvider.Instance);

        Context context = new();
        await context.Build(specProvider);

        BlockHeader? block1 = context.Blockchain.BlockTree.FindHeader(1, BlockTreeLookupOptions.None);
        Assert.That(block1, Is.Not.Null);
        Assert.That(block1!.BaseFeePerGas, Is.GreaterThan(UInt256.Zero),
            "the London fork-activation block must carry a non-zero base fee so the test is meaningful");

        // Re-executing this London block with a Berlin (pre-EIP-1559) fork override must succeed:
        // AdjustHeaderForSpec must zero BaseFeePerGas for pre-1559 specs.
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result =
            context.TraceRpcModule.trace_block(new BlockParameter(1L), nameof(Berlin));

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success),
            "tracing a London block with a pre-EIP-1559 fork override must succeed (AdjustHeaderForSpec zeroes BaseFeePerGas)");
    }

    // regression test ensuring asynchronous streaming pipe doesn't crash tracing
    // was caused by synchronously waiting non-completed IValueTaskSource-backed ValueTask
    [Test]
    public async Task trace_block_to_async_stream()
    {
        Context context = new();
        await context.Build();
        context.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = true;

        Block block = context.Blockchain.BlockTree.Head!;
        Assert.That(block.Transactions, Is.Not.Empty, "block must contain transactions so AddTrace is exercised");

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> result = context.TraceRpcModule.trace_block(new BlockParameter(block.Number));
        Assert.That(result.Data, Is.AssignableTo<IStreamableResult>());
        IStreamableResult streaming = (IStreamableResult)result.Data;

        await using AsyncCompletingStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream);

        Assert.DoesNotThrowAsync(async () => await streaming.WriteToAsync(writer, CancellationToken.None));

        await writer.CompleteAsync();
    }

    private static void AssertBalanceChange(ParityAccountStateChange? stateChanges, UInt256 before, UInt256 after)
    {
        Assert.That(stateChanges?.Balance, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateChanges!.Balance!.Before, Is.EqualTo(before));
            Assert.That(stateChanges.Balance.After, Is.EqualTo(after));
        }
    }
}
