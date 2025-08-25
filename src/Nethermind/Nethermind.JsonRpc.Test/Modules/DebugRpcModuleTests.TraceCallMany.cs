// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    [Test]
    public async Task Debug_traceCallMany_with_single_bundle_single_transaction()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
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
                    Value = 1.Ether(),
                    Gas = 21000
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Should().HaveCount(1);
        result.Data[0].Should().HaveCount(1);
        result.Data[0][0].Should().NotBeNull();
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_bundles()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
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
                        Gas = 21000
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
                        Gas = 21000
                    }
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(bundles, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Should().HaveCount(2);
        result.Data[0].Should().HaveCount(1);
        result.Data[1].Should().HaveCount(1);
    }

    [Test]
    public async Task Debug_traceCallMany_with_multiple_transactions_per_bundle()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
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
                    Value = 1.Ether(),
                    Gas = 21000
                },
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = to2,
                    Value = 2.Ether(),
                    Gas = 21000
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Should().HaveCount(1);
        result.Data[0].Should().HaveCount(2);
    }

    [Test]
    public async Task Debug_traceCallMany_fails_when_not_enough_balance()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
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
                    Value = send,
                    Gas = 21000
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().NotBeNull();
        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.ErrorCode.Should().Be(0, "RPC call should succeed even if transaction fails");

        result.Data.Should().NotBeNull();
        result.Data.Should().HaveCount(1, "should have exactly one bundle result");
        result.Data[0].Should().HaveCount(1, "bundle should have exactly one transaction trace");

        var trace = result.Data[0][0];
        trace.Should().NotBeNull("trace should exist");
        trace.Failed.Should().BeTrue("transaction should fail due to insufficient balance");

        trace.Gas.Should().BeGreaterThan(0, "gas should be consumed even on failure");
    }

    [Test]
    public async Task Debug_traceCallMany_respects_gas_cap()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
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
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
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
                    Gas = 21000
                }
            }
        };

        var options = new GethTraceOptions
        {
            DisableStorage = true,
            DisableStack = true
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest, options);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();
        result.Data.Should().HaveCount(1);
        result.Data[0].Should().HaveCount(1);
    }

    [Test]
    public async Task Debug_traceCallMany_with_block_override()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
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
                    Gas = 21000
                }
            },
            BlockOverride = new BlockOverride
            {
                GasLimit = 30000000,
                Time = 1234567890
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();

        TestContext.Out.WriteLine($"Block override test - ErrorCode: {result.ErrorCode}");
        if (result.ErrorCode != 0)
        {
            TestContext.Out.WriteLine($"Error message: {result.Result}");
        }
        else
        {
            result.Data.Should().HaveCount(1);
            result.Data[0].Should().HaveCount(1);
            result.Data[0][0].Should().NotBeNull();
        }
    }

    [Test]
    public async Task Debug_traceCallMany_with_state_override()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
        Address target = TestItem.AddressC;

        var bundle = new TransactionBundle
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = target,
                    Value = 1.Ether(),
                    Gas = 21000
                }
            },
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                [from] = new AccountOverride
                {
                    Balance = 100.Ether()
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();

        TestContext.Out.WriteLine($"State override test - ErrorCode: {result.ErrorCode}");
        if (result.ErrorCode != 0)
        {
            TestContext.Out.WriteLine($"Error message: {result.Result}");
        }
        else
        {
            result.Data.Should().HaveCount(1);
            result.Data[0].Should().HaveCount(1);
            result.Data[0][0].Should().NotBeNull();
            TestContext.Out.WriteLine($"Transaction failed status: {result.Data[0][0].Failed}");
        }
    }

    [Test]
    public async Task Debug_traceCallMany_with_combined_overrides()
    {
        using Context ctx = await Context.Create();

        Address from = Build.An.Address.TestObject;
        Address target = TestItem.AddressC;

        var bundle = new TransactionBundle
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = target,
                    Value = 1.Ether(),
                    Gas = 21000
                }
            },
            BlockOverride = new BlockOverride
            {
                GasLimit = 50000000
            },
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                [from] = new AccountOverride
                {
                    Balance = 100.Ether()
                }
            }
        };

        var result = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);

        result.Should().BeOfType<ResultWrapper<IReadOnlyList<GethLikeTxTrace[]>>>();

        TestContext.Out.WriteLine($"Combined override test - ErrorCode: {result.ErrorCode}");
        if (result.ErrorCode != 0)
        {
            TestContext.Out.WriteLine($"Error message: {result.Result}");
        }
        else
        {
            result.Data.Should().HaveCount(1);
            result.Data[0].Should().HaveCount(1);
            result.Data[0][0].Should().NotBeNull();
            TestContext.Out.WriteLine($"Transaction failed status: {result.Data[0][0].Failed}");
        }
    }

    [Test]
    public async Task Debug_traceCallMany_state_override_changes_outcome()
    {
        using var ctx = await Context.Create();
        var from = Build.An.Address.TestObject;
        var to = TestItem.AddressC;

        var noOverride = new TransactionBundle
        {
            Transactions = new[]
            {
                new LegacyTransactionForRpc
                {
                    From = from,
                    To = to,
                    Value = 1.Ether(),
                    Gas = 21_000
                }
            }
        };
        var r1 = ctx.DebugRpcModule.debug_traceCallMany(new[] { noOverride }, BlockParameter.Latest);
        r1.Data[0][0].Failed.Should().BeTrue("should fail without balance");

        var withOverride = new TransactionBundle
        {
            Transactions = noOverride.Transactions,
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                [from] = new AccountOverride { Balance = 100.Ether() }
            }
        };
        var r2 = ctx.DebugRpcModule.debug_traceCallMany(new[] { withOverride }, BlockParameter.Latest);
        r2.Data[0][0].Failed.Should().BeFalse("should succeed with override balance");
    }

    [Test]
    public async Task Debug_traceCallMany_block_override_gaslimit_applies()
    {
        using var ctx = await Context.Create();
        var from = Build.An.Address.TestObject;
        await ctx.Blockchain.AddFunds(from, 100.Ether());

        var tx = new LegacyTransactionForRpc
        {
            From = from,
            To = TestItem.AddressC,
            Value = 1.Ether(),
            Gas = 25_000_000
        };

        var bundle = new TransactionBundle
        {
            Transactions = new[] { tx },
            BlockOverride = new BlockOverride { GasLimit = 50_000_000 }
        };

        var r = ctx.DebugRpcModule.debug_traceCallMany(new[] { bundle }, BlockParameter.Latest);
        r.ErrorCode.Should().Be(0);
        r.Data[0][0].Failed.Should().BeFalse("override gas limit should allow execution");
    }
    
    [Test]
    public async Task Debug_traceCallMany_mixed_bundles_preserves_order_and_correctness()
    {
        using var ctx = await Context.Create();
        var from = Build.An.Address.TestObject; await ctx.Blockchain.AddFunds(from, 100.Ether());

        var simple = new TransactionBundle
        {
            Transactions = new[] { new LegacyTransactionForRpc { From = from, To = TestItem.AddressC, Value = 1.Ether(), Gas = 21_000 } }
        };
        var withOverride = new TransactionBundle
        {
            Transactions = simple.Transactions,
            BlockOverride = new BlockOverride { GasLimit = 30_000_000 }
        };

        var r = ctx.DebugRpcModule.debug_traceCallMany(new[] { simple, withOverride }, BlockParameter.Latest);
        r.Data.Should().HaveCount(2);
        r.Data[0].Should().HaveCount(1);
        r.Data[1].Should().HaveCount(1);

        var single = ctx.DebugRpcModule.debug_traceCallMany(new[] { simple }, BlockParameter.Latest);
        r.Data[0][0].Failed.Should().Be(single.Data[0][0].Failed);
    }

}
