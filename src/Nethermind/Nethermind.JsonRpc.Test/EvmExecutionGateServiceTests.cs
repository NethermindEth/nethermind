// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// End-to-end checks that <see cref="JsonRpcService"/> admits methods flagged
/// <see cref="JsonRpcMethodAttribute.IsEvmExecution"/> through the gate, and leaves every other method alone.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class EvmExecutionGateServiceTests
{
    private const string GatedMethod = "gated_execute";
    private const string UngatedMethod = "ungated_read";

    private static JsonRpcService CreateService(int permits, int maxQueueWaitMs, IGatedRpcModule module)
    {
        JsonRpcConfig config = new()
        {
            EthModuleConcurrentInstances = permits,
            EvmExecutionMaxQueueWaitMs = maxQueueWaitMs,
            Enabled = true,
            EnabledModules = ["Gated"],
        };

        RpcModuleProvider moduleProvider = new(Substitute.For<IFileSystem>(), config, new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IGatedRpcModule>(new SingletonFactory<IGatedRpcModule>(module), true));
        return new JsonRpcService(moduleProvider, LimboLogs.Instance, config);
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
        BlockingGatedModule module = new();
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
        BlockingGatedModule module = new();
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
        BlockingGatedModule module = new();
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
        BlockingGatedModule module = new();
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

    private sealed class BlockingGatedModule : IGatedRpcModule
    {
        private readonly SemaphoreSlim _release = new(0);

        internal SemaphoreSlim Entered { get; } = new(0);

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
