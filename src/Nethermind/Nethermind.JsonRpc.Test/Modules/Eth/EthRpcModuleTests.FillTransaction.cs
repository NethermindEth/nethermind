// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Json;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Eth_fillTransaction_fills_nonce_for_legacy_tx()
    {
        using Context ctx = await Context.Create();
        // TestItem.AddressA has nonce 3 at head block
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result.Should().ContainSubtree("{\"result\":{\"tx\":{\"nonce\":\"0x3\"}}}");
    }

    [Test]
    public async Task Eth_fillTransaction_keeps_provided_nonce()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"nonce\":\"0xa\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result.Should().ContainSubtree("{\"result\":{\"tx\":{\"nonce\":\"0xa\"}}}");
    }

    [Test]
    public async Task Eth_fillTransaction_fills_gas_when_not_provided()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"gasPrice\":\"0x1\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        // Simple ETH transfer = 21000 = 0x5208
        result.Should().ContainSubtree("{\"result\":{\"tx\":{\"gas\":\"0x5208\"}}}");
    }

    [Test]
    public async Task Eth_fillTransaction_fills_gasPrice_for_legacy_tx_when_not_provided()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        string? gasPrice = result.SelectToken("$.result.tx.gasPrice")?.Value<string>();
        gasPrice.Should().NotBeNull();
    }

    [Test]
    public async Task Eth_fillTransaction_fills_maxFeePerGas_for_eip1559_tx_when_not_provided()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"type\":\"0x2\",\"gas\":\"0x5208\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["maxFeePerGas"].Should().NotBeNull();
        result["result"]!["tx"]!["maxPriorityFeePerGas"].Should().NotBeNull();
    }

    [Test]
    public async Task Eth_fillTransaction_fills_maxFeePerGas_when_only_maxPriorityFeePerGas_provided()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        // Providing maxPriorityFeePerGas but not maxFeePerGas — maxFeePerGas must be filled and >= maxPriorityFeePerGas
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"type\":\"0x2\",\"gas\":\"0x5208\",\"maxPriorityFeePerGas\":\"0x3b9aca00\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["tx"]!["maxFeePerGas"].Should().NotBeNull();
        long maxFee = Convert.ToInt64(result["result"]!["tx"]!["maxFeePerGas"]!.Value<string>(), 16);
        long maxPriorityFee = Convert.ToInt64(result["result"]!["tx"]!["maxPriorityFeePerGas"]!.Value<string>(), 16);
        maxFee.Should().BeGreaterThanOrEqualTo(maxPriorityFee);
    }

    [Test]
    public async Task Eth_fillTransaction_returns_raw_rlp_and_tx_object()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"nonce\":\"0x3\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"value\":\"0x0\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"]!["raw"].Should().NotBeNull();
        result["result"]!["tx"].Should().NotBeNull();

        string? raw = result["result"]!["raw"]!.Value<string>();
        raw.Should().StartWith("0x");
        raw!.Length.Should().BeGreaterThan(2);
    }

    [Test]
    public async Task Eth_fillTransaction_fills_all_missing_fields()
    {
        using Context ctx = await Context.Create();
        TransactionForRpc tx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\"}}");
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", tx);

        JToken result = JToken.Parse(serialized);
        result["result"].Should().NotBeNull();
        result["result"]!["tx"]!["nonce"].Should().NotBeNull("nonce should be filled");
        result["result"]!["tx"]!["gas"].Should().NotBeNull("gas should be estimated");
        result["result"]!["raw"].Should().NotBeNull("raw RLP should be returned");
    }
}
