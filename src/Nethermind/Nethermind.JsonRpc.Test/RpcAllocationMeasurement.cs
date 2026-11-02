// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.History;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>Reports bytes allocated per JSON-RPC request, end to end through <see cref="JsonRpcProcessor"/>, against the response size.</summary>
/// <remarks>
/// Run with <c>DOTNET_TieredCompilation=0</c>: tier-0 code boxes in places optimized code does not (e.g. <c>Enum.HasFlag</c>).
/// Environment: <c>RPC_ALLOC_FILTER</c> (label substring), <c>RPC_ALLOC_ITER</c> (iterations per method),
/// <c>RPC_ALLOC_BREAKDOWN=1</c> (sampled per-type breakdown), <c>RPC_ALLOC_REPORT</c> (report file path).
/// B/req counts every thread's allocations, and with the breakdown on also the listener's own, so compare runs made with the same settings.
/// </remarks>
[Explicit("measurement harness")]
public class RpcAllocationMeasurement
{
    private const int TxPerBlock = 25;
    private const int Blocks = 5;
    private const int LabelWidth = 58;
    private const int BreakdownTypes = 12;

    [Test]
    public async Task Measure()
    {
        string? filter = Environment.GetEnvironmentVariable("RPC_ALLOC_FILTER");
        int? forcedIterations = int.TryParse(Environment.GetEnvironmentVariable("RPC_ALLOC_ITER"), out int iter) && iter > 0 ? iter : null;
        bool breakdown = Environment.GetEnvironmentVariable("RPC_ALLOC_BREAKDOWN") == "1";
        string reportPath = Environment.GetEnvironmentVariable("RPC_ALLOC_REPORT") ?? Path.Combine(Path.GetTempPath(), "rpc-alloc-report.txt");

        // The default TestBlockchain spec provider returns a new spec per GetSpec, rebuilding gas and opcode tables per tx.
        using Chain chain = await TestRpcBlockchain.ForTest(new Chain())
            .WithEthRpcModule(CreateEthModule)
            .Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance) { AllowTestChainOverride = false }));

        (Address contract, Block[] blocks) = await Populate(chain);

        // Factories, not instances: with one instance the pool's shared and exclusive modules coincide and the exclusive slot is never released.
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new JsonRpcConfig { EnabledModules = [ModuleType.Eth, ModuleType.Debug, ModuleType.Trace] }))
            .AddSingleton<ILogManager>(NullLogManager.Instance)
            .RegisterBoundedJsonRpcModule<IEthRpcModule, AutoRpcModuleFactory<IEthRpcModule>>(1, 20000)
            .AddScoped<IEthRpcModule>(_ => CreateEthModule(chain))
            .RegisterBoundedJsonRpcModule<IDebugRpcModule, AutoRpcModuleFactory<IDebugRpcModule>>(1, 20000)
            .AddScoped<IDebugRpcModule>(_ => chain.DebugRpcModule)
            .RegisterBoundedJsonRpcModule<ITraceRpcModule, AutoRpcModuleFactory<ITraceRpcModule>>(1, 20000)
            .AddScoped<ITraceRpcModule>(_ => chain.TraceRpcModule)
            .Build();

        JsonRpcProcessor processor = new(container.Resolve<IJsonRpcService>(), new JsonRpcConfig(), Substitute.For<IFileSystem>(), NullLogManager.Instance);
        CountingStream stream = new();
        ResponseSink sink = new(PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)), stream);
        using AllocationTypeListener? listener = breakdown ? new AllocationTypeListener() : null;

        File.WriteAllText(reportPath, $"{"method",-LabelWidth} {"B/req",12} {"resp B",10} {"alloc/resp",10}{Environment.NewLine}");
        List<string> errors = [];
        int id = 0;
        int casesRun = 0;
        foreach ((string method, string parameters, int defaultIterations) in Cases(contract, blocks))
        {
            string label = $"{method} {parameters}";
            if (filter is not null && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            label = label.Length > LabelWidth ? label[..LabelWidth] : label;
            casesRun++;

            byte[] request = Encoding.UTF8.GetBytes($$"""{"jsonrpc":"2.0","id":{{++id}},"method":"{{method}}","params":{{parameters}}}""");
            async Task Run(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    JsonRpcContext context = new(RpcEndpoint.Http);
                    await processor.ProcessAsync(request, context, sink, new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument));
                }
            }

            sink.ErrorCount = 0;
            stream.Captured = new MemoryStream();
            await Run(1);
            string response = Encoding.UTF8.GetString(stream.Captured.ToArray());
            long firstResponseSize = stream.Captured.Length;
            stream.Captured = null;

            int iterations = forcedIterations ?? defaultIterations;
            await Run(Math.Max(2, iterations / 10));
            GC.Collect();
            GC.WaitForPendingFinalizers();

            long responseBytesBefore = stream.Count;
            listener?.Start();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            await Run(iterations);
            long perRequest = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / iterations;
            listener?.Stop();
            long responseBytes = stream.Count - responseBytesBefore;
            long responseSize = responseBytes / iterations;
            if (sink.ErrorCount > 0) errors.Add($"{label}: {sink.ErrorCount} error responses, first: {response[..Math.Min(300, response.Length)]}");
            else if (responseBytes != firstResponseSize * iterations) errors.Add($"{label}: {responseBytes} response bytes over {iterations} requests, the first response was {firstResponseSize}");

            StringBuilder line = new($"{label,-LabelWidth} {perRequest,12:N0} {responseSize,10:N0} {(double)perRequest / Math.Max(1, responseSize),10:F2}{Environment.NewLine}");
            if (listener is not null)
            {
                foreach (KeyValuePair<string, long> type in listener.BytesByType.OrderByDescending(static t => t.Value).Take(BreakdownTypes))
                {
                    line.AppendLine($"      {type.Value / iterations,10:N0}  {type.Key}");
                }
            }

            File.AppendAllText(reportPath, line.ToString());
        }

        await TestContext.Out.WriteLineAsync(File.ReadAllText(reportPath));
        Assert.That(casesRun, Is.Positive, $"no case matches RPC_ALLOC_FILTER '{filter}'");
        Assert.That(errors, Is.Empty, "error responses are not representative measurements");
    }

    private static IEnumerable<(string Method, string Params, int Iterations)> Cases(Address contract, Block[] blocks)
    {
        Block block = blocks[^1];
        string number = $"\"0x{block.Number:x}\"";
        string firstNumber = $"\"0x{blocks[0].Number:x}\"";
        string blockHash = $"\"{block.Hash}\"";
        string txHash = $"\"{block.Transactions[TxPerBlock / 2].Hash}\"";
        string call = $$"""{"from":"{{TestItem.AddressB}}","to":"{{contract}}","data":"0x{{new UInt256(7).ToBigEndian().ToHexString()}}","gas":"0x100000"}""";

        return
        [
            ("eth_chainId", "[]", 1000),
            ("eth_blockNumber", "[]", 1000),
            ("eth_getBalance", $"[\"{TestItem.AddressB}\",\"latest\"]", 1000),
            ("eth_getTransactionCount", $"[\"{TestItem.AddressB}\",\"latest\"]", 1000),
            ("eth_getStorageAt", $"[\"{contract}\",\"0x7\",\"latest\"]", 1000),
            ("eth_getCode", $"[\"{contract}\",\"latest\"]", 1000),
            ("eth_gasPrice", "[]", 400),
            ("eth_maxPriorityFeePerGas", "[]", 400),
            ("eth_getBlockByNumber", $"[{number},false]", 200),
            ("eth_getBlockByNumber", $"[{number},true]", 60),
            ("eth_getBlockByHash", $"[{blockHash},true]", 60),
            ("eth_getBlockReceipts", $"[{number}]", 60),
            ("eth_getTransactionByHash", $"[{txHash}]", 400),
            ("eth_getTransactionReceipt", $"[{txHash}]", 400),
            ("eth_getTransactionByBlockNumberAndIndex", $"[{number},\"0x5\"]", 400),
            ("eth_getLogs", $$"""[{"fromBlock":{{firstNumber}},"toBlock":{{number}},"address":"{{contract}}"}]""", 20),
            ("eth_getLogs", $$"""[{"fromBlock":{{number}},"toBlock":{{number}}}]""", 60),
            ("eth_call", $"[{call},\"latest\"]", 200),
            ("eth_estimateGas", $"[{call},\"latest\"]", 60),
            ("eth_createAccessList", $"[{call},\"latest\"]", 60),
            ("eth_feeHistory", "[\"0x5\",\"latest\",[25,75]]", 60),
            ("eth_getProof", $"[\"{contract}\",[\"0x7\"],\"latest\"]", 200),
            ("debug_traceTransaction", $"[{txHash}]", 20),
            ("debug_traceTransaction", $$"""[{{txHash}},{"tracer":"callTracer"}]""", 60),
            ("debug_traceTransaction", $$"""[{{txHash}},{"tracer":"prestateTracer"}]""", 60),
            ("debug_traceBlockByNumber", $$"""[{{number}},{"tracer":"callTracer"}]""", 6),
            ("debug_traceBlockByNumber", $"[{number}]", 5),
            ("debug_traceCall", $$"""[{{call}},"latest",{"tracer":"callTracer"}]""", 60),
            ("debug_getRawBlock", $"[{number}]", 200),
            ("debug_getRawReceipts", $"[{number}]", 200),
            ("trace_transaction", $"[{txHash}]", 60),
            ("trace_block", $"[{number}]", 6),
            ("trace_replayTransaction", $"[{txHash},[\"trace\",\"stateDiff\"]]", 60),
            ("trace_replayBlockTransactions", $"[{number},[\"trace\"]]", 6),
            ("trace_replayBlockTransactions", $"[{number},[\"trace\",\"stateDiff\"]]", 6),
            ("trace_filter", $$"""[{"fromBlock":{{firstNumber}},"toBlock":{{number}},"toAddress":["{{contract}}"]}]""", 5),
            ("trace_call", $"[{call},[\"trace\"],\"latest\"]", 60),
        ];
    }

    private static IEthRpcModule CreateEthModule(TestRpcBlockchain chain) => new EthRpcModule(
        chain.RpcConfig, chain.Bridge, chain.BlockFinder, chain.BlockTree, chain.ReceiptFinder, chain.StateReader, chain.TxPool, chain.TxSender, chain.TestWallet,
        NullLogManager.Instance, chain.SpecProvider, chain.GasPriceOracle,
        new EthSyncingInfo(chain.BlockTree, Substitute.For<ISyncPointers>(), chain.Container.Resolve<ISyncConfig>(),
            new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), Synchronization.No.BeaconSync, chain.LogManager),
        chain.FeeHistoryOracle!, chain.ProtocolsManager, chain.ForkInfo, new BlocksConfig().SecondsPerSlot, chain.HeadBlockSignal,
        new EthCapabilitiesProvider(chain.BlockTree.AsReadOnly(), chain.Container.Resolve<IStateBoundary>(), chain.Container.Resolve<ISyncConfig>(),
            Substitute.For<ISyncPointers>(), Substitute.For<IHistoryConfig>(), Substitute.For<IHistoryPruner>()),
        chain.Container.ResolveOptional<IBlockForRpcFactory>() ?? new BlockForRpcFactory(),
        chain.Container.ResolveOptional<HashesOnlyBlockReader>());

    /// <summary>Deploys a contract that writes storage, logs and calls a second contract, then fills <see cref="Blocks"/> blocks with calls to it.</summary>
    private static async Task<(Address Contract, Block[] Blocks)> Populate(TestRpcBlockchain chain)
    {
        ulong nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);

        byte[] innerRuntime = Prepare.EvmCode
            .PushData(0).Op(Instruction.SLOAD).PushData(1).Op(Instruction.ADD).PushData(0).Op(Instruction.SSTORE)
            .StoreDataInMemory(0, Enumerable.Repeat((byte)0xab, 32).ToArray()).Return(32, 0).Done;
        Address inner = ContractAddress.From(TestItem.AddressB, nonce);
        Address outer = ContractAddress.From(TestItem.AddressB, nonce + 1);
        byte[] outerRuntime = Prepare.EvmCode
            .PushData(0).Op(Instruction.CALLDATALOAD).PushData(1).Op(Instruction.ADD)
            .PushData(0).Op(Instruction.CALLDATALOAD).Op(Instruction.SSTORE)
            .StoreDataInMemory(0, Enumerable.Repeat((byte)0xcd, 64).ToArray())
            .Log(64, 0, [TestItem.KeccakA, TestItem.KeccakB])
            .Call(inner, 50_000).Op(Instruction.POP)
            .Return(32, 0).Done;

        await chain.AddBlock(
            Build.A.Transaction.WithNonce(nonce).WithCode(Prepare.EvmCode.ForInitOf(innerRuntime).Done).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Build.A.Transaction.WithNonce(nonce + 1).WithCode(Prepare.EvmCode.ForInitOf(outerRuntime).Done).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        nonce += 2;

        Block[] blocks = new Block[Blocks];
        for (int b = 0; b < Blocks; b++)
        {
            Transaction[] txs = new Transaction[TxPerBlock];
            for (int i = 0; i < TxPerBlock; i++)
            {
                txs[i] = Build.A.Transaction.WithNonce(nonce++).WithTo(outer)
                    .WithData(new UInt256((ulong)(b * TxPerBlock + i)).ToBigEndian())
                    .WithGasLimit(150_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            }

            await chain.AddBlockMayMissTx(txs);
            blocks[b] = chain.BlockTree.Head!;
            Assert.That(blocks[b].Transactions, Has.Length.EqualTo(TxPerBlock));
        }

        return (outer, blocks);
    }

    private sealed class Chain : TestRpcBlockchain
    {
        // LimboLogs reports IsTrace, so trace-guarded logging would allocate in the measured paths.
        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider).AddSingleton<ILogManager>(NullLogManager.Instance);
    }

    private sealed class ResponseSink(PipeWriter writer, CountingStream stream) : IJsonRpcResponseSink
    {
        public long BytesWritten => stream.Count;
        public bool StopRequested => false;
        public int ErrorCount { get; set; }

        public async ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            if (response is JsonRpcErrorResponse) ErrorCount++;
            await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, false, cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        public ValueTask BeginBatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask EndBatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Discards writes, counting bytes and optionally capturing them.</summary>
    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }
        public MemoryStream? Captured { get; set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Count += buffer.Length;
            Captured?.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Groups the runtime's sampled <c>GCAllocationTick</c> events (one per ~100 KB allocated) by type.</summary>
    private sealed class AllocationTypeListener : EventListener
    {
        private const EventKeywords GcKeyword = (EventKeywords)0x1;
        private volatile bool _enabled;

        public ConcurrentDictionary<string, long> BytesByType { get; } = new();

        public void Start()
        {
            BytesByType.Clear();
            _enabled = true;
        }

        public void Stop() => _enabled = false;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, GcKeyword);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!_enabled || e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true) return;
            string type = e.Payload![e.PayloadNames!.IndexOf("TypeName")]?.ToString() ?? "?";
            long amount = Convert.ToInt64(e.Payload[e.PayloadNames.IndexOf("AllocationAmount64")]);
            BytesByType.AddOrUpdate(type, amount, (_, total) => total + amount);
        }
    }
}
