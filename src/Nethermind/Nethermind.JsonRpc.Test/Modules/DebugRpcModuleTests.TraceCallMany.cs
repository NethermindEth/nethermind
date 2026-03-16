// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[TestFixture]
public partial class DebugRpcModuleTests
{
    private static TransactionBundle CreateBundle(params TransactionForRpc[] transactions) => new() { Transactions = transactions };

    private static LegacyTransactionForRpc CreateTransaction(
        Address? from = null,
        Address? to = null,
        UInt256? value = null,
        long gas = GasCostOf.Transaction) =>
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

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        (await (await result.Data.FirstAsync()).FirstAsync()).Should().NotBeNull();
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_bundles()
    {
        using Context ctx = await CreateContext();

        var result = await ctx.DebugRpcModule.debug_traceCallMany([CreateBundle(CreateTransaction()), CreateBundle(CreateTransaction(to: TestItem.AddressD))], BlockParameter.Latest);

        List<int> l = [];
        await foreach (IAsyncEnumerable<GethLikeTxTrace> bundle in result.Data)
        {
            l.Add(await bundle.CountAsync());
        }
        l.Should().BeEquivalentTo([1, 1]);
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_transactions_per_bundle()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(), CreateTransaction(to: TestItem.AddressD));

        ResultWrapper<IAsyncEnumerable<IAsyncEnumerable<GethLikeTxTrace>>> result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        List<int> l = [];
        await foreach (ValueTask<int> vt in result.Data.Select(r => r.CountAsync()))
        {
            l.Add(await vt);
        }

        l.Should().BeEquivalentTo([2]);
    }

    [Test]
    public async Task Debug_traceCallMany_fails_when_not_enough_balance()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(value: 200.Ether));

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        List<int> l = [];
        await foreach (IAsyncEnumerable<GethLikeTxTrace> b in result.Data)
        {
            l.Add(await b.CountAsync());
        }
        l.Should().BeEquivalentTo([1]);

        GethLikeTxTrace trace = await (await result.Data.FirstAsync()).FirstAsync();
        trace.Should().NotBeNull();
        trace.Gas.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Debug_traceCallMany_respects_gas_cap()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        (await result.Data.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task Debug_traceCallMany_with_trace_options()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        GethTraceOptions options = new() { DisableStorage = true, DisableStack = true };

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest, options);

        List<int> l = [];
        await foreach (IAsyncEnumerable<GethLikeTxTrace> b in result.Data)
        {
            l.Add(await b.CountAsync());
        }
        l.Should().BeEquivalentTo([1]);
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

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        List<int> l = [];
        await foreach (IAsyncEnumerable<GethLikeTxTrace> b in result.Data)
        {
            l.Add(await b.CountAsync());
        }
        l.Should().BeEquivalentTo([1]);
        (await (await result.Data.FirstAsync()).FirstAsync()).Should().NotBeNull();
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

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        List<int> l = [];
        await foreach (IAsyncEnumerable<GethLikeTxTrace> b in result.Data)
        {
            l.Add(await b.CountAsync());
        }
        l.Should().BeEquivalentTo([1]);
        (await (await result.Data.FirstAsync()).FirstAsync()).Should().NotBeNull();
    }

    [Test]
    public async Task Debug_traceCallMany_block_override_gaslimit_applies()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: 25_000_000));
        bundle.BlockOverride = new BlockOverride { GasLimit = 50_000_000 };

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        (await (await result.Data.FirstAsync()).FirstAsync()).Failed.Should().BeFalse();
    }

    [Test]
    public async Task Debug_traceCallMany_mixed_bundles_preserves_order()
    {
        using Context ctx = await CreateContext();
        TransactionBundle simple = CreateBundle(CreateTransaction(gas: 4_000_000));
        TransactionBundle withOverride = CreateBundle(CreateTransaction(gas: 25_000_000));
        withOverride.BlockOverride = new BlockOverride { GasLimit = 30_000_000 };
        var result = await ctx.DebugRpcModule.debug_traceCallMany([simple, withOverride], BlockParameter.Latest);

        List<int> l = [];
        await foreach (IAsyncEnumerable<GethLikeTxTrace> bundle in result.Data)
        {
            l.Add(await bundle.CountAsync());
        }
        l.Should().BeEquivalentTo([1, 1]);
    }

    [Test]
    public async Task Debug_traceCallMany_caps_gas_to_gas_cap()
    {
        using Context ctx = await CreateContext();
        long gasCap = 50_000;
        IJsonRpcConfig config = ctx.Blockchain.Container.Resolve<IJsonRpcConfig>();
        config.GasCap = gasCap;

        // Deploy contract: GAS PUSH1 0 MSTORE PUSH1 32 PUSH1 0 RETURN
        // Returns gas available at start of execution as a 32-byte uint256
        byte[] runtimeCode = Bytes.FromHexString("5a60005260206000f3");
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;

        UInt256 nonce = ctx.Blockchain.StateReader.GetNonce(ctx.Blockchain.BlockTree.Head!.Header, TestItem.AddressD);
        Address contractAddress = ContractAddress.From(TestItem.AddressD, nonce);

        Transaction deployTx = Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyD)
            .WithNonce(nonce)
            .WithCode(initCode)
            .WithGasLimit(100_000)
            .TestObject;
        await ctx.Blockchain.AddBlock(deployTx);

        TransactionBundle bundle = CreateBundle(CreateTransaction(to: contractAddress, value: 0, gas: 100_000));

        var result = await ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        GethLikeTxTrace trace = await (await result.Data.FirstAsync()).FirstAsync();
        long gasAvailable = (long)trace.ReturnValue.ToUInt256();
        gasAvailable.Should().BeLessThan(gasCap);
        gasAvailable.Should().BeGreaterThan(0);
    }
}
