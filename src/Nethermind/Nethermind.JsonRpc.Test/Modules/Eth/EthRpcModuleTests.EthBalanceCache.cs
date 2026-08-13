// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private static IBlockchainBridge CreateBalanceCacheBridge()
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        return bridge;
    }

    private static Task<string> GetBalanceRpc(TestRpcBlockchain rpc, Address address) =>
        rpc.TestEthRpc("eth_getBalance", address.Bytes.ToHexString(true), "latest");

    private static void AssertBalanceStateAccessCount(IBlockchainBridge bridge, int expected) =>
        bridge.Received(expected).HasStateForBlock(Arg.Any<BlockHeader>());

    [TestCase(16, 1, TestName = "Enabled cache serves the identical repeat without re-reading state")]
    [TestCase(0, 2, TestName = "Disabled cache (size 0) reads state on every request")]
    public async Task Eth_getBalance_cache_repeated_identical_request(int cacheSize, int expectedStateAccesses)
    {
        IBlockchainBridge bridge = CreateBalanceCacheBridge();
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, cacheSize);

        string first = await GetBalanceRpc(rpc, TestItem.AddressA);
        string second = await GetBalanceRpc(rpc, TestItem.AddressA);

        Assert.That(first, Does.Contain("result"));
        Assert.That(second, Is.EqualTo(first));
        AssertBalanceStateAccessCount(bridge, expectedStateAccesses);
    }

    [Test]
    public async Task Eth_getBalance_cache_different_address_misses()
    {
        IBlockchainBridge bridge = CreateBalanceCacheBridge();
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, 16);

        await GetBalanceRpc(rpc, TestItem.AddressA);
        await GetBalanceRpc(rpc, TestItem.AddressB);

        AssertBalanceStateAccessCount(bridge, 2);
    }

    [Test]
    public async Task Eth_getBalance_cache_new_head_misses()
    {
        IBlockchainBridge bridge = CreateBalanceCacheBridge();
        using TestRpcBlockchain rpc = await CreateEthCallCacheChain(bridge, 16);

        await GetBalanceRpc(rpc, TestItem.AddressA);
        await rpc.AddBlock();
        await GetBalanceRpc(rpc, TestItem.AddressA);

        AssertBalanceStateAccessCount(bridge, 2);
    }
}
