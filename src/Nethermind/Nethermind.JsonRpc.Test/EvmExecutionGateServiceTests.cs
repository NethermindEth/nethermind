// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.IO.Pipelines;
using System.Buffers;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// End-to-end checks that <see cref="JsonRpcService"/> admits methods flagged
/// <see cref="JsonRpcMethodAttribute.IsEvmExecution"/> through the gate, and leaves every other method alone.
/// </summary>
// Shares the process-global queue-length metric with EvmExecutionGateTests.
[NonParallelizable]
public class EvmExecutionGateServiceTests
{
    /// <summary>eth_call, eth_estimateGas, eth_createAccessList, eth_fillTransaction, eth_simulateV1,
    /// debug_simulateV1.</summary>
    private const int GatedMethodCount = 6;

    private const string GatedMethod = "gated_execute";
    private const string UngatedMethod = "ungated_read";

    private static JsonRpcService CreateService(
        int permits,
        int maxQueueWaitMs,
        IGatedRpcModule module,
        bool gateEnabled = true,
        int webSocketsConcurrency = 1)
    {
        JsonRpcConfig config = new()
        {
            EthModuleConcurrentInstances = permits,
            EvmExecutionMaxQueueWaitMs = maxQueueWaitMs,
            EvmExecutionGateEnabled = gateEnabled,
            WebSocketsProcessingConcurrency = webSocketsConcurrency,
            Enabled = true,
            EnabledModules = ["Gated"],
        };

        RpcModuleProvider moduleProvider = new(Substitute.For<IFileSystem>(), config, new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IGatedRpcModule>(new SingletonFactory<IGatedRpcModule>(module), true));
        return new JsonRpcService(moduleProvider, LimboLogs.Instance, config);
    }

