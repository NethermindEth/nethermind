// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>Configured limits, concurrency, timeouts and failures of the services behind the tools.</summary>
[Parallelizable(ParallelScope.Self)]
public class McpLimitsTests
{
    private const string IdentityPrecompile = "0x0000000000000000000000000000000000000004";
    private const long MaxCallGas = 100_000;
    private const int MaxCallDataSize = 16;
    private const string Secret = "internal-secret-detail";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private McpTestNode _limited = null!;
    private McpClient _limitedClient = null!;
    private SeededChain _seeded = null!;

    /// <summary>One node with tight log and call limits, shared by the read-only limit tests.</summary>
    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _limited = await McpTestNode.Create(c =>
        {
            c.MaxConcurrentToolCalls = 64;
            c.MaxLogBlockRange = 1;
            c.MaxLogs = 1;
            c.MaxCallGas = MaxCallGas;
            c.MaxCallDataSize = MaxCallDataSize;
        });
        _seeded = await _limited.Seed();
        _limitedClient = await _limited.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_limitedClient is not null) await _limitedClient.DisposeAsync();
        if (_limited is not null) await _limited.DisposeAsync();
    }

    [Test]
    public async Task Log_range_above_limit_is_paged()
    {
        JsonElement page = McpAssert.Success(await Limited("get_logs", ("fromBlock", "earliest"), ("toBlock", "latest")));

        Assert.That(page.GetProperty("truncated").GetBoolean(), Is.True);
        Assert.That(page.GetProperty("nextCursor").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task Log_count_above_limit_is_paged()
    {
        string block = McpAssert.Hex(_seeded.Block.Number);

        JsonElement page = McpAssert.Success(await Limited("get_logs", ("fromBlock", block), ("toBlock", block)));

        Assert.That(page.GetProperty("truncated").GetBoolean(), Is.True);
        Assert.That(page.GetProperty("nextCursor").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task Log_query_within_limits_succeeds()
    {
        string block = McpAssert.Hex(_seeded.Block.Number);

        JsonElement logs = McpAssert.Success(await Limited("get_logs",
            ("fromBlock", block), ("toBlock", block), ("topics", JsonSerializer.SerializeToElement(new object?[] { null, SeededChain.TopicB.ToString() }))));

        Assert.That(logs.GetProperty("logs").GetArrayLength(), Is.EqualTo(1));
    }

    [TestCase(MaxCallGas, true)]
    [TestCase(MaxCallGas + 1, false)]
    public async Task Call_gas_is_capped(long gas, bool allowed)
    {
        CallToolResult result = await Limited("call", ("to", IdentityPrecompile), ("data", "0x01"), ("gas", gas.ToString()));

        if (allowed) Assert.That(McpAssert.Success(result).GetString(), Is.EqualTo("0x01"));
        else McpAssert.Error(result, McpAssert.InvalidInput);
    }

    [TestCase(MaxCallDataSize, true)]
    [TestCase(MaxCallDataSize + 1, false)]
    [TestCase(64 * 1024, false)]
    public async Task Call_data_size_is_capped(int size, bool allowed)
    {
        string data = Bytes.ToHexString(Enumerable.Repeat((byte)0xab, size).ToArray(), true);

        CallToolResult result = await Limited("call", ("to", IdentityPrecompile), ("data", data), ("gas", "50000"));

        if (allowed) Assert.That(McpAssert.Success(result).GetString(), Is.EqualTo(data));
        else McpAssert.Error(result, McpAssert.InvalidInput);
    }

    [Test]
    public async Task Result_above_size_limit_is_resource_exhausted([Values("chain_info", "get_block")] string toolName)
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.MaxResultSize = 16);
        await using McpClient client = await node.CreateClient();

        CallToolResult result = await McpToolCalls.Call(client, toolName, toolName == "get_block" ? [("block", "latest")] : []);

        McpAssert.NoInternals(McpAssert.Error(result, McpAssert.ResourceExhausted));
    }

    [Test]
    public void Error_data_is_cut_at_4_KB()
    {
        CallToolResult result = McpToolExecutor.Error(McpToolErrorCodes.ExecutionReverted, "reverted", "0x" + new string('a', 20_000));

        JsonElement error = McpAssert.Error(result, McpAssert.ExecutionReverted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("data").GetString(), Has.Length.EqualTo(2 + 2 * McpToolExecutor.MaxErrorDataBytes));
            Assert.That(error.GetProperty("dataTruncated").GetBoolean(), Is.True);
            Assert.That(error.GetProperty("dataSize").GetInt32(), Is.EqualTo(10_000));
        }
    }

    [Test]
    public async Task Oversized_error_respects_the_result_size_limit()
    {
        await using McpTestNode node = await McpTestNode.Create(static c => c.MaxResultSize = 200, start: false);
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();

        CallToolResult result = await executor.ExecuteLocalAsync("test",
            static _ => Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, new string('x', 400))), CancellationToken.None);

        McpAssert.Error(result, McpAssert.InvalidInput);
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(result.Content.OfType<TextContentBlock>().Single().Text), Is.LessThanOrEqualTo(200));
    }

    [Test]
    public async Task Slow_module_times_out()
    {
        BlockingEthModule blocking = new();
        await using McpTestNode node = await CreateWithFaults(c => c.ToolTimeout = 300);
        FaultInjectingRpcModuleProvider provider = Faults(node);
        provider.Override(nameof(IEthRpcModule.eth_getBalance), blocking.Module);
        await using McpClient client = await node.CreateClient();

        Stopwatch stopwatch = Stopwatch.StartNew();
        CallToolResult result = await GetBalance(client);
        stopwatch.Stop();
        blocking.Release();

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Error(result, McpAssert.Timeout);
            Assert.That(stopwatch.Elapsed, Is.LessThan(WaitLimit), "the tool must answer at its timeout, not when the module returns");
        }

        await WaitUntil(() => provider.Returned == 1, "the module must be returned once the slow call completes");
    }

    [Test]
    public async Task Call_waits_briefly_for_a_busy_slot()
    {
        BlockingEthModule blocking = new();
        await using McpTestNode node = await CreateWithFaults(c => c.MaxConcurrentToolCalls = 1);
        FaultInjectingRpcModuleProvider provider = Faults(node);
        provider.Override(nameof(IEthRpcModule.eth_getBalance), blocking.Module);
        await using McpClient client = await node.CreateClient();

        Task<CallToolResult> inFlight = GetBalance(client);
        await blocking.Entered.WaitAsync(WaitLimit);
        Task<CallToolResult> queued = McpToolCalls.Call(client, "chain_info", []);
        await Task.Delay(300);
        blocking.Release();

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Success(await queued.WaitAsync(WaitLimit));
            McpAssert.Success(await inFlight.WaitAsync(WaitLimit));
        }
    }

    [Test]
    public async Task Shutdown_rejects_new_calls_and_waits_for_running_bodies()
    {
        await using McpTestNode node = await McpTestNode.Create(start: false);
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool observedCancellation = false;

        Task<CallToolResult> running = executor.ExecuteLocalAsync("test", async token =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // Simulates a body that still reads node services briefly after its token fires.
                await Task.Delay(200, CancellationToken.None);
                observedCancellation = true;
                throw;
            }

            return McpToolExecutor.Error(McpToolErrorCodes.InternalError, "unreachable");
        }, CancellationToken.None);
        await entered.Task.WaitAsync(WaitLimit);

        using CancellationTokenSource budget = new(WaitLimit);
        bool drained = await executor.StopAsync(budget.Token);
        CallToolResult rejected = await executor.ExecuteLocalAsync("test", static _ => Task.FromResult(McpToolExecutor.Error(McpToolErrorCodes.InternalError, "must not run")), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained, Is.True);
            Assert.That(observedCancellation, Is.True, "stop must cancel the body token and wait until the body has finished");
            Assert.That(McpAssert.Error(rejected, McpAssert.Unavailable).GetProperty("message").GetString(), Does.Contain("shutting down"));
            McpAssert.Error(await running.WaitAsync(WaitLimit), McpAssert.Unavailable);
        }
    }

    [Test]
    public async Task Shutdown_gives_up_on_a_body_stuck_in_the_node()
    {
        await using McpTestNode node = await McpTestNode.Create(start: false);
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CallToolResult> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<CallToolResult> stuck = executor.ExecuteLocalAsync("test", _ =>
        {
            entered.TrySetResult();
            return gate.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(WaitLimit);

        using CancellationTokenSource budget = new(TimeSpan.FromMilliseconds(200));
        bool drained = await executor.StopAsync(budget.Token);
        gate.SetResult(McpToolExecutor.Error(McpToolErrorCodes.InternalError, "done"));
        await stuck.WaitAsync(WaitLimit);

        Assert.That(drained, Is.False, "an uninterruptible body is logged and left behind once the stop budget is spent");
    }

    [Test]
    public async Task Detached_work_keeps_the_concurrency_slot_until_it_finishes()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.MaxConcurrentToolCalls = 1, start: false);
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<IEthRpcModule, CancellationToken, Task<CallToolResult>> chainId = (module, _) => Task.FromResult(executor.Success(module.eth_chainId().Data));

        CallToolResult parent = await executor.ExecuteAsync("test", nameof(IEthRpcModule.eth_chainId), (module, _) =>
        {
            executor.TrackDetached(detached.Task);
            return Task.FromResult(executor.Success(module.eth_chainId().Data));
        }, CancellationToken.None);
        CallToolResult whileDetached = await executor.ExecuteAsync("test", nameof(IEthRpcModule.eth_chainId), chainId, CancellationToken.None);
        CallToolResult local = await executor.ExecuteLocalAsync("test", _ => Task.FromResult(executor.Success(1)), CancellationToken.None);

        detached.SetResult();

        using (Assert.EnterMultipleScope())
        {
            McpAssert.Success(parent);
            McpAssert.Error(whileDetached, McpAssert.ResourceExhausted);
            McpAssert.Success(local);
            McpAssert.Success(await executor.ExecuteAsync("test", nameof(IEthRpcModule.eth_chainId), chainId, CancellationToken.None));
        }
    }

    [Test]
    public async Task Calls_beyond_concurrency_limit_fail_after_a_short_wait_while_others_succeed()
    {
        BlockingEthModule blocking = new();
        await using McpTestNode node = await CreateWithFaults(c => c.MaxConcurrentToolCalls = 1);
        FaultInjectingRpcModuleProvider provider = Faults(node);
        provider.Override(nameof(IEthRpcModule.eth_getBalance), blocking.Module);
        await using McpClient client = await node.CreateClient();

        Task<CallToolResult> inFlight = GetBalance(client);
        await blocking.Entered.WaitAsync(WaitLimit);

        CallToolResult rejected = await McpToolCalls.Call(client, "chain_info", []);
        McpAssert.Error(rejected, McpAssert.ResourceExhausted);
        McpAssert.Success(await McpToolCalls.Call(client, "node_status", []));

        blocking.Release();
        McpAssert.Quantity(McpAssert.Success(await inFlight.WaitAsync(WaitLimit)).GetProperty("balance"), McpAssert.Hex(BlockingEthModule.Balance));

        await WaitUntil(() => provider.Returned == 1, "the module must be returned");
        McpAssert.Success(await McpToolCalls.Call(client, "chain_info", []));
    }

    [Test]
    public async Task Parallel_calls_within_limit_all_succeed()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.MaxConcurrentToolCalls = 8);
        await using McpClient client = await node.CreateClient();

        CallToolResult[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => McpToolCalls.Call(client, "chain_info", [])));

        foreach (CallToolResult result in results) McpAssert.Success(result);
    }

    [Test]
    public async Task Client_cancellation_propagates_and_frees_the_slot()
    {
        BlockingEthModule blocking = new();
        await using McpTestNode node = await CreateWithFaults(c => c.MaxConcurrentToolCalls = 1);
        FaultInjectingRpcModuleProvider provider = Faults(node);
        provider.Override(nameof(IEthRpcModule.eth_getBalance), blocking.Module);
        await using McpClient client = await node.CreateClient();

        using CancellationTokenSource cts = new();
        Task<CallToolResult> cancelled = GetBalance(client, cts.Token);
        await blocking.Entered.WaitAsync(WaitLimit);
        await cts.CancelAsync();

        Assert.CatchAsync<OperationCanceledException>(async () => await cancelled);

        blocking.Release();
        await WaitUntil(() => provider.Returned == 1, "the module must be returned after the cancelled call unwinds");
        await WaitUntilAsync(async () => (await McpToolCalls.Call(client, "chain_info", [])).IsError != true,
            "the concurrency slot must be released after a cancelled call");
    }

    [Test]
    public async Task Module_failure_is_internal_error_without_details()
    {
        IEthRpcModule module = Substitute.For<IEthRpcModule>();
        module.eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())
            .Returns<Task<ResultWrapper<UInt256?>>>(_ => throw new InvalidOperationException(Secret));
        await using McpTestNode node = await CreateWithFaults();
        FaultInjectingRpcModuleProvider provider = Faults(node);
        provider.Override(nameof(IEthRpcModule.eth_getBalance), module);
        await using McpClient client = await node.CreateClient();

        JsonElement error = McpAssert.Error(await GetBalance(client), McpAssert.InternalError);

        McpAssert.NoInternals(error, Secret, nameof(InvalidOperationException));
        await WaitUntil(() => provider.Returned == 1, "a failing module must still be returned to its pool");
    }

    [Test]
    public async Task Module_error_result_is_mapped_without_leaking_control_characters()
    {
        IEthRpcModule module = Substitute.For<IEthRpcModule>();
        module.eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())
            .Returns(Task.FromResult(ResultWrapper<UInt256?>.Fail("bad\nthing\u0000happened", ErrorCodes.InternalError)));
        await using McpTestNode node = await CreateWithFaults();
        FaultInjectingRpcModuleProvider provider = Faults(node);
        provider.Override(nameof(IEthRpcModule.eth_getBalance), module);
        await using McpClient client = await node.CreateClient();

        JsonElement error = McpAssert.Error(await GetBalance(client), McpAssert.InternalError);

        McpAssert.NoInternals(error, "bad\nthing");
    }

    private static IEnumerable<TestCaseData> RentalFailures()
    {
        yield return new TestCaseData(new ModuleRentalTimeoutException("pool drained"), McpAssert.ResourceExhausted).SetName("Pool_rental_timeout_is_resource_exhausted");
        yield return new TestCaseData(new LimitExceededException("queue full"), McpAssert.ResourceExhausted).SetName("Rpc_queue_limit_is_resource_exhausted");
        yield return new TestCaseData(new ResourceNotFoundException("pruned"), McpAssert.Unavailable).SetName("Pruned_history_is_unavailable");
        yield return new TestCaseData(new InvalidOperationException(Secret), McpAssert.InternalError).SetName("Unexpected_rental_failure_is_internal_error");
    }

    [TestCaseSource(nameof(RentalFailures))]
    public async Task Rental_failure_is_mapped(Exception failure, string expectedCode)
    {
        await using McpTestNode node = await CreateWithFaults();
        FaultInjectingRpcModuleProvider provider = Faults(node);
        provider.Throw(nameof(IEthRpcModule.eth_getBalance), failure);
        await using McpClient client = await node.CreateClient();

        JsonElement error = McpAssert.Error(await GetBalance(client), expectedCode);

        McpAssert.NoInternals(error, Secret);
    }

    /// <summary>Creates a node whose <see cref="IRpcModuleProvider"/> is decorated with a <see cref="FaultInjectingRpcModuleProvider"/>.</summary>
    private static Task<McpTestNode> CreateWithFaults(Action<McpConfig>? configure = null) =>
        McpTestNode.Create(configure, static builder => builder
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));

    private static FaultInjectingRpcModuleProvider Faults(McpTestNode node) =>
        (FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();

    private Task<CallToolResult> Limited(string toolName, params (string Name, object? Value)[] args) =>
        McpToolCalls.Call(_limitedClient, toolName, args);

    private static Task<CallToolResult> GetBalance(McpClient client, CancellationToken cancellationToken = default) =>
        McpToolCalls.Call(client, "get_balance", [("address", TestItem.AddressC.ToString())], cancellationToken);

    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > WaitLimit) Assert.Fail(message);
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (stopwatch.Elapsed > WaitLimit) Assert.Fail(message);
            await Task.Delay(50);
        }
    }

    /// <summary>An eth module whose <c>eth_getBalance</c> blocks until released.</summary>
    private sealed class BlockingEthModule
    {
        public static readonly UInt256 Balance = 42;

        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingEthModule()
        {
            Module = Substitute.For<IEthRpcModule>();
            Module.eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(async _ =>
            {
                _entered.TrySetResult();
                await _gate.Task;
                return ResultWrapper<UInt256?>.Success(Balance);
            });
        }

        public IEthRpcModule Module { get; }

        public Task Entered => _entered.Task;

        public void Release() => _gate.TrySetResult();
    }
}
