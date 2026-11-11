// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class TraceBatchConcurrencyTests
{
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"prestateTracer"}]""", ExpectedResult = true, TestName = "Native tracer")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"callTracer","tracerConfig":{"onlyTopCall":true}}]""", ExpectedResult = true, TestName = "Native tracer with config")]
    [TestCase("debug_traceCall", """[{},"latest"]""", ExpectedResult = false, TestName = "No options: struct logs may stream")]
    [TestCase("debug_traceCall", """[{},"latest",{}]""", ExpectedResult = false, TestName = "No tracer: struct logs may stream")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"{result:function(){return 1},fault:function(){}}"}]""", ExpectedResult = false, TestName = "JavaScript tracer")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"callTracer","Tracer":null}]""", ExpectedResult = false, TestName = "Later case alias without a tracer")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"callTracer","Tracer":"{result:function(){return 1},fault:function(){}}"}]""", ExpectedResult = false, TestName = "Later case alias with a JavaScript tracer")]
    [TestCase("debug_traceCall", """[{},"latest",{"Tracer":"callTracer"}]""", ExpectedResult = false, TestName = "Case alias only")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"callTracer","tracer":"callTracer"}]""", ExpectedResult = false, TestName = "Repeated tracer")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"\uD800"}]""", ExpectedResult = false, TestName = "Unreadable tracer name")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"callTracer","\uD800":null}]""", ExpectedResult = false, TestName = "Unreadable property name")]
    [TestCase("debug_traceCall", """[{},"latest",{"tr\u0061cer":"callTracer"}]""", ExpectedResult = false, TestName = "Escaped tracer name")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"callTracer","TR\u0041CER":null}]""", ExpectedResult = false, TestName = "Escaped case alias")]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":1}]""", ExpectedResult = false, TestName = "Tracer that is not a string")]
    [TestCase("debug_traceCall", """{"tracer":"callTracer"}""", ExpectedResult = false, TestName = "Params that are not an array")]
    [TestCase("debug_traceCall", """[{},{"tracer":"callTracer"}]""", ExpectedResult = false, TestName = "Call options where a transaction trace keeps them")]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"callTracer"}]""", ExpectedResult = true, TestName = "Transaction: native tracer")]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"prestateTracer","tracerConfig":{"diffMode":true}}]""", ExpectedResult = true, TestName = "Transaction: native tracer with config")]
    [TestCase("debug_traceTransaction", """["0x01"]""", ExpectedResult = false, TestName = "Transaction: no options: struct logs may stream")]
    [TestCase("debug_traceTransaction", """["0x01",null]""", ExpectedResult = false, TestName = "Transaction: null options: struct logs may stream")]
    [TestCase("debug_traceTransaction", """["0x01",{}]""", ExpectedResult = false, TestName = "Transaction: no tracer: struct logs may stream")]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"{result:function(){return 1},fault:function(){}}"}]""", ExpectedResult = false, TestName = "Transaction: JavaScript tracer")]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"muxTracer","tracerConfig":{"callTracer":{}}}]""", ExpectedResult = false, TestName = "Transaction: muxTracer, which may run a JavaScript child")]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"callTracer","Tracer":"{result:function(){return 1},fault:function(){}}"}]""", ExpectedResult = false, TestName = "Transaction: later case alias with a JavaScript tracer")]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"\uD800"}]""", ExpectedResult = false, TestName = "Transaction: unreadable tracer name")]
    [TestCase("debug_traceTransaction", """["0x01","latest",{"tracer":"callTracer"}]""", ExpectedResult = false, TestName = "Transaction: options where a traced call keeps them")]
    public bool Debug_trace_runs_concurrently_only_with_one_native_tracer(string method, string parameters) =>
        CanExecuteConcurrently(new DebugTraceBatchConcurrency(new JsonRpcConfig { DebugModuleConcurrentInstances = 4 }), method, parameters, windowSize: 4);

    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"prestateTracer"}]""", 1, 4, ExpectedResult = false)]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"prestateTracer"}]""", 4, 4, ExpectedResult = true)]
    [TestCase("debug_traceCall", """[{},"latest",{"tracer":"prestateTracer"}]""", 16, 4, ExpectedResult = true)]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"callTracer"}]""", 3, 4, ExpectedResult = false)]
    [TestCase("debug_traceTransaction", """["0x01",{"tracer":"callTracer"}]""", 4, 4, ExpectedResult = true)]
    public bool Debug_trace_runs_concurrently_only_while_the_window_fits_the_debug_module_pool(string method, string parameters, int debugModuleInstances, int windowSize) =>
        CanExecuteConcurrently(new DebugTraceBatchConcurrency(new JsonRpcConfig { DebugModuleConcurrentInstances = debugModuleInstances }), method, parameters, windowSize);

    [TestCase(2, false, 2, ExpectedResult = true, TestName = "Pool of two fits a window of two")]
    [TestCase(2, false, 3, ExpectedResult = false, TestName = "Pool of two does not fit a window of three")]
    [TestCase(4, false, 4, ExpectedResult = true, TestName = "Configured pool fits the window")]
    [TestCase(4, true, 4, ExpectedResult = false, TestName = "Streamed trace executes while written")]
    public bool Trace_transaction_runs_concurrently_only_buffered_and_while_the_window_fits_the_trace_module_pool(int? traceModuleInstances, bool streamMode, int windowSize) =>
        CanExecuteConcurrently(
            new TraceTransactionBatchConcurrency(new JsonRpcConfig { TraceModuleConcurrentInstances = traceModuleInstances, EnableTracingStreamMode = streamMode }),
            "trace_transaction", """["0x01"]""", windowSize);

    [Test]
    public void Trace_transaction_default_window_is_the_processor_count([Values] bool fits) =>
        Assert.That(
            CanExecuteConcurrently(
                new TraceTransactionBatchConcurrency(new JsonRpcConfig { EnableTracingStreamMode = false }),
                "trace_transaction", """["0x01"]""", Environment.ProcessorCount + (fits ? 0 : 1)),
            Is.EqualTo(fits));

    [Test]
    public async Task Concurrent_batch_of_transaction_traces_matches_sequential_byte_for_byte()
    {
        JsonRpcConfig config = CreateConfig(batchProcessingConcurrency: 2);
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithConfig(config).Build();
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressB);
        Transaction[] transactions = new Transaction[4];
        for (int i = 0; i < transactions.Length; i++)
        {
            TransactionBuilder<Transaction> builder = i % 2 == 0
                ? Build.A.Transaction.WithTo(TestItem.AddressC).WithValue(1 + i)
                : Build.A.Transaction.WithCode(Prepare.EvmCode.PushData(i).PushData(0).Op(Instruction.SSTORE).Done);
            transactions[i] = builder.WithNonce(nonce++).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }

        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(transactions.Length));

        StringBuilder batch = new("[");
        int id = 0;
        foreach (Transaction transaction in transactions)
        {
            string hash = transaction.Hash!.ToString();
            batch.Append($$"""{"jsonrpc":"2.0","id":{{++id}},"method":"debug_traceTransaction","params":["{{hash}}",{"tracer":"callTracer"}]},""");
            batch.Append($$"""{"jsonrpc":"2.0","id":{{++id}},"method":"debug_traceTransaction","params":["{{hash}}",{"tracer":"prestateTracer"}]},""");
        }

        foreach (Transaction transaction in transactions)
        {
            batch.Append($$"""{"jsonrpc":"2.0","id":{{++id}},"method":"trace_transaction","params":["{{transaction.Hash}}"]},""");
        }

        batch[^1] = ']';
        byte[] request = Encoding.UTF8.GetBytes(batch.ToString());
        IJsonRpcService service = chain.Container.Resolve<IJsonRpcService>();
        // The test chain configures its container twice, which registers every strategy twice.
        IBatchConcurrencyStrategy[] strategies = chain.Container.Resolve<IBatchConcurrencyStrategy[]>().DistinctBy(static s => s.GetType()).ToArray();

        OverlapProbe probe = new(service);
        string concurrent = await ProcessAsync(probe, config, strategies, request);
        string sequential = await ProcessAsync(service, CreateConfig(batchProcessingConcurrency: 1), strategies, request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(strategies.SelectMany(static s => s.Methods), Is.SupersetOf(new[] { "debug_traceTransaction", "trace_transaction" }));
            Assert.That(probe.Overlapped, Is.True, "the batch items overlap");
            Assert.That(probe.TracesOverlapped, Is.True, "two trace modules run at once");
            Assert.That(concurrent, Does.Not.Contain("\"error\""));
            Assert.That(concurrent, Does.Contain("\"to\":\"" + TestItem.AddressC.ToString().ToLowerInvariant() + "\""));
            Assert.That(concurrent, Does.Contain("\"type\":\"CREATE\""), "contract creations run EVM code");
            Assert.That(concurrent, Is.EqualTo(sequential));
        }
    }

    private static JsonRpcConfig CreateConfig(int batchProcessingConcurrency) => new()
    {
        Timeout = -1,
        EnabledModules = [ModuleType.Debug, ModuleType.Trace],
        BatchProcessingConcurrency = batchProcessingConcurrency,
        EthModuleConcurrentInstances = 4,
        DebugModuleConcurrentInstances = 4,
        TraceModuleConcurrentInstances = 4,
        EnableTracingStreamMode = false
    };

    private static async Task<string> ProcessAsync(IJsonRpcService service, IJsonRpcConfig config, IBatchConcurrencyStrategy[] strategies, byte[] request)
    {
        JsonRpcProcessor processor = new(service, config, Substitute.For<IFileSystem>(), LimboLogs.Instance, null, strategies);
        BufferingSink sink = new();
        await processor.ProcessAsync(PipeReader.Create(new ReadOnlySequence<byte>(request)), new JsonRpcContext(RpcEndpoint.Http), sink,
            new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument));
        return Encoding.UTF8.GetString(sink.Output.WrittenSpan);
    }

    private static bool CanExecuteConcurrently(IBatchConcurrencyStrategy strategy, string method, string parameters, int windowSize)
    {
        using JsonDocument document = JsonDocument.Parse(parameters);
        return strategy.CanExecuteConcurrently(new JsonRpcRequest { Method = method, Params = document.RootElement }, windowSize);
    }

    /// <summary>Holds each request until a second one is in flight, and each trace_transaction until a second trace_transaction is, once, so the batch must overlap to finish promptly.</summary>
    private sealed class OverlapProbe(IJsonRpcService inner) : IJsonRpcService
    {
        private readonly TaskCompletionSource _overlapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _tracesOverlapped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _tracesInFlight;

        public bool Overlapped => _overlapped.Task.IsCompleted;

        public bool TracesOverlapped => _tracesOverlapped.Task.IsCompleted;

        public async ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context)
        {
            bool trace = request.Method == "trace_transaction";
            if (Interlocked.Increment(ref _inFlight) > 1) _overlapped.TrySetResult();
            if (trace && Interlocked.Increment(ref _tracesInFlight) > 1) _tracesOverlapped.TrySetResult();
            try
            {
                await WaitBriefly(trace ? _tracesOverlapped.Task : _overlapped.Task);
                return await inner.SendRequestAsync(request, context);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                if (trace) Interlocked.Decrement(ref _tracesInFlight);
            }
        }

        private static async Task WaitBriefly(Task overlap)
        {
            try
            {
                await overlap.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }
        }

        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null) =>
            inner.GetErrorResponse(errorCode, errorMessage, in id, methodName);

        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null) =>
            inner.GetErrorResponse(errorCode, errorMessage, methodName);
    }

    private sealed class BufferingSink : IJsonRpcResponseSink
    {
        public ArrayBufferWriter<byte> Output { get; } = new();
        public long BytesWritten => Output.WrittenCount;
        public bool StopRequested => false;

        public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken) =>
            WriteBatchItemAsync(response, report, cancellationToken);

        public ValueTask BeginBatchAsync(CancellationToken cancellationToken)
        {
            Output.Write("["u8);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            JsonRpcResponseWriter.Write(Output, response, EthereumJsonSerializer.JsonOptions);
            Output.Write(","u8);
            return ValueTask.CompletedTask;
        }

        public ValueTask EndBatchAsync(CancellationToken cancellationToken)
        {
            Output.Write("]"u8);
            return ValueTask.CompletedTask;
        }
    }
}
