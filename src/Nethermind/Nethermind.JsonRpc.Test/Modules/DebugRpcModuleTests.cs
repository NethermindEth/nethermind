// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Json;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Synchronization.ParallelSync;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
public partial class DebugRpcModuleTests
{
    private class Context : IDisposable
    {
        public IDebugRpcModule DebugRpcModule { get; }
        public TestRpcBlockchain Blockchain { get; }

        private Context(TestRpcBlockchain blockchain, IDebugRpcModule debugRpcModule)
        {
            DebugRpcModule = debugRpcModule;
            Blockchain = blockchain;
        }

        public static async Task<Context> Create(bool isAura = false)
        {
            TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(isAura ? SealEngineType.AuRa : SealEngineType.NethDev).Build();

            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            IReceiptsMigration receiptsMigration = Substitute.For<IReceiptsMigration>();
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();

            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            blockchainBridge.HasStateForRoot(Arg.Any<Hash256>()).Returns(true);

            var factory = new DebugModuleFactory(
                blockchain.WorldStateManager,
                blockchain.DbProvider,
                blockchain.BlockTree,
                blockchain.RpcConfig,
                blockchainBridge,
                new BlocksConfig().SecondsPerSlot,
                blockchain.BlockValidator,
                blockchain.BlockPreprocessorStep,
                NoBlockRewards.Instance,
                blockchain.ReceiptStorage,
                receiptsMigration,
                configProvider,
                blockchain.SpecProvider,
                syncModeSelector,
                new BadBlockStore(blockchain.BlocksDb, 100),
                new FileSystem(),
                blockchain.LogManager
            );

            IDebugRpcModule debugRpcModule = factory.Create();

            return new(blockchain, debugRpcModule);
        }

        public void Dispose() => Blockchain.Dispose();
    }

