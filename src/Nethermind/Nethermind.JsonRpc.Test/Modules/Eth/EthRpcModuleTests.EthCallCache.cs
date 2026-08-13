// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private static IBlockchainBridge CreateEthCallCacheBridge(CallOutput callOutput)
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        bridge.Call(Arg.Any<BlockHeader>(), Arg.Any<Transaction>(), Arg.Any<Dictionary<Address, AccountOverride>>(), Arg.Any<UInt256?>(), Arg.Any<BlockOverride>(), Arg.Any<CancellationToken>())
            .Returns(callOutput);
        return bridge;
    }

    private static async Task<TestRpcBlockchain> CreateEthCallCacheChain(IBlockchainBridge bridge, int cacheSize) =>
        await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge)
            .WithConfig(new JsonRpcConfig { EthCallCacheSize = cacheSize })
            .Build();

    private static void AssertBridgeCallCount(IBlockchainBridge bridge, int expected) =>
        bridge.Received(expected).Call(Arg.Any<BlockHeader>(), Arg.Any<Transaction>(), Arg.Any<Dictionary<Address, AccountOverride>>(), Arg.Any<UInt256?>(), Arg.Any<BlockOverride>(), Arg.Any<CancellationToken>());

    private static TransactionForRpc CreateEthCallCacheCall(TestRpcBlockchain rpc, string data = "0x1234") =>
        rpc.JsonSerializer.Deserialize<TransactionForRpc>($"{{\"to\": \"{TestItem.AddressA}\", \"data\": \"{data}\"}}");

    [TestCase(16, 1, TestName = "Enabled cache serves the identical repeat without re-executing")]
    [TestCase(0, 2, TestName = "Disabled cache (size 0) executes every call")]
    public async Task Eth_call_cache_repeated_identical_call(int cacheSize, int expectedExecutions)
    {
        IBlockchainBridge bridge = CreateEthCallCacheBridge(new CallOutput { OutputData = [1, 2, 3] });
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, cacheSize);
        TransactionForRpc call = CreateEthCallCacheCall(rpc);

        string first = await rpc.TestEthRpc("eth_call", call, "latest");
        string second = await rpc.TestEthRpc("eth_call", call, "latest");

        Assert.That(first, Does.Contain("0x010203"));
        Assert.That(second, Is.EqualTo(first));
        AssertBridgeCallCount(bridge, expectedExecutions);
    }

    [Test]
    public async Task Eth_call_cache_different_data_misses()
    {
        IBlockchainBridge bridge = CreateEthCallCacheBridge(new CallOutput { OutputData = [1, 2, 3] });
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, 16);

        await rpc.TestEthRpc("eth_call", CreateEthCallCacheCall(rpc, "0x1234"), "latest");
        await rpc.TestEthRpc("eth_call", CreateEthCallCacheCall(rpc, "0x5678"), "latest");

        AssertBridgeCallCount(bridge, 2);
    }

    [Test]
    public async Task Eth_call_cache_state_override_bypasses_cache()
    {
        IBlockchainBridge bridge = CreateEthCallCacheBridge(new CallOutput { OutputData = [1, 2, 3] });
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, 16);
        TransactionForRpc call = CreateEthCallCacheCall(rpc);
        Dictionary<Address, AccountOverride> stateOverride = new() { { TestItem.AddressB, new AccountOverride { Balance = (UInt256)1 } } };

        await rpc.TestEthRpc("eth_call", call, "latest", stateOverride);
        await rpc.TestEthRpc("eth_call", call, "latest", stateOverride);

        AssertBridgeCallCount(bridge, 2);
    }

    [Test]
    public async Task Eth_call_cache_new_head_misses()
    {
        IBlockchainBridge bridge = CreateEthCallCacheBridge(new CallOutput { OutputData = [1, 2, 3] });
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, 16);
        TransactionForRpc call = CreateEthCallCacheCall(rpc);

        await rpc.TestEthRpc("eth_call", call, "latest");
        await rpc.AddBlock();
        await rpc.TestEthRpc("eth_call", call, "latest");

        AssertBridgeCallCount(bridge, 2);
    }

    [Test]
    public async Task Eth_call_cache_pre_berlin_block_bypasses_cache()
    {
        IBlockchainBridge bridge = CreateEthCallCacheBridge(new CallOutput { OutputData = [1, 2, 3] });
        using TestRpcBlockchain rpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge)
            .WithConfig(new JsonRpcConfig { EthCallCacheSize = 16 })
            .Build(new TestSpecProvider(Istanbul.Instance));
        TransactionForRpc call = CreateEthCallCacheCall(rpc);

        await rpc.TestEthRpc("eth_call", call, "latest");
        await rpc.TestEthRpc("eth_call", call, "latest");

        AssertBridgeCallCount(bridge, 2);
    }

    [TestCase(true, 1, TestName = "Deterministic revert outcome is cached")]
    [TestCase(false, 2, TestName = "Non-revert execution error is not cached")]
    public async Task Eth_call_cache_failed_execution(bool executionReverted, int expectedExecutions)
    {
        IBlockchainBridge bridge = CreateEthCallCacheBridge(new CallOutput
        {
            Error = "execution failed",
            ExecutionReverted = executionReverted,
            OutputData = [0xde, 0xad],
        });
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, 16);
        TransactionForRpc call = CreateEthCallCacheCall(rpc);

        string first = await rpc.TestEthRpc("eth_call", call, "latest");
        string second = await rpc.TestEthRpc("eth_call", call, "latest");

        Assert.That(first, Does.Contain("error"));
        Assert.That(second, Is.EqualTo(first));
        AssertBridgeCallCount(bridge, expectedExecutions);
    }
}
