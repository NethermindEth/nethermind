// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Test.Modules.Eth;
using Nethermind.State;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[TestFixture]
public partial class DebugRpcModuleTests
{
    private static TransactionBundle CreateBundle(params TransactionForRpc[] transactions) => new() { Transactions = transactions };

    private static TransactionBundle CreateGasProbeBundle(ulong? gas = null) => new()
    {
        Transactions = [new LegacyTransactionForRpc { To = EthRpcSimulateTestsBase.GasProbeContractAddress, Gas = gas }],
        StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [EthRpcSimulateTestsBase.GasProbeContractAddress] = new()
            {
                Code = Bytes.FromHexString("0x5a60005260206000f3")
            }
        }
    };

    private static IEnumerable<TestCaseData> DebugTraceCallManyMissingGasCases()
    {
        yield return new TestCaseData((ulong?)null, (ulong?)null, false).SetName("omitted_gas_defaults_to_gas_cap_not_block_gas_limit");
        yield return new TestCaseData((ulong?)0UL, (ulong?)null, false).SetName("zero_gas_defaults_to_gas_cap_not_block_gas_limit");
        yield return new TestCaseData((ulong?)null, (ulong?)0UL, true).SetName("omitted_gas_with_zero_gas_cap_uncapped");
        yield return new TestCaseData((ulong?)0UL, (ulong?)0UL, true).SetName("zero_gas_with_zero_gas_cap_uncapped");
    }

    private static LegacyTransactionForRpc CreateTransaction(
        Address? from = null,
        Address? to = null,
        UInt256? value = null,
        ulong gas = GasCostOf.Transaction) =>
        new()
        {
            From = from ?? TestItem.AddressD,
            To = to ?? TestItem.AddressC,
            Value = value ?? 1.Ether,
            Gas = gas
        };

    private static async Task<Context> CreateContext()
    {
        Context ctx = await Context.Create();
        try
        {
            await ctx.Blockchain.AddFunds(TestItem.AddressD, 100.Ether);
        }
        catch
        {
            ctx.Dispose();
            throw;
        }

        return ctx;
    }

    [Test]
    public async Task Debug_traceCallMany_with_single_bundle_single_transaction()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction());

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That((JArray)result[0], Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_bundles()
    {
        using Context ctx = await CreateContext();

        JArray result = await RunTraceCallManyAsJson(ctx, [CreateBundle(CreateTransaction()), CreateBundle(CreateTransaction(to: TestItem.AddressD))]);

        Assert.That(result.Select(r => ((JArray)r).Count), Is.EqualTo([1, 1]));
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_transactions_per_bundle()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(), CreateTransaction(to: TestItem.AddressD));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That(result.Select(r => ((JArray)r).Count), Is.EqualTo([2]));
    }

    [Test]
    public async Task Debug_traceCallMany_fails_when_not_enough_balance()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(value: 200.Ether));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That(result.Select(r => ((JArray)r).Count), Is.EqualTo([1]));

        JToken trace = result[0][0]!;
        Assert.That((bool)trace["failed"]!, Is.True, "insufficient balance must surface as failed:true");
        Assert.That((long)trace["gas"]!, Is.GreaterThan(0), "failed trace gas reflects the tx gas limit");
        Assert.That((string)trace["error"]!, Does.Contain("insufficient funds"), "Nethermind wording is translated to Geth's wording for compat");
        Assert.That((int)trace["errorCode"]!, Is.EqualTo(ErrorCodes.InvalidInput), "tracing-failure errorCode mirrors the buffered ErrorCodes.InvalidInput");
    }

    [Test]
    public async Task Debug_traceCallMany_respects_gas_cap()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Debug_traceCallMany_with_trace_options()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        GethTraceOptions options = new() { DisableStorage = true, DisableStack = true };

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle], options);

        Assert.That(result.Select(r => ((JArray)r).Count), Is.EqualTo([1]));
    }

    [Test]
    public async Task Debug_traceCallMany_to_async_stream()
    {
        using Context ctx = await CreateContext();
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().EnableTracingStreamMode = true;

        // Multiple bundles so FlushBetweenBundles runs more than once.
        TransactionBundle[] bundles = [CreateBundle(CreateTransaction()), CreateBundle(CreateTransaction(to: TestItem.AddressD))];
        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany(bundles, BlockParameter.Latest);
        Assert.That(result.Data, Is.AssignableTo<IStreamableResult>());
        IStreamableResult streaming = (IStreamableResult)result.Data;

        await using AsyncCompletingStream stream = new();
        PipeWriter writer = PipeWriter.Create(stream);

        Assert.DoesNotThrowAsync(async () => await streaming.WriteToAsync(writer, CancellationToken.None));

        await writer.CompleteAsync();
    }

    private static async Task<JArray> RunTraceCallManyAsJson(Context ctx, TransactionBundle[] bundles, GethTraceOptions? options = null)
    {
        string response = options is null
            ? await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", bundles, "latest")
            : await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCallMany", bundles, "latest", options);
        return (JArray)JToken.Parse(response)["result"]!;
    }

    [Test]
    public async Task Debug_traceCallMany_with_state_override()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction());
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [TestItem.AddressD] = new() { Balance = 100.Ether }
        };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1]));
        Assert.That(result.Data.First().First(), Is.Not.Null);
    }

    [Test]
    public async Task Debug_traceCallMany_with_combined_overrides()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction());

        bundle.BlockOverride = new BlockOverride { GasLimit = 50000000 };
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [TestItem.AddressD] = new() { Balance = 100.Ether }
        };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1]));
        Assert.That(result.Data.First().First(), Is.Not.Null);
    }

    [Test]
    public async Task Debug_traceCallMany_with_invalid_simulation_override_returns_original_error_message()
    {
        using Context ctx = await CreateContext();
        Address ecrecoverAddress = new("0x0000000000000000000000000000000000000001");
        TransactionBundle bundle = CreateBundle(CreateTransaction());
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [ecrecoverAddress] = new() { MovePrecompileToAddress = ecrecoverAddress }
        };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result =
            ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.MovePrecompileSelfReference));
            Assert.That(result.Result.Error, Is.EqualTo("MovePrecompileToAddress referenced itself in replacement"));
        }
    }

    [Test]
    public async Task Debug_traceCallMany_block_override_gaslimit_applies()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: 25_000_000));
        bundle.BlockOverride = new BlockOverride { GasLimit = 50_000_000 };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        Assert.That(result.Data.First().First().Failed, Is.False);
    }

    [Test]
    public async Task Debug_traceCallMany_mixed_bundles_preserves_order()
    {
        using Context ctx = await CreateContext();
        TransactionBundle simple = CreateBundle(CreateTransaction(gas: 4_000_000));
        TransactionBundle withOverride = CreateBundle(CreateTransaction(gas: 25_000_000));
        withOverride.BlockOverride = new BlockOverride { GasLimit = 30_000_000 };
        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany([simple, withOverride], BlockParameter.Latest);

        Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1, 1]));
    }

    [TestCase(3, TestName = "Debug_traceCallMany_with_minimum_block_number_gap_returns_one_entry_per_bundle")]
    [TestCase(5, TestName = "Debug_traceCallMany_with_block_number_gap_returns_one_entry_per_bundle")]
    public async Task Debug_traceCallMany_with_block_number_gap_returns_one_entry_per_bundle(int secondBundleOffset)
    {
        using Context ctx = await CreateContext();
        ulong headNumber = ctx.Blockchain.BlockTree.Head!.Number;

        TransactionBundle first = CreateBundle(CreateTransaction());
        first.BlockOverride = new BlockOverride { Number = headNumber + 1 };

        TransactionBundle second = CreateBundle(CreateTransaction(to: TestItem.AddressD));
        second.BlockOverride = new BlockOverride { Number = headNumber + (ulong)secondBundleOffset };

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result =
            ctx.DebugRpcModule.debug_traceCallMany([first, second], BlockParameter.Latest);

        Assert.That(System.Linq.Enumerable.Count(result.Data), Is.EqualTo(2));
        Assert.That(result.Data.Select(r => r.Count()), Is.EqualTo([1, 1]));
    }

    [Test]
    public async Task Debug_traceCallMany_caps_gas_to_gas_cap()
    {
        using Context ctx = await CreateContext();
        ulong gasCap = 50_000;
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        // Deploy contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
        // Returns gas available at start of execution as a 32-byte uint256
        byte[] runtimeCode = Bytes.FromHexString("5a60005260206000f3");
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;

        ulong nonce = ctx.Blockchain.StateReader.GetNonce(ctx.Blockchain.BlockTree.Head!.Header, TestItem.AddressD);
        Address contractAddress = ContractAddress.From(TestItem.AddressD, nonce);

        Transaction deployTx = Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyD)
            .WithNonce(nonce)
            .WithCode(initCode)
            .WithGasLimit(100_000)
            .TestObject;
        await ctx.Blockchain.AddBlock(deployTx);

        TransactionBundle bundle = CreateBundle(CreateTransaction(to: contractAddress, value: 0, gas: 100_000));

        JArray result = await RunTraceCallManyAsJson(ctx, [bundle]);

        byte[] returnValue = Bytes.FromHexString((string)result[0][0]!["returnValue"]!);
        ulong gasAvailable = (ulong)returnValue.ToUInt256();
        Assert.That(gasAvailable, Is.LessThan(gasCap));
        Assert.That(gasAvailable, Is.GreaterThan(0UL));
    }

    [TestCaseSource(nameof(DebugTraceCallManyMissingGasCases))]
    public async Task Debug_traceCallMany_missing_or_zero_gas_respects_gas_cap(ulong? requestGas, ulong? configuredGasCap, bool uncapped)
    {
        using Context ctx = await CreateContext();

        ulong blockGasLimit = ctx.Blockchain.BlockTree.Head!.Header.GasLimit;
        ulong gasCap = configuredGasCap ?? blockGasLimit * 10;
        ctx.Blockchain.Container.Resolve<IJsonRpcConfig>().GasCap = gasCap;

        ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> result = ctx.DebugRpcModule.debug_traceCallMany(
            [CreateGasProbeBundle(requestGas)],
            BlockParameter.Latest);

        GethLikeTxTrace trace = result.Data.First().First();
        UInt256 gasAvailable = trace.ReturnValue.ToUInt256();
        if (uncapped)
        {
            Assert.That(trace.Failed, Is.False, "GasCap=0 should leave simulate execution uncapped rather than forcing zero gas");
            Assert.That(gasAvailable, Is.GreaterThan(UInt256.Zero));
        }
        else
        {
            Assert.That(gasAvailable, Is.GreaterThan((UInt256)blockGasLimit), $"gas available should reflect gasCap ({gasCap}), not block gas limit ({blockGasLimit})");
        }
    }
}
