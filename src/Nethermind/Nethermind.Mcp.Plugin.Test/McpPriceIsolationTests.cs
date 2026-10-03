// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.Mcp.Plugin.Test;

[NonParallelizable]
public class McpPriceIsolationTests
{
    private static readonly Address Weth = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");
    private static readonly Address NativeFeed = new("0x5f4eC3Df9cbd43714FE2740f5E3616155c5b8419");

    [TestCase("fee_estimate")]
    [TestCase("token_balances")]
    [TestCase("explain_transaction")]
    public async Task Stalled_optional_feed_preserves_main_result(string tool)
    {
        await using McpTestNode node = await CreateNode(1000);
        Transaction deposit = await Deposit(node);
        PriceRpcProvider provider = (PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeCall = (method, args) =>
        {
            if (tool == "fee_estimate" && method == nameof(IEthRpcModule.eth_feeHistory)) Thread.Sleep(850);
            if (IsPriceCall(method, args)) Thread.Sleep(1500);
        };
        await using McpClient client = await node.CreateClient();
        (string, object?)[] args = tool switch
        {
            "token_balances" => [("owner", TestItem.AddressB.ToString()), ("tokens", new[] { Weth.ToString() }), ("includeUsd", true)],
            "explain_transaction" => [("hash", deposit.Hash!.ToString()), ("includeUsd", true)],
            _ => []
        };

        CallToolResult call = await McpToolCalls.Call(client, tool, args);
        JsonElement result = McpAssert.Success(call);
        switch (tool)
        {
            case "fee_estimate":
                Assert.That(result.GetProperty("transferCost").TryGetProperty("valueUsd", out _), Is.False);
                Assert.That(result.GetProperty("usdNote").GetString(), Does.Contain("USD omitted because the price lookup deadline was reached."));
                break;
            case "token_balances":
                Assert.That(result.GetProperty("tokens").GetArrayLength(), Is.EqualTo(1));
                Assert.That(result.GetProperty("tokens")[0].GetProperty("symbol").GetString(), Is.EqualTo("WETH"));
                Assert.That(result.TryGetProperty("omitted", out _), Is.False);
                Assert.That(result.GetProperty("usdNotes").ToString(), Does.Contain("USD omitted because the price lookup deadline was reached."));
                break;
            default:
                Assert.That(result.GetProperty("tokenTransfers")[0].TryGetProperty("symbol", out JsonElement symbol) ? symbol.GetString() : null, Is.EqualTo("WETH"));
                Assert.That(result.GetProperty("notes").ToString(), Does.Contain("USD omitted because the price lookup deadline was reached."));
                break;
        }
        await McpToolCalls.AssertConformsToOutputSchema(client, tool, call);
    }

    [Test]
    public async Task Explanation_keeps_cached_symbols_with_no_metadata_budget_left()
    {
        await using McpTestNode node = await CreateNode(3000);
        Transaction deposit = await Deposit(node);
        await using McpClient client = await node.CreateClient();
        McpAssert.Success(await McpToolCalls.Call(client, "token_info", [("token", Weth.ToString())]));
        int metadataCalls = 0;
        ((PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, args) =>
        {
            if (method == nameof(IEthRpcModule.eth_getTransactionReceipt)) Thread.Sleep(1100);
            if (method == nameof(IEthRpcModule.eth_call) && args[0] is LegacyTransactionForRpc { To: { } to } && to == Weth) metadataCalls++;
        };

        CallToolResult call = await McpToolCalls.Call(client, "explain_transaction", [("hash", deposit.Hash!.ToString())]);

        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("tokenTransfers")[0].TryGetProperty("symbol", out JsonElement symbol) ? symbol.GetString() : null,
            Is.EqualTo("WETH"), "the receipt read exhausted the metadata budget, but the symbol is cached");
        Assert.That(metadataCalls, Is.Zero);
        Assert.That(result.GetProperty("notes").GetRawText(), Does.Not.Contain("Token metadata was not read"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "explain_transaction", call);
    }

    [Test]
    public async Task Explanation_usd_is_opt_in()
    {
        await using McpTestNode node = await CreateNode();
        Transaction deposit = await Deposit(node);
        int priceCalls = 0;
        ((PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, args) =>
        {
            if (IsPriceCall(method, args)) Interlocked.Increment(ref priceCalls);
        };
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "explain_transaction", [("hash", deposit.Hash!.ToString())]);

        Assert.That(McpAssert.Success(call).GetProperty("fees").GetProperty("total").TryGetProperty("valueUsd", out _), Is.False);
        Assert.That(priceCalls, Is.Zero);
        await McpToolCalls.AssertConformsToOutputSchema(client, "explain_transaction", call);
    }

    [Test]
    public async Task Optional_feed_internal_cancellation_does_not_cancel_the_tool()
    {
        await using McpTestNode node = await CreateNode();
        ((PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, args) =>
        {
            if (IsPriceCall(method, args)) throw new OperationCanceledException();
        };
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "fee_estimate", []);

        Assert.That(McpAssert.Success(call).GetProperty("usdNote").GetString(), Is.Not.Empty);
        await McpToolCalls.AssertConformsToOutputSchema(client, "fee_estimate", call);
    }

    [Test]
    public async Task Optional_feed_preserves_tool_cancellation_and_lease_ownership()
    {
        await using McpTestNode node = await CreateNode(maxConcurrent: 1);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        PriceRpcProvider provider = (PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeCall = (method, args) =>
        {
            if (IsPriceCall(method, args)) { started.Set(); release.Wait(TimeSpan.FromSeconds(8)); }
        };
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        McpPriceReader prices = node.Chain.Container.Resolve<McpPriceReader>();
        using CancellationTokenSource cancellation = new();
        Task work = executor.ExecuteAsync("price-slot", nameof(IEthRpcModule.eth_call), async (_, token) =>
        {
            await prices.ReadOptionalAsync(executor, "native", BlockParameter.Latest, TimeSpan.FromSeconds(3), token);
            return executor.Success(new { ok = true });
        }, cancellation.Token);
        try
        {
            Assert.That(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(3))), Is.True);
            await cancellation.CancelAsync();
            Assert.CatchAsync<OperationCanceledException>(async () => await work);
            Assert.That(SpinWait.SpinUntil(() => provider.ActiveEthLeases == 1, TimeSpan.FromSeconds(1)), Is.True,
                "the body releases its module while the detached worker retains its own");
            CallToolResult blocked = await executor.ExecuteAsync("blocked", nameof(IEthRpcModule.eth_call),
                (_, _) => Task.FromResult(executor.Success(new { ok = true })), CancellationToken.None);
            McpAssert.Error(blocked, "resource_exhausted");
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public async Task Optional_batch_deduplicates_feeds_and_uses_one_lease()
    {
        await using McpTestNode node = await CreateNode();
        PriceRpcProvider provider = (PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        int calls = 0;
        provider.BeforeCall = (method, args) =>
        {
            if (IsPriceCall(method, args)) Interlocked.Increment(ref calls);
        };
        int rented = provider.TotalEthLeases;
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        McpPriceReader prices = node.Chain.Container.Resolve<McpPriceReader>();
        CallToolResult call = await executor.ExecuteLocalAsync("price-batch", async token =>
        {
            Dictionary<string, McpPriceResult> result = await prices.ReadOptionalBatchAsync(executor,
                ["native", Weth.ToString(), "USDC"], BlockParameter.Latest, TimeSpan.FromMilliseconds(500), token);
            Assert.That(result["native"].Quote, Is.Not.Null);
            Assert.That(result[Weth.ToString()].Quote, Is.SameAs(result["native"].Quote));
            Assert.That(result["USDC"].Quote, Is.Null, "the second feed has no contract in this genesis");
            return executor.Success(new { ok = true });
        }, CancellationToken.None);

        McpAssert.Success(call);
        Assert.That(calls, Is.EqualTo(2), "one round read and one decimals read for the shared feed");
        Assert.That(provider.TotalEthLeases - rented, Is.EqualTo(1));
        Assert.That(provider.ActiveEthLeases, Is.Zero);
    }

    [Test]
    public async Task Optional_price_budget_includes_the_initial_module_rental()
    {
        await using McpTestNode node = await CreateNode(1000);
        PriceRpcProvider provider = (PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.BeforeRent = method =>
        {
            if (method == nameof(IEthRpcModule.eth_feeHistory)) Thread.Sleep(850);
        };
        provider.BeforeCall = (method, args) =>
        {
            if (IsPriceCall(method, args)) Thread.Sleep(1500);
        };
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "fee_estimate", []);

        Assert.That(McpAssert.Success(call).GetProperty("usdNote").GetString(), Does.Contain("USD omitted because the price lookup deadline was reached."));
        await McpToolCalls.AssertConformsToOutputSchema(client, "fee_estimate", call);
    }

    [TestCase(1000, 600, 0)]
    [TestCase(5000, 3900, 600)]
    public void Optional_price_budget_reserves_margin_and_skips_short_windows(int timeout, int elapsed, int maximum)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Thread.Sleep(elapsed);
        TimeSpan budget = McpPriceReader.OptionalBudget(timeout, clock);
        Assert.That(budget.TotalMilliseconds, Is.LessThanOrEqualTo(maximum));
        if (maximum > 0) Assert.That(budget, Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    public async Task Historical_price_batch_reads_its_header_once()
    {
        await using McpTestNode node = await CreateNode();
        int headers = 0;
        ((PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, _) =>
        {
            if (method is nameof(IEthRpcModule.eth_getHeaderByNumber) or nameof(IEthRpcModule.eth_getHeaderByHash)) headers++;
        };
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        McpPriceReader prices = node.Chain.Container.Resolve<McpPriceReader>();
        McpAssert.Success(await executor.ExecuteLocalAsync("historical-prices", async token =>
        {
            Dictionary<string, McpPriceResult> batch = await prices.ReadOptionalBatchAsync(executor,
                ["native", "USDC", Weth.ToString()], new BlockParameter(0UL), TimeSpan.FromSeconds(1), token);
            Assert.That(batch.Count, Is.EqualTo(3));
            return executor.Success(new { ok = true });
        }, CancellationToken.None));
        Assert.That(headers, Is.EqualTo(1), "all distinct feeds must share the same historical header");
    }

    [Test]
    public async Task Latest_balances_price_the_same_pinned_block()
    {
        await using McpTestNode node = await CreateNode();
        PriceRpcProvider provider = (PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        Hash256? balanceHash = null;
        Hash256? priceHash = null;
        provider.BeforeCall = (method, args) =>
        {
            if (method == nameof(IEthRpcModule.eth_getBalance) && args[1] is BlockParameter { Type: BlockParameterType.BlockHash } balance)
                balanceHash = balance.BlockHash;
            if (IsPriceCall(method, args) && args[1] is BlockParameter { Type: BlockParameterType.BlockHash } price)
                priceHash = price.BlockHash;
        };
        await using McpClient client = await node.CreateClient();

        McpAssert.Success(await McpToolCalls.Call(client, "token_balances",
            [("owner", TestItem.AddressB.ToString()), ("tokens", Array.Empty<string>()), ("includeUsd", true)]));

        Assert.That(priceHash, Is.EqualTo(balanceHash).And.Not.Null);
    }

    [Test]
    public async Task Optional_price_clock_excludes_waiting_for_a_tool_slot()
    {
        await using McpTestNode node = await CreateNode(1000, maxConcurrent: 1);
        await using McpClient client = await node.CreateClient();
        ((PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeCall = (method, args) =>
        {
            if (IsPriceCall(method, args)) Thread.Sleep(80);
        };
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CallToolResult> holder = executor.ExecuteAsync("holder", nameof(IEthRpcModule.eth_getBalance), async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return executor.Success(new { ok = true });
        }, CancellationToken.None);
        await entered.Task;
        Task<CallToolResult> estimate = McpToolCalls.Call(client, "fee_estimate", []);
        try
        {
            await Task.Delay(850);
            release.TrySetResult();
            McpAssert.Success(await holder);
            CallToolResult call = await estimate;
            Assert.That(McpAssert.Success(call).GetProperty("transferCost").TryGetProperty("valueUsd", out _), Is.True,
                "a queued call still has its full body budget when the slot is granted");
            await McpToolCalls.AssertConformsToOutputSchema(client, "fee_estimate", call);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task Optional_price_rent_failure_keeps_the_completed_fees()
    {
        await using McpTestNode node = await CreateNode();
        ((PriceRpcProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).BeforeRent = method =>
        {
            if (method == nameof(IEthRpcModule.eth_call)) throw new InvalidOperationException("Pool unavailable");
        };
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "fee_estimate", []);

        Assert.That(McpAssert.Success(call).GetProperty("usdNote").GetString(), Does.Contain("unavailable"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "fee_estimate", call);
    }

    private static bool IsPriceCall(string method, object?[] args) =>
        method == nameof(IEthRpcModule.eth_call) && args[0] is LegacyTransactionForRpc { To: { } to } && to == NativeFeed;

    private static Task<McpTestNode> CreateNode(int timeout = 5000, int? maxConcurrent = null) => McpTestNode.Create(
        configure: config => { config.ToolTimeout = timeout; if (maxConcurrent is { } slots) config.MaxConcurrentToolCalls = slots; },
        configureContainer: builder => builder.AddScoped<IGenesisPostProcessor, McpTxTokensTests.FeedGenesis>()
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new PriceRpcProvider(inner)));

    internal static async Task<Transaction> Deposit(McpTestNode node)
    {
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction tx = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(1)
            .WithTo(Weth).WithData(McpAbiSignature.Parse("deposit()").Selector).WithGasLimit(100_000)
            .SignedAndResolved(node.Chain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject;
        await node.Chain.AddBlock(tx);
        return tx;
    }

    internal sealed class PriceRpcProvider(IRpcModuleProvider inner) : IRpcModuleProvider
    {
        private int _active;
        private int _rented;
        public int ActiveEthLeases => Volatile.Read(ref _active);
        public int TotalEthLeases => Volatile.Read(ref _rented);
        public Action<string, object?[]>? BeforeCall { get; set; }
        public Action<string>? BeforeRent { get; set; }
        public IJsonSerializer Serializer => inner.Serializer;
        public IReadOnlyCollection<string> Enabled => inner.Enabled;
        public IReadOnlyCollection<string> All => inner.All;
        public void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule => inner.Register(pool);
        public ModuleResolution Check(string methodName, JsonRpcContext context, out string? module, out ResolvedMethodInfo? method) => inner.Check(methodName, context, out module, out method);
        public ResolvedMethodInfo? Resolve(string methodName) => inner.Resolve(methodName);
        public async ValueTask<IRpcModule> Rent(string methodName, bool canBeShared)
        {
            BeforeRent?.Invoke(methodName);
            return Wrap(await inner.Rent(methodName, canBeShared));
        }
        public async ValueTask<IRpcModule> Rent(ResolvedMethodInfo method)
        {
            BeforeRent?.Invoke(method.MethodInfo.Name);
            return Wrap(await inner.Rent(method));
        }
        private IRpcModule Wrap(IRpcModule module)
        {
            if (module is not IEthRpcModule eth) return module;
            IEthRpcModule proxy = DispatchProxy.Create<IEthRpcModule, PriceEthProxy>();
            ((PriceEthProxy)proxy).Inner = eth;
            ((PriceEthProxy)proxy).BeforeCall = (method, args) => BeforeCall?.Invoke(method, args);
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _rented);
            return proxy;
        }
        private IRpcModule Unwrap(IRpcModule module)
        {
            if (module is not PriceEthProxy proxy) return module;
            Interlocked.Decrement(ref _active);
            return proxy.Inner;
        }
        public void Return(string methodName, IRpcModule module) => inner.Return(methodName, Unwrap(module));
        public void Return(ResolvedMethodInfo method, IRpcModule module) => inner.Return(method, Unwrap(module));
    }

    public class PriceEthProxy : DispatchProxy
    {
        internal IEthRpcModule Inner { get; set; } = null!;
        internal Action<string, object?[]>? BeforeCall { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            BeforeCall?.Invoke(targetMethod!.Name, args ?? []);
            try { return targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is { } cause)
            {
                ExceptionDispatchInfo.Capture(cause).Throw();
                throw;
            }
        }
    }
}
