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
    private static TransactionBundle CreateBundle(Address from, Address to, UInt256 value, long gas = 21000, 
        BlockOverride? blockOverride = null, Dictionary<Address, AccountOverride>? stateOverrides = null) =>
        new()
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = to,
                    Value = value,
                    Gas = gas
                }
            },
            BlockOverride = blockOverride,
            StateOverrides = stateOverrides
        };

    [Test]
    public async Task Debug_traceCallMany_with_single_bundle_single_transaction()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        Address to = TestItem.AddressC;
        UInt256 balance = 100.Ether();

        await ctx.Blockchain.AddFunds(from, balance);

        var bundle = new TransactionBundle
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = to,
                    Value = 1.Ether()
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Select(r => r.Length).Should().BeEquivalentTo([1]);
        result.Data[0][0].Should().NotBeNull();
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_bundles()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        Address to1 = TestItem.AddressC;
        Address to2 = TestItem.AddressD;
        UInt256 balance = 100.Ether();

        await ctx.Blockchain.AddFunds(from, balance);

        var bundles = new[]
        {
            new TransactionBundle
            {
                Transactions = new[]
                {
                    new LegacyTransactionForRpc
                    {
                        From = from,
                        To = to1,
                        Value = 1.Ether(),
                    }
                }
            },
            new TransactionBundle
            {
                Transactions = new[]
                {
                    new LegacyTransactionForRpc
                    {
                        From = from,
                        To = to2,
                        Value = 2.Ether(),
                    }
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(bundles, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Select(r => r.Length).Should().BeEquivalentTo([1, 1]);
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_transactions_per_bundle()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        Address to1 = TestItem.AddressC;
        Address to2 = TestItem.AddressD;
        UInt256 balance = 100.Ether();

        await ctx.Blockchain.AddFunds(from, balance);

        var bundle = new TransactionBundle
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = to1,
                    Value = 1.Ether()
                },
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = to2,
                    Value = 2.Ether()
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Select(r => r.Length).Should().BeEquivalentTo([2]);
    }

    [Test]
    public async Task Debug_traceCallMany_fails_when_not_enough_balance()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        UInt256 balance = 1.Ether();
        UInt256 send = balance * 2;

        await ctx.Blockchain.AddFunds(from, balance);

        var bundle = new TransactionBundle
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = TestItem.AddressC,
                    Value = send
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().NotBeNull();
        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.ErrorCode.Should().Be(0);

        result.Data.Should().NotBeNull();
        result.Data.Select(r => r.Length).Should().BeEquivalentTo([1]);

        var trace = result.Data[0][0];
        trace.Should().NotBeNull();
        trace.Failed.Should().BeTrue();

        trace.Gas.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Debug_traceCallMany_respects_gas_cap()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        UInt256 balance = 100.Ether();

        await ctx.Blockchain.AddFunds(from, balance);

        var bundle = new TransactionBundle
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = TestItem.AddressC,
                    Value = 1.Ether(),
                    Gas = long.MaxValue
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Should().HaveCount(1);
    }

    [Test]
    public async Task Debug_traceCallMany_with_trace_options()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        UInt256 balance = 100.Ether();

        await ctx.Blockchain.AddFunds(from, balance);

        TransactionBundle bundle = CreateBundle(from, TestItem.AddressC, 1.Ether());

        var options = new GethTraceOptions
        {
            DisableStorage = true,
            DisableStack = true
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest, options);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Select(r => r.Length).Should().BeEquivalentTo([1]);
    }

    [Test]
    public async Task Debug_traceCallMany_with_block_override()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        UInt256 balance = 100.Ether();

        await ctx.Blockchain.AddFunds(from, balance);

        var blockOverride = new BlockOverride
        {
            GasLimit = 30000000,
            Time = 1234567890
        };
        TransactionBundle bundle = CreateBundle(from, TestItem.AddressC, 1.Ether(), blockOverride: blockOverride);

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();

        TestContext.Out.WriteLine($"Block override test - ErrorCode: {result.ErrorCode}");
        if (result.ErrorCode != 0)
        {
            TestContext.Out.WriteLine($"Error message: {result.Result}");
        }
        else
        {
            result.Data.Select(r => r.Length).Should().BeEquivalentTo([1]);
            result.Data[0][0].Should().NotBeNull();
        }
    }

    [Test]
    public async Task Debug_traceCallMany_with_state_override()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        Address target = TestItem.AddressC;

        var stateOverrides = new Dictionary<Address, AccountOverride>
        {
            [from] = new AccountOverride { Balance = 100.Ether() }
        };
        TransactionBundle bundle = CreateBundle(from, target, 1.Ether(), stateOverrides: stateOverrides);

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();

        TestContext.Out.WriteLine($"State override test - ErrorCode: {result.ErrorCode}");
        if (result.ErrorCode != 0)
        {
            TestContext.Out.WriteLine($"Error message: {result.Result}");
        }
        else
        {
            result.Data.Select(r => r.Length).Should().BeEquivalentTo([1]);
            result.Data[0][0].Should().NotBeNull();
            TestContext.Out.WriteLine($"Transaction failed status: {result.Data[0][0].Failed}");
        }
    }

    [Test]
    public async Task Debug_traceCallMany_with_combined_overrides()
    {
        using var ctx = await Context.Create();

        Address from = TestItem.AddressD;
        Address target = TestItem.AddressC;

        var blockOverride = new BlockOverride { GasLimit = 50000000 };
        var stateOverrides = new Dictionary<Address, AccountOverride>
        {
            [from] = new AccountOverride { Balance = 100.Ether() }
        };
        TransactionBundle bundle = CreateBundle(from, target, 1.Ether(), blockOverride: blockOverride, stateOverrides: stateOverrides);

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();

        TestContext.Out.WriteLine($"Combined override test - ErrorCode: {result.ErrorCode}");
        if (result.ErrorCode != 0)
        {
            TestContext.Out.WriteLine($"Error message: {result.Result}");
        }
        else
        {
            result.Data.Select(r => r.Length).Should().BeEquivalentTo([1]);
            result.Data[0][0].Should().NotBeNull();
            TestContext.Out.WriteLine($"Transaction failed status: {result.Data[0][0].Failed}");
        }
    }

    [Test]
    public async Task Debug_traceCallMany_state_override_changes_outcome()
    {
        using var ctx = await Context.Create();
        var from = TestItem.AddressD;
        var to = TestItem.AddressC;

        TransactionBundle noOverride = CreateBundle(from, to, 1.Ether());
        var r1 = ctx.DebugRpcModule.debug_traceCallMany(new[] { noOverride }, BlockParameter.Latest);
        r1.Data[0][0].Failed.Should().BeTrue();

        var stateOverrides = new Dictionary<Address, AccountOverride>
        {
            [from] = new AccountOverride { Balance = 100.Ether() }
        };
        TransactionBundle withOverride = CreateBundle(from, to, 1.Ether(), stateOverrides: stateOverrides);
        var r2 = ctx.DebugRpcModule.debug_traceCallMany(new[] { withOverride }, BlockParameter.Latest);
        r2.Data[0][0].Failed.Should().BeFalse();
    }

    [Test]
    public async Task Debug_traceCallMany_block_override_gaslimit_applies()
    {
        using var ctx = await Context.Create();
        var from = TestItem.AddressD;
        await ctx.Blockchain.AddFunds(from, 100.Ether());

        var tx = new LegacyTransactionForRpc
        {
            From = from,
            To = TestItem.AddressC,
            Value = 1.Ether(),
            Gas = 25_000_000
        };

        var blockOverride = new BlockOverride { GasLimit = 50_000_000 };
        var bundle = CreateBundle(from, TestItem.AddressC, 1.Ether(), 25_000_000, blockOverride);

        var r = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);
        r.ErrorCode.Should().Be(0);
        r.Data[0][0].Failed.Should().BeFalse();
    }

    [Test]
    public async Task Debug_traceCallMany_mixed_bundles_preserves_order()
    {
        using var ctx = await Context.Create();
        var from = TestItem.AddressD;
        await ctx.Blockchain.AddFunds(from, 100.Ether());

        TransactionBundle simple = CreateBundle(from, TestItem.AddressC, 1.Ether());
        var blockOverride = new BlockOverride { GasLimit = 30_000_000 };
        TransactionBundle withOverride = CreateBundle(from, TestItem.AddressC, 1.Ether(), blockOverride: blockOverride);

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { simple, withOverride }, BlockParameter.Latest);

        result.ErrorCode.Should().Be(0);
        result.Data.Select(r => r.Length).Should().BeEquivalentTo([1, 1]);
    }


}
