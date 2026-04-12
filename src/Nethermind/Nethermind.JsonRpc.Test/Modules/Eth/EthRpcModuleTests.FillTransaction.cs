// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Eth_fillTransaction_returns_raw_rlp_and_tx_object()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0x3\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"value\":\"0x0\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["raw"].Should().NotBeNull();
        result["result"]!["tx"].Should().NotBeNull();
        result["result"]!["raw"]!.Value<string>().Should().StartWith("0x");
    }

    [Test]
    public async Task Eth_fillTransaction_fills_nonce_for_legacy_tx()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"gasPrice\":\"0x1\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["nonce"]!.Value<string>().Should().Be("0x3");
    }

    [Test]
    public async Task Eth_fillTransaction_fills_nonce_for_eip1559_tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"type\":\"0x2\",\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"maxPriorityFeePerGas\":\"0x1\",\"maxFeePerGas\":\"0x100\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["nonce"].Should().NotBeNull();
    }

    [Test]
    public async Task Eth_fillTransaction_keeps_provided_nonce()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0xa\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["nonce"]!.Value<string>().Should().Be("0xa");
    }

    [Test]
    public async Task Eth_fillTransaction_fills_gas_when_not_provided()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0x0\",\"gasPrice\":\"0x1\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["gas"]!.Value<string>().Should().Be("0x5208");
    }

    [Test]
    public async Task Eth_fillTransaction_keeps_provided_gas()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0x0\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["gas"]!.Value<string>().Should().Be("0x5208");
    }

    [Test]
    public async Task Eth_fillTransaction_fills_gasPrice_for_legacy_tx()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0x0\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["gasPrice"].Should().NotBeNull();
    }

    [Test]
    public async Task Eth_fillTransaction_fills_maxFeePerGas_for_eip1559_tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"type\":\"0x2\",\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0x0\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["maxFeePerGas"].Should().NotBeNull();
        result["result"]!["tx"]!["maxPriorityFeePerGas"].Should().NotBeNull();
    }

    [Test]
    public async Task Eth_fillTransaction_fills_maxFeePerGas_when_only_priority_provided()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"type\":\"0x2\",\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0x0\",\"gas\":\"0x5208\",\"maxPriorityFeePerGas\":\"0x1\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        string? maxFee = result["result"]!["tx"]!["maxFeePerGas"]!.Value<string>();
        string? tip = result["result"]!["tx"]!["maxPriorityFeePerGas"]!.Value<string>();
        maxFee.Should().NotBeNull();
        tip.Should().NotBeNull();
        Convert.ToUInt64(maxFee, 16).Should().BeGreaterThanOrEqualTo(Convert.ToUInt64(tip, 16));
    }

    [Test]
    public async Task Eth_fillTransaction_keeps_provided_fee_fields()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"type\":\"0x2\",\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"," +
            $"\"nonce\":\"0x0\",\"gas\":\"0x5208\"," +
            $"\"maxPriorityFeePerGas\":\"0x2\",\"maxFeePerGas\":\"0x3\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["maxFeePerGas"]!.Value<string>().Should().Be("0x3");
        result["result"]!["tx"]!["maxPriorityFeePerGas"]!.Value<string>().Should().Be("0x2");
    }

    [Test]
    public async Task Eth_fillTransaction_fails_when_gas_estimation_fails()
    {
        using Context ctx = await Context.Create();
        // Contract creation with no data — eth_estimateGas rejects this with validateUserInput: true
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"gasPrice\":\"0x1\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["error"].Should().NotBeNull();
    }
}
