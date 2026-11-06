// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
[NonParallelizable]
public class RpcExecutionAdmissionTests
{
    [Test]
    public async Task Queued_execution_holds_no_shared_slot_and_keeps_its_own_request_context()
    {
        using Harness harness = new();
        JsonRpcContext firstContext = new(RpcEndpoint.Http);
        JsonRpcContext secondContext = new(RpcEndpoint.Http);
        Task<JsonRpcResponse> first = harness.Send("test_execute", firstContext, 1);
        Task<JsonRpcResponse> second = harness.Send("test_execute", secondContext, 2);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.State.Started, Is.EqualTo(1));
                Assert.That(second.IsCompleted, Is.False);
            }
            using JsonRpcResponse head = await harness.Send("test_head");
            Assert.That(((ResultWrapper<int>)head).Data, Is.EqualTo(1), "a queued heavy call must not take the head read's shared slot");
        }
        finally
        {
            harness.State.Release.TrySetResult(true);
            using JsonRpcResponse firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(5));
            using JsonRpcResponse secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(((ResultWrapper<int>)firstResponse).Data, Is.EqualTo(1));
                Assert.That(((ResultWrapper<int>)secondResponse).Data, Is.EqualTo(2));
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.State.Contexts.ToArray(), Is.EqualTo(new[] { firstContext, secondContext }));
            Assert.That(JsonRpcContext.Current, Is.Null);
        }
    }

    [Test]
    public async Task Streamed_execution_keeps_its_permit_until_disposal_and_disposal_is_idempotent()
    {
        using Harness harness = new();
        using JsonRpcResponse stream = await harness.Send("test_stream");
        Task<JsonRpcResponse> first = harness.Send("test_execute", null, 1);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.State.Started, Is.Zero);
                Assert.That(first.IsCompleted, Is.False);
            }
            using JsonRpcResponse head = await harness.Send("test_head");
            Assert.That(((ResultWrapper<int>)head).Data, Is.Zero);
            stream.Dispose();
            await harness.State.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stream.Dispose();
            Task<JsonRpcResponse> second = harness.Send("test_execute", null, 2);
            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(harness.State.Started, Is.EqualTo(1), "disposing twice must not admit a second executor");
                    Assert.That(second.IsCompleted, Is.False);
                }
            }
            finally
            {
                harness.State.Release.TrySetResult(true);
                using JsonRpcResponse secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(((ResultWrapper<int>)secondResponse).Data, Is.EqualTo(2));
            }
        }
        finally
        {
            stream.Dispose();
            harness.State.Release.TrySetResult(true);
            using JsonRpcResponse firstResponse = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(((ResultWrapper<int>)firstResponse).Data, Is.EqualTo(1));
        }
    }

    [TestCase("binding", ErrorCodes.InvalidParams, 1)]
    [TestCase("invocation", ErrorCodes.InternalError, 2)]
    [TestCase("context", ErrorCodes.InternalError, 1)]
    public async Task Failed_request_returns_execution_and_module_leases(string failure, int expectedCode, int nextResult)
    {
        using Harness harness = new(timeout: 0);
        harness.State.Release.TrySetResult(true);
        if (failure == "context") harness.State.ContextFailures = 1;
        object value = failure == "binding" ? "not-an-integer" : -1;
        using JsonRpcResponse failed = await harness.Send("test_execute", null, value);
        Assert.That(((JsonRpcErrorResponse)failed).Error!.Code, Is.EqualTo(expectedCode));

        using JsonRpcResponse next = await harness.Send("test_execute", null, 1);
        Assert.That(((ResultWrapper<int>)next).Data, Is.EqualTo(nextResult), "a failed request must not consume the only execution permit");
        using JsonRpcResponse head = await harness.Send("test_head");
        Assert.That(((ResultWrapper<int>)head).Data, Is.EqualTo(nextResult), "failed and subsequent calls must return their shared slots");
    }

    [Test]
    public async Task Module_rental_timeout_does_not_consume_execution_admission()
    {
        using Harness harness = new(timeout: 0);
        IAdmissionModule rented = await harness.Pool.GetModule(false);
        try
        {
            using JsonRpcResponse failed = await harness.Send("test_exclusive");
            Assert.That(((JsonRpcErrorResponse)failed).Error!.Code, Is.EqualTo(ErrorCodes.ModuleTimeout));
        }
        finally
        {
            harness.Pool.ReturnModule(rented);
        }
        using JsonRpcResponse next = await harness.Send("test_exclusive");
        Assert.That(((ResultWrapper<int>)next).Data, Is.EqualTo(1));
    }

    [RpcModule(ModuleType.Eth)]
    public interface IAdmissionModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Test execution-heavy method used to verify execution admission.", IsExecutionHeavy = true)]
        Task<ResultWrapper<int>> test_execute(int value);

        [JsonRpcMethod(Description = "Test light method used to verify it bypasses execution admission.")]
        ResultWrapper<int> test_head();

        [JsonRpcMethod(Description = "Test streamed execution-heavy method used to verify permit lifetime.", IsExecutionHeavy = true)]
        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> test_stream();

        [JsonRpcMethod(Description = "Test non-sharable execution-heavy method used to verify module rental timeouts.", IsExecutionHeavy = true, IsSharable = false)]
        ResultWrapper<int> test_exclusive();
    }

    private sealed class State : IDisposable
    {
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<JsonRpcContext?> Contexts = new();
        public readonly CancellationTokenSource StreamTimeout = new();
        public int Started;
        public int ContextFailures;

        public void Dispose()
        {
            Release.TrySetResult(true);
            StreamTimeout.Dispose();
        }
    }

    private sealed class AdmissionModule(State state) : IAdmissionModule, IContextAwareRpcModule
    {
        private JsonRpcContext _context = null!;
        public JsonRpcContext Context
        {
            get => _context;
            set
            {
                if (Interlocked.Exchange(ref state.ContextFailures, 0) != 0) throw new InvalidOperationException("Context unavailable");
                _context = value;
            }
        }

        private int Start()
        {
            int started = Interlocked.Increment(ref state.Started);
            state.Contexts.Enqueue(JsonRpcContext.Current);
            state.Entered.TrySetResult(true);
            return started;
        }

        public async Task<ResultWrapper<int>> test_execute(int value)
        {
            int started = Start();
            if (value < 0) throw new InvalidOperationException("Execution failed");
            await state.Release.Task;
            return ResultWrapper<int>.Success(started);
        }

        public ResultWrapper<int> test_head() => ResultWrapper<int>.Success(Volatile.Read(ref state.Started));

        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> test_stream() =>
            ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success(new ParityTxTraceStreamingResult<ParityTxTraceFromReplay>(
                static (_, _, _) => { }, state.StreamTimeout, NullLogger.Instance));

        public ResultWrapper<int> test_exclusive() => ResultWrapper<int>.Success(Start());
    }

    private sealed class AdmissionFactory(State state) : ModuleFactoryBase<IAdmissionModule>
    {
        public override IAdmissionModule Create() => new AdmissionModule(state);
    }

    private sealed class Harness : IDisposable
    {
        public readonly State State = new();
        public readonly BoundedModulePool<IAdmissionModule> Pool;
        private readonly GCKeeper _keeper = new(NoGCStrategy.Instance, NullLogManager.Instance);
        private readonly JsonRpcService _service;

        public Harness(int timeout = 5_000)
        {
            JsonRpcConfig config = new() { EthModuleConcurrentInstances = 1, MaxConcurrentSharedRequests = 2, Timeout = timeout, EnabledModules = ["eth"] };
            Pool = new BoundedModulePool<IAdmissionModule>(new AdmissionFactory(State), 1, timeout, new RpcLimits(queuedLimit: 1, sharedLimit: 2));
            RpcModuleProvider provider = new(new RealFileSystem(), config, new EthereumJsonSerializer(), NullLogManager.Instance);
            provider.Register(Pool);
            _service = new JsonRpcService(provider, NullLogManager.Instance, config, _keeper);
        }

        public Task<JsonRpcResponse> Send(string method, JsonRpcContext? context = null, params object?[] parameters) =>
            _service.SendRequestAsync(RpcTest.BuildJsonRequest(method, parameters), context ?? new JsonRpcContext(RpcEndpoint.Http)).AsTask();

        public void Dispose()
        {
            State.Dispose();
            _keeper.Dispose();
        }
    }
}
