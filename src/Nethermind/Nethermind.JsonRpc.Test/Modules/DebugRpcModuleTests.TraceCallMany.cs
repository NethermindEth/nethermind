// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
            Value = value ?? 1.Ether(),
            Gas = gas
        };

    private static async Task<Context> CreateContext()
    {
        Context ctx = await Context.Create();;
        try
        {
            await ctx.Blockchain.AddFunds(TestItem.AddressD, 100.Ether());
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

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        result.Data.First().First().Should().NotBeNull();
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_bundles()
    {
        using Context ctx = await CreateContext();

        var result = ctx.DebugRpcModule.debug_traceCallMany([CreateBundle(CreateTransaction()), CreateBundle(CreateTransaction(to: TestItem.AddressD))], BlockParameter.Latest);

        result.Data.Select(r => r.Count()).Should().BeEquivalentTo([1, 1]);
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_transactions_per_bundle()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(), CreateTransaction(to: TestItem.AddressD));

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        result.Data.Select(r => r.Count()).Should().BeEquivalentTo([2]);
    }

    [Test]
    public async Task Debug_traceCallMany_fails_when_not_enough_balance()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(value: 200.Ether()));

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        result.Data.Select(r => r.Count()).Should().BeEquivalentTo([1]);

        GethLikeTxTrace trace = result.Data.First().First();
        trace.Gas.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Debug_traceCallMany_respects_gas_cap()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        result.Data.Should().HaveCount(1);
    }

    [Test]
    public async Task Debug_traceCallMany_with_trace_options()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: long.MaxValue));

        GethTraceOptions options = new() { DisableStorage = true, DisableStack = true };

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest, options);

        result.Data.Select(r => r.Count()).Should().BeEquivalentTo([1]);
    }

    [Test]
    public async Task Debug_traceCallMany_with_state_override()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction());
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [TestItem.AddressD] = new() { Balance = 100.Ether() }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        result.Data.Select(r => r.Count()).Should().BeEquivalentTo([1]);
        result.Data.First().First().Should().NotBeNull();
    }

    [Test]
    public async Task Debug_traceCallMany_with_combined_overrides()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction());

        bundle.BlockOverride = new BlockOverride { GasLimit = 50000000 };
        bundle.StateOverrides = new Dictionary<Address, AccountOverride>
        {
            [TestItem.AddressD] = new() { Balance = 100.Ether() }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);

        result.Data.Select(r => r.Count()).Should().BeEquivalentTo([1]);
        result.Data.First().First().Should().NotBeNull();
    }

    [Test]
    public async Task Debug_traceCallMany_block_override_gaslimit_applies()
    {
        using Context ctx = await CreateContext();
        TransactionBundle bundle = CreateBundle(CreateTransaction(gas: 25_000_000));
        bundle.BlockOverride = new BlockOverride { GasLimit = 50_000_000 };

        var result = ctx.DebugRpcModule.debug_traceCallMany([bundle], BlockParameter.Latest);
        result.Data.First().First().Failed.Should().BeFalse();
    }

    [Test]
    public async Task Debug_traceCallMany_mixed_bundles_preserves_order()
    {
        using Context ctx = await CreateContext();
        TransactionBundle simple = CreateBundle(CreateTransaction(gas: 25_000_000));
        TransactionBundle withOverride = CreateBundle(CreateTransaction(gas: 25_000_000));
        withOverride.BlockOverride = new BlockOverride { GasLimit = 30_000_000 };
        var result = ctx.DebugRpcModule.debug_traceCallMany([simple, withOverride], BlockParameter.Latest);

        result.Data.Select(r => r.Count()).Should().BeEquivalentTo([1, 1]);
    }
}
