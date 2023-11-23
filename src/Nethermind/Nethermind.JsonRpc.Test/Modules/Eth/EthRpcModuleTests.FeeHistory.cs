// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [TestCase(1, "latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x2da282a8\",\"0x27ee3253\"],\"gasUsedRatio\":[0.0],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    [TestCase(1, "pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x2da282a8\",\"0x27ee3253\"],\"gasUsedRatio\":[0.0],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    [TestCase(2, "0x01", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x3b9aca00\",\"0x342770c0\"],\"gasUsedRatio\":[0.0,0.0],\"oldestBlock\":\"0x0\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"],[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    [TestCase(2, "earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x3b9aca00\"],\"gasUsedRatio\":[0.0],\"oldestBlock\":\"0x0\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x0\"]]},\"id\":67}")]
    public async Task Eth_feeHistory(long blockCount, string blockParameter, string expected)
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        string serialized = await ctx.Test.TestEthRpc("eth_feeHistory", blockCount.ToString(), blockParameter, "[0,10.5,20,60,90]");
        serialized.Should().Be(expected);
    }
}