    [Test]
    public async Task Debug_traceCall_fails_when_not_enough_balance()
    {
        using Context ctx = await Context.Create();

        Address address = Build.An.Address.TestObject;
        UInt256 balance = 100.Ether(), send = balance / 2;

        JsonRpcResponse response = await RpcTest.TestRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = $"{address}", to = $"{TestItem.AddressC}", value = send.ToString("X") }
        );

        response.Should().BeOfType<JsonRpcErrorResponse>()
            .Which.Error?.Message?.Should().Contain("insufficient funds");
    }

    [Test]
    public async Task Debug_traceCall_runs_on_top_of_specified_block()
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain blockchain = ctx.Blockchain;

        Address address = Build.An.Address.TestObject;
        UInt256 balance = 100.Ether();

        await blockchain.AddFunds(address, balance / 2);
        await blockchain.AddFunds(address, balance / 2);
        Hash256 lastBlockHash = blockchain.BlockTree.Head!.Hash!;

        JsonRpcResponse response = await RpcTest.TestRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = $"{address}", to = $"{TestItem.AddressC}", value = balance.ToString("X") },
            $"{lastBlockHash}"
        );

        response.Should().BeOfType<JsonRpcSuccessResponse>();
    }

    [TestCase(
        "Nonce override doesn't cause failure",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"nonce":"0x123"}}"""
    )]
    [TestCase(
        "Uses account balance from state override",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","value":"0x100"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x100"}}"""
    )]
    [TestCase(
        "Executes code from state override",
        """{"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0xc200000000000000000000000000000000000000","input":"0xf8b2cb4f000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033"}}""",
        "00000000000000000000000000000000000000000000003635c9adc5de9f09e5"
    )]
    [TestCase(
        "Executes precompile using overriden address",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}""",
        "000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"
    )]
    public async Task Debug_traceCall_with_state_override(string name, string transactionJson, string stateOverrideJson, string? expectedValue = null)
    {
        var transaction = JsonSerializer.Deserialize<object>(transactionJson);
        var stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        JsonRpcResponse response = await RpcTest.TestRequest(ctx.DebugRpcModule, "debug_traceCall",
            transaction, null, new { stateOverrides = stateOverride }
        );

        GethLikeTxTrace trace = response.Should().BeOfType<JsonRpcSuccessResponse>()
            .Which.Result.Should().BeOfType<GethLikeTxTrace>()
            .Subject;

        if (expectedValue != null)
            Convert.ToHexString(trace.ReturnValue).Should().BeEquivalentTo(expectedValue);
    }

    [TestCase(
        "When balance is overriden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xbe5c953dd0ddb0ce033a98f36c981f1b74d3b33f","value":"0x100"}""",
        """{"0x7f554713be84160fdf0178cc8df86f5aabd33397":{"balance":"0x100"}}"""
    )]
    [TestCase(
        "When address code is overriden",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xf8b2cb4f000000000000000000000000b7705ae4c6f81b66cdb323c65f4e8133690fc099"}""",
        """{"0xc200000000000000000000000000000000000000":{"code":"0x608060405234801561001057600080fd5b506004361061002b5760003560e01c8063f8b2cb4f14610030575b600080fd5b61004a600480360381019061004591906100e4565b610060565b604051610057919061012a565b60405180910390f35b60008173ffffffffffffffffffffffffffffffffffffffff16319050919050565b600080fd5b600073ffffffffffffffffffffffffffffffffffffffff82169050919050565b60006100b182610086565b9050919050565b6100c1816100a6565b81146100cc57600080fd5b50565b6000813590506100de816100b8565b92915050565b6000602082840312156100fa576100f9610081565b5b6000610108848285016100cf565b91505092915050565b6000819050919050565b61012481610111565b82525050565b600060208201905061013f600083018461011b565b9291505056fea2646970667358221220172c443a163d8a43e018c339d1b749c312c94b6de22835953d960985daf228c764736f6c63430008120033"}}"""
    )]
    [TestCase(
        "When precompile address is changed",
        """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000","input":"0xB6E16D27AC5AB427A7F68900AC5559CE272DC6C37C82B3E052246C82244C50E4000000000000000000000000000000000000000000000000000000000000001C7B8B1991EB44757BC688016D27940DF8FB971D7C87F77A6BC4E938E3202C44037E9267B0AEAA82FA765361918F2D8ABD9CDD86E64AA6F2B81D3C4E0B69A7B055"}""",
        """{"0x0000000000000000000000000000000000000001":{"movePrecompileToAddress":"0xc200000000000000000000000000000000000000", "code": "0x"}}"""
    )]
    public async Task Debug_traceCall_with_state_override_does_not_affect_other_calls(string name, string transactionJson, string stateOverrideJson)
    {
        var transaction = JsonSerializer.Deserialize<object>(transactionJson);
        var stateOverride = JsonSerializer.Deserialize<object>(stateOverrideJson);

        using Context ctx = await Context.Create();

        var resultOverrideBefore = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, null, new
        {
            stateOverrides = stateOverride,
            enableMemory = false,
            disableStorage = true,
            disableStack = true,
            tracer = "callTracer",
            tracerConfig = new { withLog = false }
        });

        var resultNoOverride = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, null, new
        {
            // configuration to minimize number of fields being compared
            enableMemory = false,
            disableStorage = true,
            disableStack = true,
            tracer = "callTracer",
            tracerConfig = new { withLog = false }
        });

        var resultOverrideAfter = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall", transaction, null, new
        {
            stateOverrides = stateOverride,
            enableMemory = false,
            disableStorage = true,
            disableStack = true,
            tracer = "callTracer",
            tracerConfig = new { withLog = false }
        });

        using (new AssertionScope())
        {
            JToken.Parse(resultOverrideBefore).Should().BeEquivalentTo(resultOverrideAfter);
            JToken.Parse(resultNoOverride).Should().NotBeEquivalentTo(resultOverrideAfter);
        }
    }
}