    /// <summary>A reader over the body, either as one buffer or revealed one byte per read so the batch
    /// source genuinely resumes mid-document.</summary>
    private static PipeReader CreateReader(string body, bool segmented)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        return segmented ? new OneBytePerReadPipeReader(bytes) : PipeReader.Create(new ReadOnlySequence<byte>(bytes));
    }

    /// <summary>Exposes one more byte on each <see cref="ReadAsync"/> over a multi-segment sequence, with no
    /// second thread. Deterministic where a <see cref="Pipe"/> is not: its backpressure never resumes against
    /// <c>JsonRpcProcessor</c>, which consumes nothing until it has a whole document.</summary>
    private sealed class OneBytePerReadPipeReader(byte[] bytes) : PipeReader
    {
        private readonly Segment _first = Segment.Build(bytes);
        private long _revealed;
        private long _consumed;

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_revealed < bytes.Length) _revealed++;
            ReadOnlySequence<byte> buffer = Slice(_consumed, _revealed);
            return new ValueTask<ReadResult>(new ReadResult(buffer, isCanceled: false, isCompleted: _revealed == bytes.Length));
        }

        private ReadOnlySequence<byte> Slice(long start, long end)
        {
            (Segment startSeg, int startIdx) = Locate(start);
            (Segment endSeg, int endIdx) = Locate(end);
            return new ReadOnlySequence<byte>(startSeg, startIdx, endSeg, endIdx);
        }

        private (Segment, int) Locate(long index)
        {
            Segment seg = _first;
            while (index > 0 && seg.Next is Segment next)
            {
                seg = next;
                index--;
            }
            return (seg, (int)index);
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            Segment seg = (Segment)consumed.GetObject()!;
            _consumed = seg.RunningIndex + consumed.GetInteger();
        }

        public override bool TryRead(out ReadResult result)
        {
            result = default;
            return false;
        }

        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public static Segment Build(byte[] source)
            {
                Segment head = new();
                Segment current = head;
                for (int i = 0; i < source.Length; i++)
                {
                    current.Memory = new ReadOnlyMemory<byte>(source, i, 1);
                    current.RunningIndex = i;
                    if (i < source.Length - 1)
                    {
                        Segment next = new();
                        current.Next = next;
                        current = next;
                    }
                }
                return head;
            }
        }
    }

    private static JsonRpcRequest Request(string method, int id = 1) =>
        new() { JsonRpc = "2.0", Method = method, Id = id };

    private static void AssertShed(JsonRpcResponse response)
    {
        JsonRpcErrorResponse error = (JsonRpcErrorResponse)response;
        Assert.That(error.Error!.Code, Is.EqualTo(ErrorCodes.LimitExceeded));
    }

    [Test]
    public async Task Gated_method_is_shed_while_every_permit_is_held()
    {
        using BlockingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 0, module);
        using JsonRpcContext context = new(RpcEndpoint.Http);

        ValueTask<JsonRpcResponse> inFlight = service.SendRequestAsync(Request(GatedMethod), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        AssertShed(await service.SendRequestAsync(Request(GatedMethod, 2), context));

        module.Release();
        await inFlight;
    }

    [Test]
    public async Task Ungated_method_is_served_while_the_gate_is_saturated()
    {
        using BlockingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 0, module);
        using JsonRpcContext context = new(RpcEndpoint.Http);

        ValueTask<JsonRpcResponse> inFlight = service.SendRequestAsync(Request(GatedMethod), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        JsonRpcResponse ungated = await service.SendRequestAsync(Request(UngatedMethod, 2), context);
        Assert.That(ungated, Is.Not.InstanceOf<JsonRpcErrorResponse>());

        module.Release();
        await inFlight;
    }

    [Test]
    public async Task Permit_is_released_when_the_gated_method_throws()
    {
        ThrowingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 0, module);
        using JsonRpcContext context = new(RpcEndpoint.Http);

        // Twice: the second call can only be admitted if the first returned its permit on the failure path.
        for (int i = 0; i < 2; i++)
        {
            JsonRpcResponse response = await service.SendRequestAsync(Request(GatedMethod, i), context);
            JsonRpcErrorResponse error = (JsonRpcErrorResponse)response;
            Assert.That(error.Error!.Code, Is.Not.EqualTo(ErrorCodes.LimitExceeded), "the permit must be released on failure, not leaked");
        }
    }

    [Test]
    public async Task Batch_items_are_shed_rather_than_queued()
    {
        // A batch is dispatched sequentially, so waiting would delay every later item on the same connection.
        // With a one-minute budget this test would hang if batch items were allowed to queue.
        using BlockingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 60_000, module);
        using JsonRpcContext context = new(RpcEndpoint.Http);

        ValueTask<JsonRpcResponse> inFlight = service.SendRequestAsync(Request(GatedMethod), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        JsonRpcRequest batchItem = Request(GatedMethod, 2);
        batchItem.IsBatchItem = true;

        Task<JsonRpcResponse> shed = service.SendRequestAsync(batchItem, context).AsTask();
        Assert.That(await Task.WhenAny(shed, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(shed), "a batch item must not wait for a slot");
        AssertShed(await shed);

        module.Release();
        await inFlight;
    }

    [Test]
    public async Task Http_request_waits_for_a_slot_and_is_then_served()
    {
        using BlockingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 30_000, module);
        using JsonRpcContext context = new(RpcEndpoint.Http);

        ValueTask<JsonRpcResponse> inFlight = service.SendRequestAsync(Request(GatedMethod), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Task<JsonRpcResponse> queued = service.SendRequestAsync(Request(GatedMethod, 2), context).AsTask();
        Assert.That(queued.IsCompleted, Is.False, "the gate is saturated, so the second request must wait");

        module.Release();
        await inFlight;

        // The waiter takes the freed permit and enters the module; release again so it can finish.
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        module.Release();

        JsonRpcResponse served = await queued;
        Assert.That(served, Is.Not.InstanceOf<JsonRpcErrorResponse>());
    }

    [Test]
    public async Task Batch_items_are_shed_when_fed_through_the_processor([Values] bool segmentedInput)
    {
        // Driven through JsonRpcProcessor rather than by setting IsBatchItem here, so deleting the processor's
        // assignment fails this test. A batch is dispatched sequentially, so a gated item must shed promptly and
        // the ungated item behind it must still be served rather than waiting out the 60s budget.
        using BlockingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 60_000, module);
        using JsonRpcContext context = new(RpcEndpoint.Http);

        ValueTask<JsonRpcResponse> inFlight = service.SendRequestAsync(Request(GatedMethod), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        JsonRpcProcessor processor = new(service, new JsonRpcConfig(), Substitute.For<IFileSystem>(), LimboLogs.Instance, null);
        string batch = $$"""[{"jsonrpc":"2.0","method":"{{GatedMethod}}","id":1},{"jsonrpc":"2.0","method":"{{UngatedMethod}}","id":2}]""";
        CollectingSink sink = new();

        Task drain = processor
            .ProcessAsync(CreateReader(batch, segmentedInput), context, sink, new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument))
            .AsTask();

        Assert.That(await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(20))), Is.SameAs(drain),
            "a batch item must not wait for a slot, or the whole batch stalls behind it");
        await drain;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sink.Responses, Has.Count.EqualTo(2));
            Assert.That(((JsonRpcErrorResponse)sink.Responses[0]).Error!.Code, Is.EqualTo(ErrorCodes.LimitExceeded));
            Assert.That(sink.Responses[1], Is.Not.InstanceOf<JsonRpcErrorResponse>());
        }

        module.Release();
        await inFlight;
    }

    [Test]
    public async Task A_disabled_gate_admits_every_caller()
    {
        // EvmExecutionGateEnabled = false must restore the previous behaviour exactly: no permit, no shedding.
        using BlockingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 0, module, gateEnabled: false);
        using JsonRpcContext context = new(RpcEndpoint.Http);

        ValueTask<JsonRpcResponse> first = service.SendRequestAsync(Request(GatedMethod), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        // With one permit this would shed; disabled, it must enter the module too.
        ValueTask<JsonRpcResponse> second = service.SendRequestAsync(Request(GatedMethod, 2), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        module.Release();
        module.Release();
        JsonRpcResponse firstResponse = await first;
        JsonRpcResponse secondResponse = await second;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResponse, Is.Not.InstanceOf<JsonRpcErrorResponse>());
            Assert.That(secondResponse, Is.Not.InstanceOf<JsonRpcErrorResponse>());
        }
    }

    [TestCase(1, false, TestName = "A single-lane WebSocket connection sheds rather than queues")]
    [TestCase(2, true, TestName = "A multi-lane WebSocket connection may queue")]
    public async Task WebSocket_queueing_follows_the_processing_concurrency(int wsConcurrency, bool expectQueueing)
    {
        // On a lane that processes one request at a time, waiting only delays the calls behind it.
        using BlockingGatedModule module = new();
        JsonRpcService service = CreateService(permits: 1, maxQueueWaitMs: 60_000, module, webSocketsConcurrency: wsConcurrency);
        using JsonRpcContext context = new(RpcEndpoint.Ws);

        ValueTask<JsonRpcResponse> inFlight = service.SendRequestAsync(Request(GatedMethod), context);
        await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Task<JsonRpcResponse> second = service.SendRequestAsync(Request(GatedMethod, 2), context).AsTask();
        Task finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5)));

        if (expectQueueing)
        {
            Assert.That(finished, Is.Not.SameAs(second), "a multi-lane connection should wait for a slot");
            module.Release();
            await inFlight;
            await module.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            module.Release();
            Assert.That(await second, Is.Not.InstanceOf<JsonRpcErrorResponse>());
            return;
        }

        Assert.That(finished, Is.SameAs(second), "a single-lane connection must not wait for a slot");
        AssertShed(await second);
        module.Release();
        await inFlight;
    }

    [Test]
    public void Methods_that_reach_the_evm_without_crossing_the_service_are_gated()
    {
        // eth_fillTransaction estimates gas by calling eth_estimateGas on the module directly when the caller
        // omits it (EthRpcModule.eth_fillTransaction), so that execution never passes through JsonRpcService and
        // the gate only sees it if eth_fillTransaction is itself flagged. It is shareable, so without the flag it
        // reaches EstimateGasShareable outside the aggregate cap.
        MethodInfo fillTransaction = typeof(IEthRpcModule).GetMethod(nameof(IEthRpcModule.eth_fillTransaction))!;

        Assert.That(fillTransaction.GetCustomAttribute<JsonRpcMethodAttribute>()?.IsEvmExecution, Is.True,
            "eth_fillTransaction estimates gas in-process and would otherwise escape the gate");
    }

    [Test]
    public void No_gated_method_can_return_a_streamable_result()
    {
        // The permit is released when the invocation returns, so a gated method whose result re-executes while the
        // response is written would run the EVM outside the gate. Nothing enforces that at runtime by design - a
        // per-request check would sit on the hot path - so it is pinned here.
        //
        // Checked against the concrete streaming types rather than the declared payload: every streaming result is
        // substituted at runtime under a base (GethLikeTxTraceStreamingSingleResult : GethLikeTxTrace), so a test
        // that looked for a declared IStreamableResult would never match and would pass vacuously.
        // Anchored to named assemblies rather than whatever the host happens to have loaded, which varies with
        // test filtering and sharding and could silently narrow the guard to nothing. Scope is therefore what this
        // project references: plugin modules (Merge, Optimism, Taiko) are NOT covered, and a plugin that flags
        // IsEvmExecution on a streaming-capable payload would not be caught here.
        Assembly[] scanned = [typeof(IStreamableResult).Assembly];

        Type[] streamingTypes = [.. scanned
            .SelectMany(static a => a.GetTypes())
            .Where(static t => !t.IsAbstract && !t.IsInterface && typeof(IStreamableResult).IsAssignableFrom(t))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(streamingTypes, Is.Not.Empty, "no IStreamableResult implementations were found to check against");
            Assert.That(AllRpcModuleInterfaces(scanned), Does.Contain(typeof(IEthRpcModule)),
                "the eth module was not in the scanned set, so gated methods would not be seen");
        }

        List<string> offenders = [];
        int gatedMethods = 0;
        int payloadsCompared = 0;
        foreach (Type moduleType in AllRpcModuleInterfaces(scanned))
        {
            foreach (MethodInfo method in moduleType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.GetCustomAttribute<JsonRpcMethodAttribute>() is not { IsEvmExecution: true }) continue;

                gatedMethods++;
                foreach (Type payload in PayloadTypes(method))
                {
                    payloadsCompared++;
                    foreach (Type streaming in streamingTypes)
                    {
                        if (payload.IsAssignableFrom(streaming))
                        {
                            offenders.Add($"{moduleType.Name}.{method.Name} -> {payload.Name} accepts {streaming.Name}");
                        }
                    }
                }
            }
        }

        // The real vacuity risk is here, not in the two sets above: if PayloadTypes stops unwrapping a return
        // shape it silently yields nothing and the guard passes having compared nothing at all.
        Assert.That(gatedMethods, Is.GreaterThanOrEqualTo(GatedMethodCount),
            "fewer gated methods were found than are flagged IsEvmExecution, so some were not examined");
        Assert.That(payloadsCompared, Is.GreaterThanOrEqualTo(gatedMethods),
            "at least one payload type per gated method must have been extracted and compared");

        Assert.That(offenders, Is.Empty,
            "a method flagged IsEvmExecution must not be able to return an IStreamableResult; see JsonRpcMethodAttribute.IsEvmExecution");
    }

    private static IEnumerable<Type> AllRpcModuleInterfaces(Assembly[] assemblies) =>
        assemblies
            .SelectMany(static a => a.GetExportedTypes())
            .Where(static t => t.IsInterface && typeof(IRpcModule).IsAssignableFrom(t));

    /// <summary>Payload types a JSON-RPC method can resolve to, unwrapping Task/ValueTask and the result wrapper.</summary>
    private static IEnumerable<Type> PayloadTypes(MethodInfo method)
    {
        Type returnType = method.ReturnType;
        if (returnType.IsGenericType)
        {
            Type definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                returnType = returnType.GenericTypeArguments[0];
            }
        }

        return returnType.IsGenericType ? returnType.GenericTypeArguments : [];
    }

    [RpcModule("Gated")]
    public interface IGatedRpcModule : IRpcModule
    {
        [JsonRpcMethod(
            Description = "Test method used to verify admission through the EVM execution gate.",
            IsImplemented = true,
            IsSharable = true,
            IsEvmExecution = true)]
        Task<ResultWrapper<int>> gated_execute();

        [JsonRpcMethod(
            Description = "Test method used to verify that ungated methods bypass the EVM execution gate.",
            IsImplemented = true,
            IsSharable = true)]
        ResultWrapper<int> ungated_read();
    }

    /// <summary>Collects what the processor writes, so a batch can be driven end to end without a transport.</summary>
    private sealed class CollectingSink : IJsonRpcResponseSink
    {
        internal List<JsonRpcResponse> Responses { get; } = [];

        public long BytesWritten => 0;
        public bool StopRequested => false;

        public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            Responses.Add(response);
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginBatchAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            Responses.Add(response);
            return ValueTask.CompletedTask;
        }

        public ValueTask EndBatchAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class BlockingGatedModule : IGatedRpcModule, IDisposable
    {
        private readonly SemaphoreSlim _release = new(0);

        internal SemaphoreSlim Entered { get; } = new(0);

        public void Dispose()
        {
            _release.Dispose();
            Entered.Dispose();
        }

        public async Task<ResultWrapper<int>> gated_execute()
        {
            Entered.Release();
            await _release.WaitAsync();
            return ResultWrapper<int>.Success(1);
        }

        public ResultWrapper<int> ungated_read() => ResultWrapper<int>.Success(2);

        internal void Release() => _release.Release();
    }

    private sealed class ThrowingGatedModule : IGatedRpcModule
    {
        public Task<ResultWrapper<int>> gated_execute() => throw new InvalidOperationException("boom");

        public ResultWrapper<int> ungated_read() => ResultWrapper<int>.Success(2);
    }
}
